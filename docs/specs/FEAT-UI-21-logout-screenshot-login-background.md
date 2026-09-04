# [FEAT-UI-21] Screenshot on Logout for Next Login Background

- **Feature ID:** `FEAT-UI-21`
- **Track:** `ui`
- **Status:** `⏸️ Pending`
- **Owner:** 
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Clean shutdown of the application. Mimicking the Firestorm viewer, the client should take a snapshot of the current 3D scene right before logging out or closing the application. This image is saved locally. On the next start and during the login process, this snapshot is displayed as the background image on the loading screen until the login is fully complete and the user can move in the 3D world.

## Acceptance Criteria
- [ ] Intercept the application quit / logout process to prevent a hard kill of the process.
- [ ] Ensure a **clean shutdown sequence**: send a proper logout request to the grid, wait for the disconnect/confirmation, and save necessary client states.
- [ ] Capture the current 3D viewport (ideally without the UI) during this shutdown sequence and save it to the local user data directory (e.g., `last_session_bg.png`).
- [ ] During the next application startup, the login and loading screens load and display this image as the background.
- [ ] The background is hidden/removed only when the login is fully complete and the 3D world is ready to be interacted with.

## Technical Specs & Affected Files
- `app/scripts/Boot.cs` (Application quit interception, loading screen management)
- `app/scripts/UI/LoginWindow.cs` (or equivalent Login UI script for background display)
- `app/scripts/UI/LoadingScreen.cs` 

## Sub-tasks / Progress
- [ ] Implement viewport capture on shutdown/logout.
- [ ] Save snapshot to disk.
- [ ] Load snapshot as texture on Boot/Login.
- [ ] Transition logic to hide the snapshot when the region is fully loaded.
