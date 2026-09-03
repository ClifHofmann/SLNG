# [BUG-INV-01] Inventory: worn-state not visible, detach-from-Worn broken, slow load with no progress

- **Feature ID:** `BUG-INV-01`
- **Track:** `ui` / `net`
- **Status:** `🧪 Review` — all four symptoms addressed; the `v0.20.57` three need a live check.
- **Priority:** **Medium** — one functional break (detach from the Worn tab does nothing),
  two UX gaps.
- **Owner:** `claude`
- **Depends on:** `M5-1` (Inventory Browser), `FEAT-UI-16` (Worn tab), `FEAT-INV-03` (COF
  write-back on detach)
- **Reported:** live, three symptoms in one report.

## Symptoms

1. **In the "Inventar" tab you cannot tell what is already worn.** No worn marker on items in
   the main tree that you are currently wearing.
2. **In the "Angezogen" (Worn) tab, "Ablegen" does nothing.** Item stays worn, no error.
3. **The inventory loads very slowly, and there is no indication that a fetch is still in
   progress** — no spinner, no "loading…", folders / names just appear late.
4. **Search only finds folders that were already expanded by hand.** Typing "DOUX" finds the
   `DOUX` *folder* (it happened to be loaded) but shows nothing inside it; you have to open it
   manually for its contents to appear. Nested folders you never touched are invisible to the
   search entirely.

## Where it lives

`app/scripts/UI/InventoryPanel.cs` (the tabbed panel: "Inventar" / "Angezogen" / "Outfits"),
against `GridSession`'s inventory + worn-item API.

## Fixed so far

