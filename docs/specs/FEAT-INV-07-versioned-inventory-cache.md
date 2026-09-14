# [FEAT-INV-07] Versioned inventory cache + background fetch + instant search

- **Feature ID:** `FEAT-INV-07`
- **Track:** `net` (+ `ui`)
- **Status:** `⏸️ Pending`
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

**Cache SLNG's own neutral `InventoryEntry` DTOs, not LibreMetaverse objects.** The cache sits
*in front of* `GridSession.FetchInventoryChildrenAsync`, which already returns
`IReadOnlyList<InventoryEntry>`. Nothing has to be pushed back into LibreMetaverse's store for
browsing or searching to work — a real fetch still populates it, which is what the wear/COF
paths need.

**Trap, already verified:** do **not** serialize via LibreMetaverse's own OSD helpers.
`InventoryFolder.GetOSD()` writes `item_id` / `type` and omits `descendents` and `owner_id`,
while `InventoryFolder.FromOSD()` reads `category_id` / `folder_id` / `type_default` /
`descendents` / `owner_id` (`InventoryBase.cs:857-873`, `:914-926`). **That pair does not round
trip** — `FromOSD(GetOSD(f))` loses the type and throws on the missing keys. Serializing our own
record sidesteps it entirely.

### Layering

`SLNG.Net` owns the cache and does the file IO; it is handed a directory by `app`, the same
shape as the existing asset cache (`Boot.cs:957` passes
`ProjectSettings.GlobalizePath("user://cache/assets")`). Suggested location:
`user://cache/inventory/<agent-id>.json.gz`. No `using Godot;` crosses into `src/`, and no
LibreMetaverse type crosses out of it.

### Cache invalidation

The version comparison is the whole contract:

| cached version | skeleton version | action |
|---|---|---|
| present, equal | — | serve from cache, no network |
| present, different | — | refetch that folder, rewrite its entry |
| absent | — | fetch as today |
| folder gone from skeleton | — | drop the cache entry |

The version for a freshly fetched folder comes from the CAPS reply, which LibreMetaverse already
records on the stored folder (`InventoryManager.cs:427-428`,
`fetchedFolder.Version = res["version"]`). `GridSession` reads it back off the store node after
a fetch and stores it with the cached contents.

**Correctness rule:** a folder whose version is unknown after a fetch must **not** be written to
the cache. An entry with no version can never be validated, and serving it would be a permanent
stale read. Skipping it costs one refetch next session; writing it risks showing a wrong
inventory forever.

## Phases

### Phase 1 — versioned disk cache (the win)

Persist per-folder `InventoryEntry` lists plus their version; on login, serve matching folders
from the cache instead of the network. Browsing becomes instant for unchanged folders, and the
existing search crawl gets dramatically cheaper because most folders are already local.

### Phase 2 — background fetch

After login, walk folders whose cached version is stale or missing, in batches (the viewer's
10-at-a-time / 12-outstanding is a sane starting point) and off the critical path. Must respect
the existing caps rate limiter — the log already shows `Caps rate limiter queue full` under
normal load, so this cannot be a flood.

### Phase 3 — in-memory search

Replace `InventoryPanel`'s live depth-first crawl with a filter over the loaded set. Drop
`MinSearchCrawlChars` / `MaxSearchFolderLoads` — with the tree local, the cap that made search
incomplete is no longer needed. Keep a fetch-on-demand fallback for a folder that is still
unknown, exactly as `llinventoryfilter.cpp:202-216` does.

## Acceptance Criteria

- [ ] Second login to the same account serves unchanged folders from disk — no CAPS request per
      folder, provable from the log.
- [ ] A folder changed by another viewer between sessions is refetched, and shows the new
      contents (version mismatch path).
- [ ] A folder with no known version is never served from cache.
- [ ] Cache is per account and per grid — logging into a different account or grid never reads
      another's file.
- [ ] A corrupt, truncated or unreadable cache file degrades to today's behaviour rather than
      breaking login.
- [ ] Phase 3: searching a large inventory returns matches from folders never expanded by hand,
      with no per-keystroke network traffic.
- [ ] Unit tests: version-match / mismatch / missing-version decisions, round-trip of the cache
      format, corrupt-file handling.

## Technical Specs & Affected Files

- `src/SLNG.Net/InventoryCache.cs` — new. Load/save the gzipped per-folder entries, version
  comparison, corruption handling.
- `src/SLNG.Net/GridSession.cs` — read the skeleton versions off the store; consult the cache in
  `FetchInventoryChildrenAsync`; record the post-fetch version; expose a save hook.
- `app/scripts/Boot.cs` — supply the cache directory; save on `NotificationWMCloseRequest`
  (`Boot.cs:2068-2075`) **and** on explicit logout (`Boot.cs:524`), since a crash-free quit is
  not the only way a session ends.
- `app/scripts/UI/InventoryPanel.cs` — Phase 3: filter locally, drop the crawl caps.
- `tests/SLNG.Net.Tests/InventoryCacheTests.cs` — new.

## Open questions

- **Library folders.** The viewer caches the Library separately and skips it for a second
  instance (`llappviewer.cpp:6180-6187`). The Library is identical for everyone and effectively
  immutable — cache it once, or skip it in Phase 1?
- **Cache size.** 30 000 items of `InventoryEntry` gzipped is likely a few MB. Worth measuring
  before choosing a format; JSON + gzip is the simple default.

## Sub-tasks / Progress

- [ ] Phase 1: cache format + load/save + version gate
- [ ] Phase 1: wire into `FetchInventoryChildrenAsync`, save on quit and on logout
- [ ] Phase 1: tests
- [ ] Phase 2: background fetch, rate-limiter aware
- [ ] Phase 3: local search, drop the crawl caps
- [ ] In-world verification: relog and confirm the folder fetches disappear from the log
