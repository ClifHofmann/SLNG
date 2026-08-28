# [MVP2-3] Minimap, World Map & Region Search

- **Feature ID:** `MVP2-3`
- **Track:** `ui` / `net`
- **Status:** `✅ Done`
- **Owner:** `claude`
- **Agent:** `protocol-re` (grid/map protocol) + `ux-designer` (minimap + map window)
- **Dep:** `MVP2-2` (landmark teleport — the teleport execution path this reuses), `M0-2`

## Context
`MVP2-2` was scoped as *"Phase 1 of World Map, Minimap & Teleport"* and shipped only the
**landmark teleport + create-landmark** slice. The minimap, the full grid map, region search and
teleport-by-coordinates were explicitly deferred there (`MVP2-2` spec sub-tasks, last item).
This task is that deferred half, tracked on its own so "MVP 2 done" is honest.

Today the only navigation UI is: landmark rows in the inventory (right-click / double-click →
teleport). There is no map of any kind.

## Requirements

### 1. Minimap (radar) overlay — Phase 1
- A small, toggleable, avatar-centred top-down view of the **current region**: region bounds,
  own position + heading, and dots for **nearby avatars**.
- Nearby-avatar positions come from LibreMetaverse's `CoarseLocationUpdate` (compact per-region
  avatar list — exactly what a radar needs) and/or the `World`'s own avatar entities.
- North-up (Phase 1); heading-up is a later option.
- Toolbar entry (`SLNGWindow` or a fixed corner overlay — implementation's call, but it must
  respect "Toggle HUD").

### 2. Full world / grid map — Phase 2
- `WorldMapWindow : SLNGWindow`. Pan + zoom over a grid of **region map tiles**.
- Tiles come from the grid's map-tile service. LibreMetaverse's `GridManager`
  (`GridClient.Grid`) exposes `RequestMapRegion` / `RequestMapItems` / the `GridRegion` event
  and the map-image asset ids; the actual tile bitmaps are J2C textures fetched through the
  existing asset path.
- Own-avatar marker at the correct global position. Friends markers are a later add.
- **Double-click a point → teleport there** (reuses `MVP2-2`'s teleport path, extended to
  `RequestTeleport(regionHandle, localPos)`).
- Click a region → show its name + coordinates.

### 3. Region search + teleport by name/coords — Phase 3
- A search box in the map window: type a region name → `GridManager` name lookup
  (`MapNameRequest` / `RequestMapRegion(name)`) → resolve to handle + global coords → centre
  the map on it and offer "Teleport".
- A direct "go to `<region> <x> <y> <z>`" entry (SLURL-ish) that resolves and teleports.

### 4. Arrival feedback — Phase 4
- A brief "Arrived in `<region>`" toast on `GridSession.RegionConnected` (that event **now
  exists** — the `MVP2-2` spec's note that it doesn't is stale as of BUG-NET-01 / BUG-ENV-01).

## Boundary / constraints
- **No LibreMetaverse type crosses `GridSession`'s public surface** (AGENTS.md): convert
  `GridRegion` and map items to neutral DTOs (name, global x/y, size, map-image id).
- Grid/map events arrive on background threads → buffer + drain, never touch `World` or Godot
  from the callback.
- Map tiles are textures — decode off the main thread, upload via the existing `GpuCache` path.
- OpenSim is the test target; SL's map service differs in URL shape but the LMV API is the same.

## Acceptance criteria
- [x] Minimap shows the current region, own position/heading, and moving dots for nearby
      avatars; toggles with "Toggle HUD".
- [x] Minimap is zoomable (mouse wheel) and lists nearby avatars by name; clicking a name
      highlights that avatar's dot on the radar (2026-08-28 addendum).
