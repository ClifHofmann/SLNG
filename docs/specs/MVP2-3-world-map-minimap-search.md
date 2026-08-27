# [MVP2-3] Minimap, World Map & Region Search

- **Feature ID:** `MVP2-3`
- **Track:** `ui` / `net`
- **Status:** `🧪 Review`
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
- [x] World map window renders region tiles for an area around the avatar and pans/zooms.
- [x] Double-clicking the map teleports the avatar to that region + local position.
- [x] Region search resolves a name to a location and can teleport there.
- [x] "Arrived in `<region>`" feedback shows after a successful teleport / region crossing.
- [x] No LMV type on `GridSession`'s public API; background events are marshalled.
- [ ] **Not yet confirmed in-world** — every box above is unit-tested (`GridSessionTests`) and
      `--selftest`-clean, but none of it has been driven against a live OpenSim session yet
      (tile fetch, pan/zoom feel, click-to-teleport accuracy, the arrival toast's timing).

## Implementation notes (as shipped on `feature/MVP2-3-world-map-minimap-search`)
- **`GridSession`** gained neutral DTOs (`NearbyAvatar`/`NearbyAvatarsEvent`, `MapRegionInfo` —
  see `src/SLNG.Core/GridEvents.cs`) and wrappers over `GridClient.Grid`: `NearbyAvatarsUpdated`
  (off `CoarseLocationUpdate`), `RegionDiscovered` (off `GridRegion`), `RequestMapBlocks`,
  `ResolveRegionByNameAsync`/`ResolveRegionByHandleAsync` (`GetGridRegionAsync`), and
  `TeleportToAsync(handle, localPos)` (mirrors `TeleportToLandmarkAsync`'s progress-message +
  post-teleport position resync). No LMV `GridRegion`/`Simulator`/`Vector3` crosses out.
- **`MinimapOverlay`** (new, not an `SLNGWindow` — a StatsOverlay-style corner panel) maps the
  *whole current region* onto a square canvas rather than panning/zooming with the avatar —
  correct for a standard 256 m region and much simpler; a varregion just stretches the same
  square. Own position/heading comes from `World`'s local `AvatarComponent`; nearby dots merge
  `World` (exact, draw-distance-limited) with `CoarseLocationUpdate` (coarse, region-wide) keyed
  by agent id, so both draw-distance-limited and off-screen avatars show up.
- **`WorldMapWindow : SLNGWindow`** — drag to pan, wheel to zoom, click to inspect a point,
  double-click to teleport. Tiles fetched via `RequestMapBlocks` and rendered through the
  existing texture/`GpuCache` path (a map tile is an ordinary JPEG2000 asset). Shows the same
  merged own+other avatar markers as the minimap, for whichever region is actually connected.
  Reachable from both the "World" toolbar buttons and the top menu (World → World Map / Minimap).
- **Threading:** every `await`-resumed continuation (search, region-resolve, teleport, tile
  fetch) parks its result and calls `CallDeferred` before touching a Control — Godot's main
  thread has no `SynchronizationContext`, so code after a button handler's first `await` is NOT
  guaranteed to still be running on it (same gotcha `SLNGWindow.LoadTextureIntoAsync` documents).
- **Phase 4 arrival toast** waits (via a `_Process` drain, not a fixed delay) for
  `GridSession.CurrentRegionName` to actually be populated before showing the toast, since
  `RegionConnected` can fire before the RegionHandshake that carries the name arrives.
- Localised: `ui.worldmap.*`, `ui.map.arrived_in`, `ui.menu.world_map`/`ui.menu.minimap`
  (en-US + de-DE, `--selftest` locale parity 200/200).
- Tests: 6 new `GridSessionTests` (wire-event → DTO mapping via reflection, same pattern as
  `OnScriptDialog`'s test; no-connection graceful-failure for every new async wrapper).

## Known limitations (deferred, not blocking)
- The world map's avatar radar only covers the region the client is actually **connected to** —
  a region merely being *displayed* on the map (panned to, not logged into) has no avatar data,
  same as the spec's own "Friends markers are a later add" scope cut.
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
