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
     outbound path. **Corrected by `protocol-re` against LibreMetaverse 3.1.3 (by reflection)
     and `scratch/slviewer`'s `LLVOAvatarSelf::sendHoverHeight`:** not an `AgentUpdate` field
     (none exists) and not an "AvatarHoverHeight" cap (no such cap) — it's an HTTP CAPS POST
     `{ hover_height: <metres> }` to the **`AgentPreferences`** capability, already implemented
     as `AgentManager.SetHoverHeightAsync(double)` in the pinned 3.1.3 package. See
     `GridSession.SetHoverHeight`'s doc comment for the full trail, including why this is not
     gated on a `Simulator.Features` check (that property doesn't exist in 3.1.3 — also
     verified by reflection, not assumed).
   - Persist the last value in `preferences.cfg` and re-apply on login.
   - Reflect it in the local self-avatar render immediately (the existing offset field),
     not only after the sim echoes it back.
3. **Menu is the shell for FEAT-AVATAR-02** — leave a clear insertion point for
   Stop Animations / Reset Skeleton / Reload Avatar.

## Acceptance Criteria
- [ ] An "Avatar" menu exists; Rebake and Detach-All are reachable from it and gone from World.
      Implemented (`TopMenu.cs`) and builds/selftests clean, but not yet clicked through in a
      running client -- no GUI available in this session to confirm visually.
- [ ] The hover-height slider moves the local avatar up/down and other viewers see the change.
      Local half implemented (`Boot.ApplySelfHoverHeight`, optimistic, no sim round-trip needed);
      outbound half implemented (`GridSession.SetHoverHeight` -> `AgentManager.SetHoverHeightAsync`
      -> `AgentPreferences` cap) but **unverified against a live grid** -- the OpenSim dev target is
      expected (per protocol-re's source read) to lack this cap entirely, so "other viewers see the
      change" needs an SL/Agni session to actually confirm, same caveat FEAT-AVATAR-03's own spec
      text anticipated.
- [ ] The value persists across a relog. `AvatarHoverSettings` read/write to preferences.cfg is
      implemented and follows the existing DofSettings/CameraSettings pattern exactly, but an
      actual relog was not performed.
- [x] `--selftest` locale parity stays green. 281/281 keys, de-DE covers en-US fully (verified
      2026-09-11, `v0.22.31-alpha`).

## Technical Specs & Affected Files
- `app/scripts/UI/TopMenu.cs`
- `app/scripts/Boot.cs` (wiring, persistence)
- `src/SLNG.Net/GridSession.cs` (outbound hover-height)
- `app/i18n/en-US.json`, `app/i18n/de-DE.json`
