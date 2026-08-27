# Bug: BUG-NET-01 (Camera-based Object Fetching / Interest Management)

## Context
When zooming the camera towards a distant point (using the SL-typical Alt-Click focus zoom), the physical camera moves towards the target. However, 3D objects (prims/meshes) at that distant location are not loaded. 

## Technical Details
- **Symptom:** The actual 3D objects (meshes/prims) are missing, not just high-res textures.
- **Action:** The camera zoom is physical (Alt-Click), meaning it changes the camera's actual spatial position in the world, not just the Field of View (FOV).
- **Probable Root Cause:** The client's Interest Management and Object Fetching logic is likely strictly bound to the *Avatar's* position (Draw Distance around the avatar). When the camera moves independently, the network synchronization or local visibility culling does not update to include the camera's new region of interest. 

## Acceptance Criteria
- [ ] Camera spatial position is correctly factored into the interest list / object fetch priority.
- [ ] Zooming (Alt-Clicking) onto a distant, currently unloaded point reliably triggers the network fetch and rendering of objects at that location.
- [ ] Returning the camera to the avatar restores normal avatar-centric loading behavior without breaking existing visibility.