- [x] World map window renders region tiles for an area around the avatar and pans/zooms.
- [x] Double-clicking the map teleports the avatar to that region + local position.
- [x] Region search resolves a name to a location and can teleport there.
- [x] "Arrived in `<region>`" feedback shows after a successful teleport / region crossing.
- [x] No LMV type on `GridSession`'s public API; background events are marshalled.
- [x] **World map teleport reconfirmed in-world, 2026-08-28** — a single map click + double-click
      on "Swondo Flower Island" produced `[WorldMap] Teleport result: success=True message=""` in
      `godot.log`, immediately followed by new region asset activity (fresh texture fetches),
      i.e. an actual region crossing, not just a truthy return value. The re-entrancy race is
      fixed.
- [x] **Minimap roster click confirmed in-world, 2026-08-28** — `RefreshListIfChanged` fix works;
      highlighting an avatar from the roster list now actually happens.
- [x] **Double-click a roster row jumps the radar to them, 2026-08-28 addendum** — pans AND zooms
      in (32 m visible range, vs. the 64 m default) on the double-clicked avatar; double-clicking
      the same (already-focused) row returns to centring on the local avatar. This is a purely
      client-side UI operation -- the SL/OpenSim `AgentUpdate` "camera" fields exist for the real
      3D viewport camera (interest-list culling, LOD), not a flat 2D radar, so nothing is reported
      to the grid for this.
- [x] **World map window fits the screen, 2026-08-28** — same-day follow-up: the window opened
      640x520 at a hardcoded (200,100) regardless of actual screen size and ran off the bottom.
      First fix attempt reordered `PersistId`/`base._Ready()` (matching `EnvironmentWindow`'s
      order) and shrank the first-open default to 600x460 at (160,60) -- `MinimapOverlay` had the
      identical reordering applied too. **This diagnosis was wrong**, corrected the same day (see
      the next entry): `SLNGWindow.RestorePersistedGeometry` runs via `CallDeferred`, specifically
      so subclass defaults are already applied by the time it runs regardless of when `PersistId`
      is set within `_Ready()` -- the reorder was harmless but didn't touch the actual cause.
- [x] **Real root cause: unbounded single-corner resize corrupted saved geometry, 2026-08-28** —
      the window "too tall, off the bottom" report recurred after the reorder "fix", with
      `user://preferences.cfg` showing `world_map_size=Vector2(480, 1236)`. `SLNGWindow` only ever
      had ONE resize handle (the bottom-right corner), which COUPLES width and height on every
      drag -- there was no way to narrow the window without also dragging its height. An errant
      drag (likely while trying to narrow it) drove height to 1236 px, and
      `RestorePersistedGeometry` only ever enforced a MINIMUM size against `CustomMinimumSize`,
      never a maximum, so the broken value reloaded forever on every subsequent launch. Also
      directly requested by the tester independently ("es wäre gut wenn man Fenster an allen
      Seiten anfassen könnte"). Fixed in `SLNGWindow` (shared by every window in the app, not
      just the world map): all 4 edges + 4 corners now get their own resize handle (`ResizeEdge`
      flags, `AddResizeHandles`/`ApplyResize`), and both the live drag AND
      `RestorePersistedGeometry` now clamp against the current viewport as a maximum, so a value
      already saved before this fix self-heals on next load instead of staying broken forever.

## Live-test fixes (2026-08-28)
The first actual in-world test found two real bugs the unit tests couldn't catch (both are pure
Godot-input/UX issues, invisible to a headless test):

1. **Minimap roster click did nothing.** `MinimapOverlay._Process` called `RefreshList()`
   unconditionally every frame, tearing down and rebuilding every roster row's `Button` ~60
   times a second. Godot's `BaseButton` only raises `Pressed` if the SAME node instance is still
   alive for both the press and the release event; a click landing between two rebuilds (which,
   at 60 Hz, is nearly all of them) simply never registered. Fixed with `RefreshListIfChanged`:
   compute a cheap signature of (agent id, name) pairs + the current selection, and only rebuild
   when it actually differs from the last rebuild.
