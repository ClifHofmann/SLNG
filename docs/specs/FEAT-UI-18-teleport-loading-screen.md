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

## Implementation (2026-08-28, `feature/FEAT-UI-18-teleport-loading-screen`)

**Neutral progress event (`SLNG.Core` / `SLNG.Net`).** New `TeleportStage` enum + `TeleportProgressEvent`
record in `GridEvents.cs`, mirrored by name from LibreMetaverse's `TeleportStatus`
(`Start/Progress/Failed/Finished/Cancelled`, `None` dropped) — the same pattern `LoginStage`
already uses so no `OpenMetaverse`/`LibreMetaverse` type crosses the `SLNG.Net` boundary
(AGENTS.md layering). `GridSession` gained `public event EventHandler<TeleportProgressEvent>
TeleportProgress`, raised by **all three** teleport paths (`TeleportToAsync` region-handle,
`TeleportToLandmarkAsync`, `TeleportToGlobalPosition` fire-and-forget) via a shared private
`OnLmvTeleportProgress` relay. Each path also raises a **synthetic `Started`** the moment the
request is sent (LibreMetaverse does not reliably raise `Start` before `Progress`) and a
**terminal `Finished`/`Failed`** once the awaited call returns — a teleport *timeout* raises no
LibreMetaverse event at all, so an overlay listening only to relayed events would hang. The
terminal is emitted by a `FinishTeleport(result)` wrapper around every `return` in the awaited
methods. 7 new `GridSessionTests` (status→stage mapping `[Theory]`, `None` dropped,
`TeleportToGlobalPosition` no-connection graceful).

**Overlay (`app/scripts/UI/TeleportOverlay.cs`).** A `CanvasLayer` (Layer 100, above `HudLayer`'s
10) with a full-rect blur `ColorRect` (reuses `res://materials/ui_blur.tres`, the login screen's
own material) + centred glass `PanelContainer`, an **indeterminate** spinner ring (`_Draw` arc
sweep — teleport reports discrete stages, not a percentage, so a fake progress bar would lie the
same way the login checklist refuses to), and a status `Label`. Deliberately **not** an
`SLNGWindow` (that standard is for draggable floaters; this is a modal blocking overlay in the
same family as the login `%LoadingScreen`, a plain `CenterContainer`). The backing rect is
`MouseFilter.Stop` so nothing behind it is clickable mid-teleport. Fades in (~0.18 s) / out
(~0.3 s) via `Tween`; a terminal state lingers 0.4 s on success / 3 s on failure (long enough to
read the reason) before fading. `ForceHide()` for the disconnect/relogin edge (its session's
remaining events are gone).

**Wiring (`Boot.cs`).** `_session.TeleportProgress` is buffered into a `ConcurrentQueue` off the
network thread and drained in `_Process` (same buffer-and-drain rule as the arrival toast /
region environment), then `ApplyTeleportProgress` maps stage → overlay call with a **localised**
per-stage string (`ui.teleport.*`, en-US + de-DE) rather than LibreMetaverse's inconsistent
English narration; a real failure reason (timeout, rejection) is passed through verbatim. A
`_teleportActive` flag gates mid-flight `Progress` updates so a stray late event can't revive the
text after the overlay has shown its terminal state.

Builds (solution + `app/`), 376 tests (+7), `dotnet format` clean, `--selftest` 26/26 (locale
parity 212/212). `v0.11.5-alpha`. **Awaiting in-world confirmation.**
