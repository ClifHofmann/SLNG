# [MVP2-3] Minimap, World Map & Region Search

- **Feature ID:** `MVP2-3`
- **Track:** `ui` / `net`
- **Status:** `⏸️ Pending`
- **Owner:** —
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
- [ ] Minimap shows the current region, own position/heading, and moving dots for nearby
      avatars; toggles with "Toggle HUD".
- [ ] World map window renders region tiles for an area around the avatar and pans/zooms.
- [ ] Double-clicking the map teleports the avatar to that region + local position.
- [ ] Region search resolves a name to a location and can teleport there.
- [ ] "Arrived in `<region>`" feedback shows after a successful teleport / region crossing.
- [ ] No LMV type on `GridSession`'s public API; background events are marshalled.

## Affected files (anticipated)
- `src/SLNG.Net/GridSession.cs` — neutral wrappers over `GridClient.Grid`
  (`RequestRegionsAsync`, `ResolveRegionByNameAsync`, `TeleportToAsync(handle, localPos)`),
  `CoarseLocationUpdate` → a neutral nearby-avatar list event.
- `src/SLNG.Core/` — `MapRegionInfo` / `NearbyAvatar` DTOs.
- `app/scripts/UI/MinimapOverlay.cs` (new), `app/scripts/UI/WorldMapWindow.cs` (new).
- `app/scripts/Boot.cs` — construct + toolbar entries.
