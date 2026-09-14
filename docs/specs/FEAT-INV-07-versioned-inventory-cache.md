# [FEAT-INV-07] Versioned inventory cache + background fetch + instant search

- **Feature ID:** `FEAT-INV-07`
- **Track:** `net` (+ `ui`)
- **Status:** `🧪 Review` — Phase 1 confirmed in-world (v0.22.147-alpha); Phases 2 and 3 implemented (v0.22.148 / v0.22.149-alpha), awaiting in-world verification.
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Asked 2026-09-14: *„wie bekommt es Firestorm hin, dass das Durchsuchen vom Inventar schnell
geht?"* — followed by *„imho ist das auch wichtig"*.

**The answer is that the reference viewer does not search fast; it searches locally.** By the
time the search box has focus, the entire inventory is already in RAM. Searching is a string
match over a map, with no network involved at all.

SLNG does the opposite. [`InventoryPanel`](file:///E:/Git/SLNG/app/scripts/UI/InventoryPanel.cs)
crawls the tree depth-first **while the user types**, one CAPS round trip per folder, capped at
`MaxSearchFolderLoads = 800`. Every search pays full network latency, and on a large inventory
the cap means the search silently does not see everything.

This task moves SLNG onto the same model. It is deliberately staged: **Phase 1 alone removes
most of the cost**, and each phase is independently shippable.

## How the reference viewer actually does it

Verified in `scratch/slviewer` (the Linden viewer — Firestorm is a fork of it and inherits this
machinery unchanged).

### 1. Login already delivers every folder, with a version

The login response's `inventory-skeleton` lists **every** folder with `parent_id`, `name`,
`type_default` and a **`version`** integer. The folder tree is therefore complete before the
first fetch. `llinventoryfilter.cpp:206-211` treats a missing folder as a can't-happen case:

> `// Shouldn't happen? Server provides full list of folders on startup`

**SLNG gets this for free already and ignores it.** LibreMetaverse parses the skeleton with
versions intact (`LoginResponseData.cs:999-1018`) and pushes every folder into its store at
login (`InventoryManager.Handlers.cs:339-350`). `InventoryFolder.Version` and
`DescendentCount` are public, settable, and reachable from the pinned 3.1.3 package — confirmed
by reflection, not just read in the newer vendored checkout.

### 2. A gzipped disk cache holds the contents between sessions

One file per account (`<agent-id>.inv.llsd.gz`), written at shutdown
(`llappviewer.cpp:6176-6188`), loaded at login by `LLInventoryModel::loadSkeleton`
(`llinventorymodel.cpp:2807+`).

The load is where the win is (`:2929-2946`): for each cached folder, compare the **cached
version** against the **skeleton version**.

- equal → the cached contents are trusted as-is, **no fetch**
- different → `tcat->setVersion(NO_VERSION)`, and only *that* folder is refetched

A returning user with 30 000 items refetches the handful of folders that actually changed.

### 3. A background fetch fills the rest before the user asks

`LLInventoryModelBackgroundFetch` walks everything still at `VERSION_UNKNOWN` in batches of
**10 folders**, with at most **12 requests outstanding** (`:1092-1093`), while the user is doing
something else.

### 4. Search is then a pure in-memory filter

`LLInventoryFilter` walks the loaded model. It kicks a fetch only for a folder still at
`VERSION_UNKNOWN` (`llinventoryfilter.cpp:202-216`) — a fallback, not the normal path.

## Design for SLNG

**Correction made while building this: LibreMetaverse already implements Phase 1.** The spec
originally proposed caching SLNG's own neutral `InventoryEntry` records in a separate file. That
would have been a parallel implementation of something the library already does — and does the
same way the reference viewer does.

`Inventory.SaveToDisk` / `RestoreFromDisk` (`InventoryCache.cs`) persist the whole store with
MessagePack, and the restore is **version-aware**: for every cached folder it compares the cached
version against the version the login skeleton just reported and sets
`InventoryNode.NeedsUpdate` accordingly, restoring contents only for folders that match and
dropping items whose parent is dirty (`InventoryCache.cs:195-250`). That is
`LLInventoryModel::loadSkeleton` in C#.

This was **verified against the pinned 3.1.3 package** by round-tripping a real store, not read
from the newer vendored checkout — the distinction has cost this project real debugging before.
`tests/SLNG.Net.Tests/InventoryCacheTests.cs` pins the behaviour, because it is now a dependency:
if a package bump changed it, the cache would silently serve a **stale** inventory, which is far
worse than a slow one.

Using it also warms LibreMetaverse's store itself, so every consumer benefits, not only the
browser.

### What SLNG actually adds

1. `GridSession.OpenInventoryCache(directory)` — after login, once the skeleton is in the store,
   restore `<agent-id>.inv.cache`. Keyed by agent id so two accounts, or the same name on two
   grids, never read each other's inventory. Ordering matters: with no skeleton there are no
   server versions to compare against and every cached folder is discarded as orphaned.
2. `FetchInventoryChildrenAsync` — serve from `store.GetContents(folder)` when the node's
   `NeedsUpdate` is false, instead of calling `FolderContentsAsync`. `NeedsUpdate` starts **true**
   for every skeleton folder and is cleared by exactly two things: a successful fetch this
   session, or a cache restore at a matching version. That default is what makes the whole scheme
   safe — the network is only skipped when something actively proved the contents good.
3. `GridSession.SaveInventoryCache()` — on quit **and** on logout.

**Side benefit:** re-expanding a folder is now free. The old code refetched on every expand even
though the answer was already in the store.

### Layering

`SLNG.Net` owns the path and the calls; `app` hands it a real directory
(`ProjectSettings.GlobalizePath("user://cache/inventory")`), the same shape as the existing asset
cache. No `using Godot;` in `src/`, no LibreMetaverse type out of it.

## Phases

### Phase 1 — versioned disk cache (the win) — ✅ done, v0.22.147-alpha

Restore on login, serve clean folders from the store, save on quit and logout. Browsing is
instant for unchanged folders, and the existing search crawl gets dramatically cheaper because
most folders are already local.

### Phase 2 — background fetch — 🧪 implemented v0.22.148-alpha

`GridSession.PrefetchInventoryAsync` walks everything still flagged `NeedsUpdate` and fetches
it, so a folder the user never opened by hand is local too — and a *first* login, where the
cache is empty, benefits as well.

- **Ten folders per request.** The `FetchInventoryDescendents2` payload takes a list of folders
  (`InventoryManager.cs:377`, public on the pinned 3.1.3), so this is one POST per ten, not ten
  POSTs. Same batch size as the reference viewer.
- **One request at a time, 250 ms apart.** The viewer keeps twelve in flight; SLNG deliberately
  does not, because this shares a caps budget with texture and material fetching and the live log
  already shows the sim's rate limiter filling up under normal load. A background fill that slows
  the world down has missed the point.
- **Starts 30 s after login**, not immediately — the first seconds in a region are the busiest
  the caps budget ever gets.
- **Re-walks the tree each batch**, because fetching a folder is how its subfolders become
  visible in the first place.
- **Refused folders are remembered**, so a folder whose flag the grid never clears cannot spin
  the walk forever.
- **Agent inventory only.** The Library is shared, immutable and large, and nobody searches it
  for their own things.
- **Cancelled on logout and quit**, so the walk never POSTs against a simulator this client has
  left.

### Phase 3 — search over the local tree — 🧪 implemented v0.22.149-alpha

**The tree stays.** Asked for live, and it is what the reference viewers do — a search is a lens
over the tree, not a different screen: *„der Baum bleibt; wenn man etwas markiert hat und die
Suche löscht, fliegt der Filter weg, aber man bleibt auf dem Eintrag stehen."* A flat results
list was considered and rejected on that basis.

Two changes:

1. **The 800-folder cap no longer counts free reads.** It exists to bound *network* requests, and
   once the background fill has a folder locally there is no request to bound — reading it is a
   dictionary lookup. Counting those against the budget is what made search stop early on a large
   inventory even when the whole thing was already in memory. `GridSession.IsFolderLocal` exposes
   the same `NeedsUpdate` flag the cache already turns on. The cap still applies to folders that
   really would hit the grid, which is the case on a first login before the fill finishes.
2. **The selection survives a filter pass.** The panel keeps its own handle on the picked row
   (Godot's selection does not reliably survive a row being hidden and shown again), and after
   every filter pass re-selects it, expands its ancestors and scrolls it back into view. The
   scroll is deferred, because the Tree has not laid out the rows it just un-hid yet and scrolling
   to a stale position lands in the wrong place.

## Acceptance Criteria

- [x] Second login to the same account serves unchanged folders from disk — no CAPS request per
      folder, provable from the log.
- [x] A folder changed by another viewer between sessions is refetched, and shows the new
      contents (version mismatch path).
- [x] A folder with no known version is never served from cache.
- [x] Cache is per account and per grid — logging into a different account or grid never reads
      another's file.
- [x] A corrupt, truncated or unreadable cache file degrades to today's behaviour rather than
      breaking login.
- [x] Phase 3: searching a large inventory returns matches from folders never expanded by hand,
      with no per-keystroke network traffic.
- [ ] Phase 3: clearing the search box leaves the selected row selected and in view.
- [ ] Unit tests: version-match / mismatch / missing-version decisions, round-trip of the cache
      format, corrupt-file handling.

## Technical Specs & Affected Files

- `src/SLNG.Net/GridSession.cs` — `OpenInventoryCache` / `SaveInventoryCache`; serve from the
  store in `FetchInventoryChildrenAsync` when `NeedsUpdate` is false.
- `app/scripts/Boot.cs` — supply the cache directory; save on `NotificationWMCloseRequest`
  (`Boot.cs:2068-2075`) **and** on explicit logout (`Boot.cs:524`), since a crash-free quit is
  not the only way a session ends.
- `app/scripts/UI/InventoryPanel.cs` — Phase 3: filter locally, drop the crawl caps.
- `tests/SLNG.Net.Tests/InventoryCacheTests.cs` — pins LibreMetaverse's version-comparison
  behaviour, which the cache now depends on.

## Open questions

- **Library folders.** The viewer caches the Library separately and skips it for a second
  instance (`llappviewer.cpp:6180-6187`). The Library is identical for everyone and effectively
  immutable — cache it once, or skip it in Phase 1?
- **Cache size.** Resolved by using LibreMetaverse's MessagePack format — ~870 bytes for a
  4-node store in the round-trip test. Worth re-measuring on a real 30 000-item inventory.

## Sub-tasks / Progress

- [x] Phase 1: restore on login, serve on `NeedsUpdate == false`, save on quit + logout
- [x] Phase 1: verified the pinned LMV does the version comparison (not assumed from source)
- [x] Phase 1: tests (5)
- [x] Phase 2: background fetch, batched 10/request, one at a time, cancelled on logout
- [x] Phase 3: cap only counts network loads; selection survives clearing the filter
- [x] In-world verification: relog and confirm the folder fetches disappear from the log
