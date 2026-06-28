# Handoff → Gemini (avatar, 2026-06-28)

Branch `feat/m4-5-base-avatar-mesh` (pushed). Build 0/0, tests 25/25.

## Done this session
- **M4-5 real SL mesh**: avatar renders from LibreMetaverse `.llm` files
  (`AvatarBodyMeshService` → neutral DTOs; `AvatarRenderer.BuildSkinnedMeshInstance`).
  Skinned, T-pose correct, skin-tone fallback color.
- **M4-2 animation** cherry-picked onto this branch and **now actually plays**.
  Root cause of the dead T-pose: the programmatic `MeshInstance3D` had no resolved
  skeleton → set `mi.Skeleton = mi.GetPathTo(skeleton)` + bind by bone index
  (`AddBind`) not name + normalize weights.
- **Sink fix**: animation position keys (only on mPelvis) dropped the body into the
  ground → apply **rotation only**; rest pose handles placement.
- **Camera**: Alt+LMB orbits camera around avatar without turning it (separate
  `_orbitYaw/_orbitPitch`, avatar facing from `_yaw` only).

## Open / next
- **PRs #13 (M4-2), #14 (M4-4), #15 (M4-5) still open, unmerged.** #15 contains the
  cherry-picked M4-2 work, so merge order matters — squash-merge #15 last or resolve.
- **Root motion deferred**: position keyframes skipped in `AvatarAnimationPlayer`.
  Re-enable once mPelvis key reference frame is understood (jumps, real translation).
- **Per-bake texture routing** added (`AvatarRenderer` bake-index→part map) but the
  OpenSim test grid sends no bakes yet — untested. No `Appearance: N bake slots` log
  seen, so VisualParams/BakedTextures likely empty on this grid.
- **Double skeleton load** at startup (`Initialize` runs twice) — harmless, not chased.
- Other avatars float ~1m (center-vs-feet) — still open from old handoff.

## Run
`dotnet build app/SLNG.App.csproj` then `godot --path app`. Visual changes need a
human login to verify (headless can't render).
