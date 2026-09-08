# Feature: FEAT-AVATAR-03 ("Avatar" top menu + hover-height control)

## Context
Avatar actions are scattered: Rebake and Detach-All live under the *World* menu, and
there is no hover-height control at all. The reference viewers keep an "Avatar" menu
for exactly this. This task adds that menu and the first control unique to it —
hover height — and becomes the home for `FEAT-AVATAR-02`'s troubleshooting tools.

## Requirements
1. **Avatar menu:**
   - New 5th `MenuBar` entry in `TopMenu` (`ui.menu.avatar`), localised en-US + de-DE.
   - Move **Rebake** (`OnRebakeAvatar`) and **Detach All HUDs / Detach All Attachments**
     out of the World menu into it. Leave the World menu's other entries alone.
2. **Hover height:**
   - Slider, range −2.0 … +2.0 m, default 0, with a reset.
   - Send the offset to the sim. Today `HoverHeight` is only *read* from other avatars'
     appearance for the render offset (`GridSession._lastSelfHoverOffsetZ`); there is no
     outbound path. Use the `AgentUpdate` / `SetHoverHeight` (`AvatarHoverHeight` cap)
     mechanism the reference viewer uses; confirm the exact wire form via `protocol-re`
     against LibreMetaverse 3.1.3 before implementing.
   - Persist the last value in `preferences.cfg` and re-apply on login.
   - Reflect it in the local self-avatar render immediately (the existing offset field),
     not only after the sim echoes it back.
3. **Menu is the shell for FEAT-AVATAR-02** — leave a clear insertion point for
   Stop Animations / Reset Skeleton / Reload Avatar.

## Acceptance Criteria
- [ ] An "Avatar" menu exists; Rebake and Detach-All are reachable from it and gone from World.
- [ ] The hover-height slider moves the local avatar up/down and other viewers see the change.
- [ ] The value persists across a relog.
- [ ] `--selftest` locale parity stays green.

## Technical Specs & Affected Files
- `app/scripts/UI/TopMenu.cs`
- `app/scripts/Boot.cs` (wiring, persistence)
- `src/SLNG.Net/GridSession.cs` (outbound hover-height)
- `app/i18n/en-US.json`, `app/i18n/de-DE.json`
