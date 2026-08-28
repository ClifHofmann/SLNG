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
