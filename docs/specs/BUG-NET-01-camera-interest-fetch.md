# Bug: BUG-NET-01 (Camera-based Object Fetching / Interest Management)

## Context
When zooming the camera towards a distant point (using the SL-typical Alt-Click focus zoom), the physical camera moves towards the target. However, 3D objects (prims/meshes) at that distant location are not loaded. 

## Technical Details
- **Symptom:** The actual 3D objects (meshes/prims) are missing, not just high-res textures.
- **Action:** The camera zoom is physical (Alt-Click), meaning it changes the camera's actual spatial position in the world, not just the Field of View (FOV).
- **Probable Root Cause:** The client's Interest Management and Object Fetching logic is likely strictly bound to the *Avatar's* position (Draw Distance around the avatar). When the camera moves independently, the network synchronization or local visibility culling does not update to include the camera's new region of interest. 

## Root cause (found 2026-08-27)

SLNG never told the sim where its camera is. `GridSession.SetMovement` only ever called
`_client.Self.Movement.Camera.LookDirection(...)` — which sets the camera *axes* but not
`Camera.Position`. LibreMetaverse's `AgentCamera` constructor defaults `Position` to region
centre `(128, 128, 20)` and `Far` to `128`, and `AgentManager.Movement.SendUpdate` packs those
straight into `AgentUpdate.CameraCenter` / `.Far`. So every sim SLNG has ever connected to
computed its interest list around region centre, never the avatar and never the render camera —
`grep` confirms `GridSession.cs:2520` was the *only* line in `src/` touching the movement camera.

## Fix (`fix/BUG-NET-01-camera-interest-fetch`)

- `GridSession.SetMovement` takes optional `cameraPosition` / `cameraForward`
  (`System.Numerics.Vector3?`, region-local, SL Z-up — no LibreMetaverse type crosses the
  boundary) and `cameraFar`. With a real pose it calls `Camera.LookAt(slPos, slPos + slFwd)` so
  `CameraCenter` follows the render camera; otherwise it keeps the old `LookDirection` fallback.
- `AvatarController._Process` captures the finalised render-camera Godot position, converts it
  with `RenderConfig.FromGodot`, converts the forward vector by the axis swap `(x,-z,y)`, and
  computes `Far = clamp(dist(camera,avatar) + DrawDistance, 128, 512)` so the avatar's
  surroundings stay in the interest set even while the camera is zoomed away. Passed to the
  existing 10 Hz `SetMovement` send.

## Acceptance Criteria
- [x] Camera spatial position is correctly factored into the interest list / object fetch priority.
- [x] Zooming (Alt-Clicking) onto a distant, currently unloaded point reliably triggers the network fetch and rendering of objects at that location.
- [x] Returning the camera to the avatar restores normal avatar-centric loading behavior without breaking existing visibility.

**Confirmed in-world 2026-08-27 (OpenSim).** The network half alone did nothing visible — the
render half (`ObjectRenderer` cull sweep against `min(dist-to-avatar, dist-to-camera)`, commit
`4fc727e`) is what makes distant objects appear. No unit-testable seam today (`SetMovement` needs
a live `GridClient`; `RenderConfig` / `CameraSettings` live in `app/`, which has no test
project — flagged as a gap by code review).