2. **World map teleport appeared to do nothing.** Two compounding problems:
   - The default zoom (`DefaultPixelsPerMeter = 0.4`) rendered a 256 m region as a ~100 px square
     centred in a much larger, otherwise-EMPTY canvas -- on a typical single/few-region OpenSim
     test grid, most of the visible map was actually non-existent neighbour grid squares, so a
     click anywhere but dead-centre targeted nothing. Fixed: `CenterOnAvatarIfNeeded` now also
     fits the zoom so the home region fills ~70% of the shorter canvas side by default.
   - Nothing stopped a click (or double-click) on such empty space from firing a real
     `TeleportToAsync` anyway. A raw handle-based `TeleportLocationRequest` to a grid square with
     no region on it isn't rejected by the sim -- it just gets no reply -- so the call sat for the
     full 40s `AgentManager.TeleportTimeout` before reporting "timed out", with nothing visible in
     between; a quick test click-and-wait-a-few-seconds looks exactly like "broken". Fixed by
     gating teleport on a CONFIRMED region: `HandlePointAction` only teleports immediately for an
     already-resolved tile (the common case -- `RequestVisibleTilesIfNeeded` has usually already
     streamed it in); otherwise it resolves first and teleports automatically only if that comes
     back non-null, showing "No region here." otherwise. This mirrors the real viewer's own gate
     (`LLAgent::doTeleportViaLocation` only takes the direct/immediate path once
     `LLWorldMap::simInfoFromHandle` has already resolved the target -- verified against the
     vendored `slviewer` source, not guessed).
   - Added `GD.Print` diagnostics around the teleport call (handle/position requested, and the
     result) so any future failure is visible in `godot.log` instead of only in UI text that
     never reaches the console.

