# Feature: FEAT-RENDER-11 (LookAt / PointAt effect indicators)

## Context
Other viewers show where a nearby avatar is looking or pointing (the "LookAt" crosshair
with a name label, and the pointing beam). SLNG does not decode the `ViewerEffect`
message at all, so none of this is visible.

## Requirements
1. **Protocol (`protocol-re`):**
   - Subscribe to LibreMetaverse's `ViewerEffect` handling (`AvatarManager.ViewerEffect*`
     events — `LookAtEffect`, `PointAtEffect`, generic beam/sphere).
   - Convert to engine-neutral DTOs at the `SLNG.Net` boundary (source agent id,
     target agent id or world position, effect type, expiry). No LMV type crosses out.
2. **Render:**
   - LookAt: a small billboarded crosshair at the target point + the source resident's
     name. Only render a subset of LookAt target types (mirror the reference viewer —
     it hides Idle/AutoListen, shows Freelook/Select/Focus etc.).
   - PointAt: a beam from the source avatar's hand to the target.
   - Effects time out on their own expiry; no stale indicators left behind.
3. **UI:**
   - Toggle to show/hide these (default off), in the Avatar or View menu.
   - Respects "Toggle HUD".

## Acceptance Criteria
- [ ] With the toggle on, a nearby avatar's LookAt target shows as a named crosshair.
- [ ] PointAt renders a beam and clears when the gesture ends.
- [ ] Toggle off removes all indicators; no leaks on avatar despawn / region change.
- [ ] Nothing renders for effect types the reference viewer suppresses.

## Technical Specs & Affected Files
- `src/SLNG.Net/GridSession.cs` (ViewerEffect subscription + DTOs)
- `src/SLNG.Core/GridEvents.cs`
- `app/scripts/AvatarRenderer.cs` or a new overlay node
- `app/scripts/UI/TopMenu.cs` (toggle)
