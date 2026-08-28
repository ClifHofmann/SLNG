# Bug: BUG-NET-02 (Neighbor Region Rendering / Cross-Region Visibility)

## Context
The user reported a high-priority bug: *"Man kann nicht über die Sim Grenze sehen"* (Cannot see across the region border). Currently, the client only connects to and renders the single simulator the avatar is occupying. The world abruptly terminates at the local region borders (typically 256x256m), severely hindering navigation and immersion.

## Requirements
1. **Neighbor Connections (Network):**
   - Listen for neighbor region advertisements (`EnableSimulator` / seed caps) provided by the grid.
   - Establish secondary UDP circuit connections to neighbor simulators.
   - Connect as a "child agent" to these regions so they begin streaming `Terrain` and `ObjectUpdate` packets to the client.
2. **World Simulation & Coordinate Offsets:**
   - The rendering engine must map incoming objects and terrain from neighbor simulators to their correct absolute global grid coordinates.
   - If the main region is at grid (1000, 1000) and a neighbor is at (1001, 1000), the neighbor's local (0,0) must correctly offset by +256m in the X-axis in Godot's World3D.
3. **Culling / Draw Distance:**
   - Automatically connect to adjacent regions if they fall within the user's active Draw Distance.
   - Gracefully disconnect and unload terrain/prims when moving too far away.

## Acceptance Criteria
- [ ] Moving towards a region border automatically loads and displays the adjacent neighbor region.
- [ ] Terrain perfectly aligns at the region seam.
- [ ] Objects on the neighbor region render in their correct positions and are visible from the main region.

## Investigation & Phase 1 (2026-08-28, `fix/BUG-NET-03-neighbor-region-rendering`)

**Root cause — one flag.** LibreMetaverse 3.1.3 defaults `Settings.Agent.MultipleSims` to
**`false`** (`Settings/AgentSettings.cs:10`). With it off, `NetworkManager.EnableSimulatorHandler`
(`NetworkManager.cs:1463`) does `if (!Client.Settings.Agent.MultipleSims) return;` — so every
`EnableSimulator` message the grid sends for a neighbor is **dropped**, no child circuit is ever
opened, and the world ends at the current region's 256 m border. (The pinned version changed here
vs. LMV 3.0.0 — an existing `OnSimConnected` comment about "a neighbor sim connected only for
interest-list purposes" pre-dates FEAT-NET-03 and no longer describes runtime behaviour.)

**The rest of the stack was already multi-region.** Nothing else needed structural change:
- `SLNG.Core.ECS.World` keys every entity/terrain by `(regionHandle, localId)` — no single-region
  assumption anywhere.
- `WorldSimulation` threads `e.RegionHandle` through every handler; `RegionDisconnectedEvent` →
  `World.RemoveRegion(handle)` already unloads a region.
- `RenderConfig.ToGodot(regionHandle, slLocal)` offsets by the region's global SW corner minus
  the floating origin, so a neighbor's local coords land at the right Godot world position for free.
- `TerrainRenderer` keeps one terrain node per region handle.
- `GridSession.OnObjectUpdate` / `OnTerseObjectUpdate` / `OnLandPatchReceived` / `OnKillObject`
  and the per-sim `TerrainSettingsEvent` in `OnSimConnected` all pass `e.Simulator.Handle`
  straight through with **no `== CurrentSim` filter**.
- BUG-NET-01's draw-distance cull already measures `min(dist-to-avatar, dist-to-camera)` against
  `RenderConfig.DrawDistance` (96 m), so a neighbor's near-border objects render and its far ones
  stay culled — no extra work needed for criterion 3's "within draw distance".

**Change:** `_client.Settings.Agent.MultipleSims = true` in the `GridSession` ctor, plus
unconditional `[Neighbor] connected/disconnected <name> (<handle>) <dir>` log lines
(`OnSimConnected` else-branch / `OnSimDisconnected`) — infrequent lifecycle events, and the
signal a live session needs to confirm the cross-border fetch. New `GridSessionTests` pins the
flag (its default is the wrong way, so a settings cleanup could silently regress this).
`v0.11.6-alpha`. Builds (solution + `app/`), 377 tests, `dotnet format` clean, `--selftest` 26/26.

**Not yet verified in-world** — this is the load-bearing step. Watch for on a live OpenSim grid
with configured neighbors: (a) `[Neighbor] connected …` lines appear near a border; (b) neighbor
terrain renders and the seam aligns; (c) neighbor objects render at correct positions; (d) walking
back across / teleporting away fires `[Neighbor] disconnected …` and unloads that region.

**Known follow-ups deferred to Phase 2 (need live packet capture first):**
- Whether OpenSim streams objects to a child agent without the client sending it `AgentUpdate`s.
  LibreMetaverse's `Movement.SendUpdate` only ever targets `CurrentSim` (`AgentManager.Movement.cs:554`),
  and its `SendManualUpdate` / parameter overloads also `SendPacket` without a sim arg, so there is
  no public API to feed a neighbor's interest list — if neighbor objects don't appear, hand-rolling
  an `AgentUpdatePacket` per child circuit via `Network.SendPacket(pkt, sim)` is the likely Phase 2.
- Walk-across region crossings (child→root promotion) may not re-fire `RegionConnected`, so the
  floating origin might not recenter on a crossing — harmless within ±256 m, revisit if judder
  appears past the first ring of neighbors.
