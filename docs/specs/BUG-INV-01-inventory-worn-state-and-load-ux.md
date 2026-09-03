# [BUG-INV-01] Inventory: worn-state not visible, detach-from-Worn broken, slow load with no progress

- **Feature ID:** `BUG-INV-01`
- **Track:** `ui` / `net`
- **Status:** `⏸️ Pending`
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
