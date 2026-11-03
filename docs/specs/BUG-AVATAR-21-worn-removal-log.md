# [BUG-AVATAR-21] Worn items vanish, no log reason

- **Feature ID:** `BUG-AVATAR-21`
- **Track:** `avatar` / `render`
- **Status:** `🚧 In Progress`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md)

## Overview & Goal

When a worn attachment (item) of an avatar disappears or is removed, previously only `[JointOverride] … taken off` lines were logged without indication of the removal origin or the state of the avatar and stream.

This task adds diagnostic logging for worn attachment removals without changing any behavior:
1. Distinguishes removal origins (`reason` parameter in `RemoveVisual`, e.g. `"entity-removed"`, `"drop-worn"`).
2. Logs a `[WornRemoved]` diagnostic line for every removed attachment:
   `[WornRemoved] wearer=<id|SELF> item=<id> mesh=<id|--------> reason=<reason> wearerDist=<dist> wearerShown=<bool> wearerReduced=<bool> wearerParked=<bool> wornLeft=<n>`
3. Logs a frame summary line if an avatar removes more than 3 attachments in a single frame:
   `[WornRemoved] wearer=<id> removed <n> items in one frame (wornLeft=<m>)`

## Acceptance Criteria

- [ ] `RemoveVisual(Guid entityId, string reason = "entity-removed")` tracks removal reason.
- [ ] `DropWornVisuals` passes `"drop-worn"`.
- [ ] `[WornRemoved]` logged when an entry in `_attachmentToAvatar` is removed.
- [ ] Mass removals (> 3 items per wearer in one frame) log a summary line.
- [ ] `AppVersion` bumped (patch).
- [ ] Clean build and test passes via `/slng-verify`.

## Technical Specs & Affected Files

- `app/scripts/AvatarRenderer.cs`
- `app/scripts/Boot.cs`
- `docs/ROADMAP.md`