### `v0.20.33`'s durable COF-link delete stripped the avatar's bake — safety gate added (`v0.20.36-alpha`)
**Live regression, 2026-09-03.** `v0.20.33` changed `CleanUpCurrentOutfit` from `MoveItem → Trash`
(which HTTP-400s on SL, so it was a no-op) to a real `RemoveItemsAsync` AIS delete. Session logs:
self bake `8=784033ee 9=9965f08e 10=e1baf1d1 11=1e70f9f4 …` resolved cleanly every session
09:50–12:13; at 12:13 (v0.20.34) the user clicked **Outfit aufräumen** →
`[OutfitCleanup] deleted … unworn-attachment=2 … (via RemoveItems, AIS=True)`; the very next
session (12:26) had **no `[SelfBake]` line at all** and every BoM face `UNRESOLVED` — grey avatar.
The 12:13 log also showed `uncached=2` (two COF links whose target nodes weren't in the store)
and a rate-limited region (`Caps rate limiter queue full`): the COF was still loading, so links
read as "dead"/"unworn" that weren't, the AIS delete made it stick, and the forced server
re-composite came back empty.

**Fix:** `CleanUpCurrentOutfit` now refuses to touch the COF unless **both** the store and the
scene are demonstrably loaded — `storeReady` = every link with a non-Zero target has that target
node in the store (`linkUnresolved == 0`), `sceneReady` = at least one attachment visible in the
scene (you always wear a body). Otherwise it returns `OutfitCleanupResult { Deferred = true }`
and the panel shows *"Inventar/Szene lädt noch — bitte gleich nochmal versuchen."* The 12:13
state (`uncached=2` → `linkUnresolved ≥ 2`) would now defer. `dead` / `target-in-trash` /
`duplicate` / `unworn-attachment` categories are unchanged **once the gate passes**.

Same trip, the two other COF-link removers that still did `MoveItem → Trash` (→ 400 on SL, so
detach never persisted — "Ablegen geht nicht persistent", `warn: Move item … Bad Request` spam
in the 12:13 log) were switched to `RemoveItemsAsync`: `DetachItemAsync`'s stale-link cleanup and
`RemoveOutfitLinksForItems` (the Detach-All path). Both act on an **explicit** item the user
chose to take off, so a durable delete is well-targeted there — no gate needed.

**Recovery for an already-broken avatar:** `Ctrl+Alt+R` (SLNG rebake → `RequestSetAppearance(forceRebake: true)`
on SSB), or relog + wait, or re-wear the outfit in Firestorm. The inventory items were never
touched — only COF *links*.

### Search now crawls the subtree instead of filtering only what's loaded (`v0.20.35-alpha`)
`OnSearchTextChanged` → `EnsureFoldersLoadedForSearch` walked the tree **once** and kicked off a
fetch only for folder rows that already existed as `TreeItem`s — one level deep. The async
`Populate` that lands a folder's children never re-triggered the walk, so the crawl stopped
dead at the first not-yet-expanded level. `FilterTree` then only ever saw hand-expanded folders,
and a folder whose *name* matched still showed empty (its children were `Visible = false` because
they didn't match). Now: while a query ≥ `MinSearchCrawlChars` (2) is active, each `Populate`
calls `ContinueSearchCrawl(item)` to fetch that folder's subfolders — one level per `Populate`,
so the crawl follows the tree down as it materialises, bounded by `MaxSearchFolderLoads` (800)
and re-filtering after every folder lands (progressive reveal). `FilterTree` gained an
`ancestorMatched` flag so a name-matched folder reveals its **whole** subtree. A `_pendingFetches`
counter drives a real "Suche läuft… (N Ordner)" / "Lädt… (N Ordner)" status (symptom 3's progress
gap for the search path). Godot-`TreeItem`-bound, no unit-test surface. **Not yet re-verified
in-world.**

### "Ablegen" in the Worn tab (and Wear/Detach in the main tree) did nothing (`v0.20.34-alpha`)
`DetachWornAsync`, `DetachAndRefreshAsync`, `AttachAndRefreshAsync` all did
`await …Async().ConfigureAwait(false)` and then `Callable.From(lambda).CallDeferred()` from the
resulting **worker thread** — Godot's main thread has no `SynchronizationContext`
(`UserProfileWindow.cs:898`, `WorldMapWindow.cs:23`), so this is the `BUG-RENDER-01` anti-pattern:
`Callable.From(lambda).CallDeferred()` off the main thread can crash or **silently never
dispatch**. The detach/attach packet itself is sent *before* that line, so the server action may
have been happening all along — but the status text and the list/tree refresh never ran, so it
"did nothing" from the user's side. Converted all three to `CallDeferred(nameof(Finish…))` on a
named method (status computed off-thread into a field), matching `RefreshWornIfVisible`'s existing
safe pattern in the same file. **Not yet re-verified in-world** on a genuinely-worn attachment.

### "Outfit aufräumen" — trashed COF links reappeared (`v0.20.33-alpha`)
User: the Heol Star bracelet/earrings show as worn "(nicht aktiv)" but are **not on the avatar**;
`Outfit aufräumen` removes them, they *"kurz verschwinden, tauchen aber wieder auf"*.
`[OutfitCleanup] … unworn-attachment=2 …` proved the cleanup found and (thought it) removed them.
Root cause: `CleanUpCurrentOutfit`'s `Trash()` did `MoveItem(link → Trash)` + a local
`cofNode.Nodes.Remove` — but `MoveInventoryItem` on a Current-Outfit link does not stick on
SL/OpenSim; the link comes back on the next COF refetch. A COF link has no asset, so deleting one
only drops the outfit entry (the linked item is untouched), and delete is what the reference
viewer does (`llappearancemgr.cpp` `removeCOFItemLinks` → `remove_inventory_item`). Changed to
collect the link ids and call `_client.Inventory.RemoveItemsAsync(...)` — LibreMetaverse routes
that through the AIS capability on SL (durable) and a `RemoveInventoryObjects` packet on OpenSim,
a real delete either way. `[OutfitCleanup]` log now reports `via RemoveItems, AIS=<bool>`.
**Not yet re-verified in-world** — if they STILL reappear after this, something is *re-adding*
them (server-side COF, an `AppearanceManager` re-sync, or a current-Outfit re-apply), a separate
investigation. `RemoveOutfitLinksForItems` (the detach path) still uses `MoveItem` — apply the
same change there if the detach-doesn't-persist symptom survives.

### The right-click context menu crashed and permanently greyed "Detach" (`v0.20.32-alpha`)
`OnTreeGuiInput` called `_contextMenu.SetItemDisabled(<id>, ...)` — but `SetItemDisabled` takes
an **index**, and this menu's ids (`0,1,2,4,5,6`) stop matching their indices (`0..5`) at
"Delete". So `SetItemDisabled(5, !isLandmark)` disabled index 5 = **"Detach"** (always, for any
non-landmark), and `SetItemDisabled(6, ...)` ran off the end of a 6-item menu →
`ERROR: Index p_idx = 6 is out of bounds`. Now resolves id → index via
`_contextMenu.GetItemIndex(id)`. This is (at least part of) why "Ablegen" did nothing from the
"Inventar" tree — the menu entry was disabled.

## Leads (not yet verified — starting points for the fix)

### 2 — detach from the Worn tab, and the refresh after ANY detach
`InventoryPanel.cs` has ~18 `Callable.From(lambda).CallDeferred()` calls, most of them right
after `await …Async().ConfigureAwait(false)` — i.e. dispatched from a **worker thread**, the
`BUG-RENDER-01` anti-pattern (`[[godot-callable-from-not-threadsafe]]`; the file's own comment at
~line 346 says "never `Callable.From(lambda)` from a bg thread" and then the code does exactly
that everywhere). `DetachWornAsync` (Worn tab), `DetachAndRefreshAsync` and `AttachAndRefreshAsync`
(main tree) all end this way: the detach/attach call itself runs *before* the `Callable.From`, so
the server action may actually happen, but the post-action UI refresh + status text can crash or
silently never dispatch — presenting as "nothing happened". Convert every such site to
`CallDeferred(nameof(...))` on a named method (state in fields) or `MainThreadWorkQueue.Enqueue`,
same as the `BUG-RENDER-01` pass — its own sub-task, ~18 sites. Also still verify the id passed is
the worn-item id not a COF link id (`BUG-NET-02`), and the wearable-detach path on a
server-side-baking region.

### 2b — detach from the Worn tab (original note)
`DetachWornAsync` (`InventoryPanel.cs`, ~line 435):

```csharp
var result = await _session.DetachItemAsync(itemId).ConfigureAwait(false);
Callable.From(() => { ... RefreshWorn(); ... }).CallDeferred();
```

`.ConfigureAwait(false)` puts the continuation on a **background thread**, and
`Callable.From(lambda).CallDeferred()` off the main thread is the exact anti-pattern
`BUG-RENDER-01` found and fixed at 14 sites (`[[godot-callable-from-not-threadsafe]]`): it can
crash with `AccessViolationException` or silently never dispatch — which would present as
"Ablegen does nothing, no refresh, status stuck on *Wird abgelegt…*". Fix: hop back via
`CallDeferred(nameof(...))` or `MainThreadWorkQueue`, same as the `BUG-RENDER-01` conversions.
Independently, confirm `row.GetMetadata(0)` carries the **worn item id** `DetachItemAsync`
expects and not a COF link id (the `BUG-NET-02` failure mode), and that `DetachItemAsync` has a
sensible path for a **wearable** on a server-side-baking region (SL) — see
`[[libremetaverse-sendappearance-flag]]` and `[[lmv-attachment-cache-lags]]`.

### 1 — worn marker in the main tree
The main tree build already reads `_session.GetWornItemsMap()` and gilds worn rows
`SetCustomColor(0, Color(1.0, 0.88, 0.4))` (`InventoryPanel.cs` ~940 / ~1243 / ~1287). If it is
not visible: (a) the gold tint is too subtle — a badge / bold / "(getragen)" suffix would read
better; (b) only **already-expanded** folders get re-marked (`RefreshFolder` bails "never
expanded -- nothing to refresh", ~line 903), so an item in a folder you expand *after* wearing
never gets the marker, and the map may also be stale relative to a later `WornItemsChanged`;
(c) `GetWornItemsMap()` may come back empty / stale on SL. Decide on the visual treatment with
`ux-designer`, then make sure the marker is applied on first expand and refreshed on
`WornItemsChanged` for every loaded folder.

### 3 — slow load, no progress
Lazy per-expansion `FetchInventoryChildrenAsync` with `_loadedFolders` gating; item **names**
arrive a beat later ("Names arrive from RequestFetchInventory a moment later — reload once",
~line 581). No loading affordance anywhere. Add: a per-folder spinner / "lädt…" row while a
`FetchInventoryChildrenAsync` for that folder is in flight, and a top-level busy indicator while
the initial skeleton is still filling. Separately investigate whether the *delay itself* is
avoidable — prefetch the first level, or widen the fetch batch — rather than only papering over
it with a spinner.

## Acceptance

- Worn items are unmistakably marked in the "Inventar" tree (not just a faint tint), on first
  expand and after any wear/detach.
- "Ablegen" in the "Angezogen" tab actually detaches (attachment removed / wearable taken off),
  the list refreshes, and it does not rely on `Callable.From(...).CallDeferred()` from a worker
  thread.
- A visible loading state while inventory is still being fetched (per-folder and initial).
- Typing a name in the search box finds matching folders **and their contents**, and matching
  items in folders that were never expanded by hand, without the user having to open anything.
- Tests where there is a test surface (`GridSession` detach/worn-map logic in `SLNG.Net.Tests`);
  the Godot UI wiring itself has none.

## `v0.20.57-alpha` — the three remaining pieces

### 1. Fourteen `Callable.From(lambda).CallDeferred()` sites, every one on a worker thread

Every async method in this panel awaits with `ConfigureAwait(false)`, and Godot's main thread has
no `SynchronizationContext` — so **all fourteen** remaining sites were running the anti-pattern from
a background thread. That is not a style point: a delegate-backed `Callable`'s deferred dispatch is
main-thread-only in Godot .NET, so from a worker it either crashes the process with a fatal
`AccessViolationException` inside `godotsharp_callable_call_deferred` or silently never runs. Both
have already happened in this project — the crash in `GpuCache`, and the silent no-op **in this
file**, which is why "Ablegen" looked dead for a whole live session (`v0.20.34`). The remaining
fourteen covered every Outfits-tab action (wear / replace / add / remove / save / rename), the
landmark teleport, and the main tree's folder fetch.

Rather than give each one a field-plus-callback pair (the `FinishDetachWorn` /
`FinishTreeItemAction` shape), they all go through one helper:

```csharp
private void RunOnMainThread(Action work)
{
    if (!IsInstanceValid(this)) return;
    _uiWork.Enqueue(work);
    CallDeferred(nameof(DrainUiWork));   // by METHOD NAME -- safe from any thread
}
```

`CallDeferred` by method name is a StringName dispatch with no delegate marshalling, which is the
documented-safe form; the `ConcurrentQueue` keeps the closures each site already had, FIFO, so
ordering between two updates is preserved. `DrainUiWork` catches per item, so one failed UI update
cannot swallow the rest of the queue.

### 2. Worn marker in the "Inventar" tree

Was a warm gold `SetCustomColor` and nothing else — subtle against every other tint in the tree,
and invisible to a colour-blind reader. Now a `✔ ` glyph prefix as well, and, more importantly, it
**stays correct**: `ApplyWornMarker` is one idempotent function (it strips what it added last time
before re-adding), used both when a row is built and by `RefreshWornMarkers`, which walks the tree
on every `WornItemsChanged`. Before, a marker was only ever right at the moment its folder was
fetched — wearing something updated the "Angezogen" tab and at most re-fetched the Current Outfit
folder, while the row for the actual item, in whatever folder it really lives in, kept its stale
marker. The walk is a metadata read per row and no network, so it can simply always run.

Row metadata is now set **before** the text so the build path can call `ApplyWornMarker` too —
one implementation, no drift between "decorated at build" and "decorated on refresh".

### 3. Per-folder load indicator

A folder row is created with a `"…"` placeholder child, which means "not fetched yet" and looks
identical whether a fetch is running, has not started, or failed — the ambiguity behind *"lädt
langsam und man sieht nicht, dass etwas passiert"*. `LoadFolder` now sets it to `⏳ lädt…` and the
failure path to `⚠ Fehler — nochmal aufklappen`. `SetFolderPlaceholder` only ever touches a **lone**
child with empty metadata, so it can never overwrite a folder's real contents. Success needs no
cleanup — `Populate` clears the placeholder outright.

**Not yet verified in-world.**

## `v0.20.58-alpha` -- "Outfit aufraeumen" had become a permanent no-op

**Live, Agni 2026-09-03.** Two attachments (`.Heol Star Bracelet Gold`, `.Heol Star Earrings
Gold`) showed in the Worn tab as `(nicht aktiv)`; *"bereinigen hilft auch nicht und ich kann sie
anlegen wenn ich will"*. The log says why -- on a fully-loaded session:

```
[OutfitCleanup] deferred - still loading (links=28 unresolved=10 scene-worn-attachments=7)
```

`storeReady` required `linkUnresolved == 0`, and **that condition is unsatisfiable by waiting**:
LibreMetaverse's inventory store only ever holds folders somebody fetched, so a COF link pointing
at an item in a folder the user never opened never resolves, ever. The gate (`v0.20.36`) was
written to mean "the COF is still streaming, come back in a moment" -- but for these links there is
no moment to come back to. The button deferred every single time.

That also explains why the two items could be listed at all while cleanup could not judge them:
**a COF link carries the target item's name**, so the Worn tab renders it fine without the target
ever being in the store. And even past the gate, the cleanup loop would have skipped them as
`uncachedSkipped`.

### Fix: ask the server, instead of waiting for something that will not happen

`CleanUpCurrentOutfitAsync` (new) resolves the missing targets first, then runs the existing
synchronous cleanup with `targetsResolved: true`:

- `ResolveCofLinkTargetsAsync` collects every COF link target missing from the store and fetches
  them with `InventoryManager.RequestFetchInventoryAsync(items, ct, callback)`, writing what comes
  back into the store via `Inventory.UpdateNodeFor` (LMV's own reply handler uses the same call).
- It then **polls the store** for up to 5 s rather than trusting that call to have finished the
  job. Whether it returns once the reply is in or merely once the request is sent is an
  implementation detail of the pinned LMV build, and LMV's own reply handler is a second writer;
  polling what the cleanup actually reads makes the outcome independent of both.
- `storeReady` becomes `linkUnresolved == 0 || targetsResolved`.

**Why relaxing that gate is safe.** Anything still unresolved after the fetch is skipped
individually by the cleanup loop (`uncachedSkipped`) -- never deleted -- so the relaxation cannot
delete a link SLNG failed to understand. And the half of the gate that actually caused the
`v0.20.36` regression is untouched: `sceneReady` still stops an attachment that has not rezzed yet
from reading as "not worn", and that is the path the incident's own log line
(`unworn-attachment=2`) came from.

The button now disables itself while the fetch runs and reports through the same
`RunOnMainThread` marshalling as everything else in the panel.

**Not yet verified in-world.** Expect `[OutfitCleanup] resolved N/10 previously-uncached COF link
target(s)` followed by a real cleanup line instead of the deferral.

## `v0.20.59-alpha` -- ten COF links pointing at one item that no longer exists

The `v0.20.58` cleanup ran for the first time and did remove the two `(nicht aktiv)` attachments:

```
[OutfitCleanup] resolved 0/1 previously-uncached COF link target(s)
[OutfitCleanup] links=28 scene-worn=7 cache-worn=6 | deleted dead=0 target-in-trash=0
                unworn-attachment=2 duplicate=0 (via RemoveItems, AIS=True)
                | kept worn=6 clothing/bodypart=10 uncached=10
```

But two numbers in that line do not add up on first reading, and the discrepancy is the next bug:
**`uncached=10` links against exactly ONE distinct unresolved target.** The cleanup counts uncached
*per link*, the fetch deduplicates *per target* -- so this COF carries ten links that all point at
the same item, and the server did not return it (`resolved 0/1`). That item is gone.

Those ten were skipped, every run, forever: the `uncached` branch exists to be conservative about
an item in a folder nobody fetched, and it cannot tell that case apart from a genuinely deleted
one. So a third of this COF was untouchable dead weight -- a large part of why cleanup felt like it
did nothing even once it started running.

### Fix

`ResolveCofLinkTargetsAsync` now **returns the targets it asked for and did not get**, and the
cleanup deletes their links as `missing-target`. The distinction that makes this safe is between
*we never asked* and *we asked and the server said no*:

- Only a **clean** fetch licenses the verdict. Cancelled, timed out or throwing returns `null`, and
  then nothing is treated as missing -- the old conservative skip stands.
- A target that is simply absent from LibreMetaverse's store, without having been asked for, is
  still skipped exactly as before.

The log line gained the numbers that make this readable rather than a puzzle:
`link targets: N link(s), M distinct uncached target(s), resolved R, still missing S,
fetchCompleted=…` plus `missing-target=` in the deletion breakdown.

**Not yet verified in-world.**

