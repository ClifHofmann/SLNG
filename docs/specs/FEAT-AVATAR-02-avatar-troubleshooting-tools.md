# Feature: FEAT-AVATAR-02 (Avatar Health & Troubleshooting Tools)

## Context
Second Life and OpenSim avatars frequently suffer from visual glitches—such as stuck animations, corrupted texture bakes, or permanently deformed skeletons caused by broken animation assets. The user requested a suite of recovery tools analogous to Firestorm's "Avatar Health" (Avatar Befinden) options to allow users to fix these issues locally without needing to relog.

## Requirements
Implement a dedicated UI menu (e.g., under `TopMenu` -> `Avatar` -> `Avatar Health` or via a toolbar button) containing the following troubleshooting commands:

1. **Force Texture Rebake ("Rebake Textures" / Texturen neu backen):**
   - Send a request to the grid to invalidate the current Bakes-on-Mesh (BoM) composites and force a fresh rebake of the avatar's appearance.
2. **Stop All Animations ("Stop Animating Me" / Animationen stoppen):**
   - Halt all currently playing animations on the local avatar.
   - Provide an escape hatch for when the avatar gets stuck in a poseball script or a broken Animation Override (AO) state.
3. **Reset Skeleton & Undeform ("Undeform Avatar" / Skelett zurücksetzen):**
   - Reset all avatar bone translations and rotations to their absolute default bind pose.
   - Re-apply the current shape parameters cleanly to fix limbs that were permanently distorted by a bad animation (which often alter joint offsets instead of just rotations).
4. **Reload Avatar (Optional):**
   - Clear the local visual cache for the avatar and re-trigger a fetch of their worn mesh and shape assets.

## Acceptance Criteria
- [ ] The viewer provides an accessible "Avatar Health" menu.
- [ ] Triggering a "Rebake" successfully refreshes the avatar's composite textures.
- [ ] "Stop Animations" immediately halts stuck poses and returns the avatar to the default idle state.
- [ ] "Reset Skeleton" successfully clears bone deformations without requiring a client restart.