**2026-08-28 correction, same day:** the zoom explanation above was wrong -- live-tester report:
they had already zoomed in manually, saw the target sim rendered, clicked/double-clicked it
directly, and teleport still did nothing. Re-examined the session's `godot.log`: the click
coordinates show a genuine double-click (two `MapCanvas` clicks at the IDENTICAL pixel,
back-to-back) plus a separate click on a `Button` positioned where the Teleport button sits --
i.e. the click DID register at the Godot input level, by both available routes. Re-verified
OpenSim's server-side `TeleportLocationRequest` handler (`LLClientView.HandleTeleportLocationRequest`
in the vendored `opensim_fetch` source) -- it resolves a cross-region handle correctly with no
special-case restriction, so this isn't a known protocol gap either. Since LibreMetaverse's own
teleport logging is silenced (`Settings.LogLevel = Error`, set deliberately for unrelated reasons
in `GridSession`'s constructor) and this session predates the `GD.Print` diagnostics added above,
the log cannot show whether `TeleportToAsync` actually ran or what LibreMetaverse reported back --
the remaining gap is genuinely unobservable from evidence gathered so far. Added a further
`GD.Print` at the point of the click itself (`HandlePointAction`: screen/global coords, computed
handle, whether it was already a known/resolved region) and at the click-resolve outcome
(`ApplyResolvedRegion`), so the FULL chain -- click -> resolve (if needed) -> teleport request ->
result -- is traceable in `godot.log` on the next attempt. `v0.10.3-alpha`.

**2026-08-28 root cause found, same day, from the new diagnostics:** the log showed a smoking gun --
`[WorldMap] Teleport result: success=False message="Teleport finished"`. That combination is
impossible if the operation genuinely finished ("Teleport finished" is the literal string
LibreMetaverse's own packet handler writes ONLY on success, alongside `_teleportTcs?.TrySetResult(true)`).
The live-test transcript explained why: the user, seeing no immediate feedback, clicked "Teleport"
again for a DIFFERENT destination while the FIRST attempt was still in flight (well within the 40s
timeout) -- confirmed by their own follow-up ("it was the sim I was aiming for -- a different one
worked"). LibreMetaverse's `AgentManager` tracks "the" in-flight teleport in a single shared field
(`_teleportTcs`); the landmark-UUID `TeleportAsync` overload guards against a second concurrent call
(`teleportStatus == TeleportStatus.Progress` check), but the region-handle overload our map/landmark-
fallback paths use has NO such guard. Two overlapping calls silently cross-wire: whichever response
(from either destination) arrives first completes WHICHEVER task happens to be `_teleportTcs` at
that moment -- explaining both the nonsensical result combinations and why the specific destination
seemed to matter (it didn't; timing did). Fixed with a session-wide `_teleportInProgress` guard
(`GridSession`, `Interlocked`-based) shared by `TeleportToAsync`/`TeleportToLandmarkAsync`/
`TeleportToGlobalPosition`, rejecting a second attempt outright with a clear "A teleport is already
in progress." instead of letting both corrupt each other -- the only fix available from this side of
a vendored NuGet package. Also fixed the failure-message preference in both `TeleportToAsync` and
`TeleportToLandmarkAsync`: they favoured a captured `TeleportProgress` message (a transient
narration, e.g. "Teleport started") over LibreMetaverse's own final `TeleportMessage` (the
authoritative reason, e.g. "Teleport timed out." -- notably, no `TeleportProgress` event fires for a
timeout at all, so the captured message is guaranteed stale in exactly that case), which is why the
very first live-test round saw the misleading "Teleport failed: Teleport started". `v0.10.4-alpha`.

## Implementation notes (as shipped on `feature/MVP2-3-world-map-minimap-search`)
- **`GridSession`** gained neutral DTOs (`NearbyAvatar`/`NearbyAvatarsEvent`, `MapRegionInfo` —
  see `src/SLNG.Core/GridEvents.cs`) and wrappers over `GridClient.Grid`: `NearbyAvatarsUpdated`
  (off `CoarseLocationUpdate`), `RegionDiscovered` (off `GridRegion`), `RequestMapBlocks`,
  `ResolveRegionByNameAsync`/`ResolveRegionByHandleAsync` (`GetGridRegionAsync`), and
  `TeleportToAsync(handle, localPos)` (mirrors `TeleportToLandmarkAsync`'s progress-message +
  post-teleport position resync). No LMV `GridRegion`/`Simulator`/`Vector3` crosses out.
- **`MinimapOverlay : SLNGWindow`** — first shipped as a passive StatsOverlay-style corner panel
  mapping the whole current region onto a fixed square canvas; **2026-08-28 addendum** promoted it
  to a full `SLNGWindow` (draggable/resizable, per the UI Standard — a zoomable radar plus a
  clickable roster is real interactive content, not a passive readout) with:
  - **Zoom** (mouse wheel over the radar): avatar-centred visible range, 16–512 m, default 64 m
    (replacing the earlier whole-region-fixed view — a real minimap should let you tighten or
    widen the view, not just watch a static square).
  - **Roster list**: every nearby avatar by name, scrollable, sorted alphabetically. World-tracked
    avatars (within draw distance) already carry a name via `AvatarComponent`; a CoarseLocationUpdate-
    only avatar (outside draw distance) has none, so it's lazily resolved via
    `GridSession.RequestAvatarName`/`NameResolved` — the exact pattern `FriendsPanel` already uses.
  - **Click-to-highlight**: clicking a roster row selects that agent id; the radar draws a red ring
    around their dot if they're within the current visible range. Same selected-row stylebox idiom
    as `FriendsPanel.BuildRow`.
  - **Double-click-to-jump** (2026-08-28 addendum): double-clicking a roster row re-centres the
    radar's PROJECTION on that avatar instead of the local one, and tightens the visible range to
    32 m (`FocusVisibleRangeMeters`) so a far-off avatar (found only via `CoarseLocationUpdate`,
    easily hundreds of metres away at the 64 m default) is actually visible afterward. The local
    avatar's own dot/heading arrow is still drawn -- just no longer assumed to sit at the canvas
    centre, since `RadarCanvas` now projects everything relative to a separate `center` parameter
    that defaults to the local avatar's position but can be overridden. Double-clicking the
    already-focused row toggles back to centring on the local avatar; if the focused avatar drops
    out of range entirely (teleported away, etc.), focus clears automatically rather than freezing
    the view on a stale point. Purely client-side -- no protocol representation of a 2D radar's
    pan/zoom exists (SL's `AgentUpdate` "camera" fields are for the real 3D viewport camera, used
    for interest-list culling/LOD, unrelated).
  Own position/heading still comes from `World`'s local `AvatarComponent`; nearby dots+names still
  merge `World` (exact, draw-distance-limited, has names) with `CoarseLocationUpdate` (coarse,
  region-wide, id-only) keyed by agent id, so both draw-distance-limited and off-screen avatars
  show up in both the radar and the roster.
- **`WorldMapWindow : SLNGWindow`** — drag to pan, wheel to zoom, click to inspect a point,
  double-click to teleport. Tiles fetched via `RequestMapBlocks` and rendered through the
  existing texture/`GpuCache` path (a map tile is an ordinary JPEG2000 asset). Shows only the
  own-avatar marker, deliberately — **the world map and the minimap are two different tools**
  (2026-08-28 clarification): the world map is a grid-wide sim search/teleport window, the
  minimap is the per-region avatar radar. This matches real SL/Firestorm, where the World Map
  shows your own position but never other residents (privacy) and the Mini-Map is the separate
  floater that shows everyone nearby. An intermediate revision briefly merged `CoarseLocationUpdate`
  into the world map too, showing other avatars there; reverted once this was clarified.
  Reachable from both the "World" toolbar buttons and the top menu (World → World Map / Minimap).
- **Threading:** every `await`-resumed continuation (search, region-resolve, teleport, tile
  fetch) parks its result and calls `CallDeferred` before touching a Control — Godot's main
  thread has no `SynchronizationContext`, so code after a button handler's first `await` is NOT
  guaranteed to still be running on it (same gotcha `SLNGWindow.LoadTextureIntoAsync` documents).
- **Phase 4 arrival toast** waits (via a `_Process` drain, not a fixed delay) for
  `GridSession.CurrentRegionName` to actually be populated before showing the toast, since
  `RegionConnected` can fire before the RegionHandshake that carries the name arrives.
- Localised: `ui.worldmap.*`, `ui.map.arrived_in`, `ui.menu.world_map`/`ui.menu.minimap`,
  `ui.minimap.*` (en-US + de-DE, `--selftest` locale parity 204/204).
- Tests: 6 new `GridSessionTests` (wire-event → DTO mapping via reflection, same pattern as
  `OnScriptDialog`'s test; no-connection graceful-failure for every new async wrapper).

## Known limitations (deferred, not blocking)
- The world map never shows other avatars, anywhere, by design (see above) — only the minimap
  does, and only for the region the client is actually **connected to**; a region merely being
  *displayed* on the minimap's own (whole-region, non-panning) view doesn't apply here since it
  never shows anything but the current region. "Friends markers" on the world map itself remain
  a later add, per the original scope cut.
- A map click's teleport target always uses Z=0 (the simulator is relied on to place the avatar
  at a sane height); there's no ground-height lookup from the map tile.
- No point-of-interest icons (telehubs, popular places, land-for-sale) — `GridManager.MapItemsAsync`
  exists but is out of this task's scope.

## Affected files
- `src/SLNG.Core/GridEvents.cs` — `NearbyAvatar`, `NearbyAvatarsEvent`, `MapRegionInfo`.
- `src/SLNG.Net/GridSession.cs` — see Implementation notes above.
- `app/scripts/UI/MinimapOverlay.cs` (new), `app/scripts/UI/WorldMapWindow.cs` (new).
- `app/scripts/UI/TopMenu.cs` — World menu entries.
- `app/scripts/Boot.cs` — construct + toolbar entries + top-menu wiring + arrival toast.
- `app/i18n/en-US.json`, `app/i18n/de-DE.json` — new keys.
- `tests/SLNG.Net.Tests/GridSessionTests.cs` — new coverage.
