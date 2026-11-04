# [BUG-AVATAR-22] Aux3 BoM channel unresolved on a stranger's item, log bake channels

- **Feature ID:** `BUG-AVATAR-22`
- **Track:** `avatar` / `render`
- **Status:** ✅ Done
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md)

## Overview & Goal

A Bakes-on-Mesh item (Aux3 channel 44) remains white on SLNG while Firestorm displays a tattoo overlay there. The current log does not list the bake channels received for remote avatars, making it impossible to determine whether the Aux3 texture never arrives from the sim, is lost during decoding/filtering, or arrives too late.

This task adds diagnostic logging (additive, `--diag` only, no behavior change) to trace the bake pipeline for remote avatars:
1. **Raw appearance packets (`GridSession.Avatar.cs`):** logs faceTextures array length, whether it is truncated (`short=true` when < 45), indices ≥ 8, and channels 40–44 presence.
2. **Renderer loaded bakes (`AvatarRenderer.cs`):** logs loaded textures on remote avatars and whether the avatar is reduced.
3. **Re-resolve path:** logs when a bake channel arrives and how many BoM meshes are re-evaluated.
4. **UNRESOLVED BoM face diagnostics:** includes the list of currently loaded bake channels `(loaded: 8,9,10,...)` when a channel cannot be resolved.

## Acceptance Criteria

- [x] `[AvatarBakes]` logged in `GridSession.Avatar.cs` with raw appearance indices and `has40-44` presence, deduplicated per avatar.
- [x] `[AvatarBakes]` logged in `AvatarRenderer.cs` with `loaded=[...]` and `reduced=<bool>` when LoadedTextures changes, deduplicated.
- [x] `[AvatarBakes]` logged when a bake channel arrives and triggers re-resolving of BoM meshes.
- [x] `[BomFace]` appends `(loaded: ...)` on UNRESOLVED.
- [x] Clean build and tests pass via `/slng-verify`.
- [x] AppVersion bumped (v0.29.3-alpha).

## Technical Specs & Affected Files

- `src/SLNG.Net/GridSession.Avatar.cs`
- `app/scripts/AvatarRenderer.cs`
- `app/scripts/Boot.cs`
- `docs/ROADMAP.md`
