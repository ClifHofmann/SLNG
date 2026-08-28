# Feature: FEAT-UI-18 (Teleport Loading Screen Overlay)

## Context
The user requested visual feedback during teleportation: *"Teleportieren brauch eine animation wie auch beim login also so nen ladebalken oder so"*. Currently, initiating a teleport can make the client appear frozen while it waits for the server to process the request and for the destination region to handshake/load. A loading overlay, similar to the login screen, is required to bridge this waiting period and provide UX feedback.

## Requirements
1. **Teleport Overlay UI:**
   - Create a new `TeleportOverlay` (or reuse components from the login loading screen like the circular progress ring and glassmorphism panel).
   - Display dynamic status text indicating the current step of the teleport (e.g., "Requesting Teleport...", "Connecting to Region...", "Arriving...").
2. **Integration with Network Flow:**
   - Hook into `GridSession` teleport methods (`TeleportToAsync`, `TeleportToLandmarkAsync`).
   - Show the overlay immediately upon initiating the teleport.
   - Read LibreMetaverse's `TeleportProgress` events (or similar status updates) to feed the loading text.
   - Hide the overlay when the teleport concludes (either success via `RegionConnected`/Arrived, or failure).
3. **User Experience:**
   - Block viewport and UI interaction while the teleport is in progress.
   - Use a smooth fade-in and fade-out transition.

## Acceptance Criteria
- [ ] A loading overlay appears immediately when a teleport is triggered.
- [ ] The UI provides textual or visual progress updates during the region crossing.
- [ ] The overlay disappears automatically once the avatar has successfully arrived at the destination.
- [ ] Teleport failures gracefully dismiss the overlay and show an error message instead.
