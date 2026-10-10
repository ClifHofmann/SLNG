# [BUG-PERF-16] Park over-cap avatars instead of unloading them

- **Feature ID:** `BUG-PERF-16`
- **Track:** `render` / `perf`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md) -- follows ADR 0004 (P3 first step)

## Overview & Goal

Under ADR 0004 ("An avatar is one character, not N attachments"), our most expensive moments are rebuilds. Previously, when an avatar went over-cap or outside the draw distance, `SetAvatarReduced` immediately destroyed all its rigged meshes (`DiscardRiggedAttachment`, `rMi.QueueFree()`), static attachment nodes (`attachNode.QueueFree()`), and unpinned its textures. When the camera swung or distance changed, `SetAvatarFull` had to rebuild and re-rig the entire outfit from scratch (`CallDeferred(UpdateAttachment)`), causing severe frame drops, worker queue storms, and visual pop-in.

BUG-PERF-16 changes avatar reduction from an **unload** to a **park**:
1. **Park on reduction:** When an avatar with a loaded outfit goes over-cap (`SetAvatarReduced`), its outfit data is kept in memory. Rigged attachments have their skins detached (`DetachSkeleton`) and visibility disabled (`rMi.Visible = false`), and static attachments are hidden and paused (`ProcessMode = Disabled`). Godot skips skin bind calculation and RenderingServer updates entirely (0 draws, 0 bound binds). The avatar is displayed as a jelly doll stand-in.
2. **Instant unpark on promotion:** When promoted back to full (`SetAvatarFull`), skins are reattached (`ReattachSkeleton`), meshes and attachment nodes are made visible, and part materials are restored. No network fetch, no mesh decode, and no worker rigging occurs. A camera swing costs nothing.
3. **Unload under memory pressure:** Outfits are only freed under memory pressure (`GpuCache.IsOverBudget`, evicting farthest parked outfits first) or after being hidden outside the draw distance for an extended period (`AvatarHiddenUnloadSeconds = 60s`).
4. **Never-loaded fallback:** Avatars arriving over-cap continue to start reduced as cheap jelly dolls without loading outfits until their first promotion.

## Acceptance Criteria
- [x] `AvatarVisual.IsParked` and `HasLoadedOutfit` track parked vs never-loaded avatars.
- [x] `SetAvatarReduced` parks already-loaded avatars (detaches skins via `DetachSkeleton`, hides meshes, keeps nodes/materials) instead of destroying them.
- [x] `SetAvatarFull` unparks parked avatars instantaneously without re-fetching or re-rigging attachments.
- [x] `UnloadAvatarOutfit` handles full destruction under memory pressure (`GpuCache.IsOverBudget`) or extended hidden time (`>60s`).
- [x] Self-test verifies parking, unparking, and memory-pressure unload lifecycles.
- [x] `dotnet test` and headless `--selftest` pass cleanly; `AppVersion` bumped to `v0.27.32-alpha`.

## Technical Specs & Affected Files
- `app/scripts/AvatarRenderer.cs`
- `app/scripts/AvatarRenderer.WornOverrides.SelfTest.cs`
- `app/scripts/GpuCache.cs`
- `app/scripts/Boot.cs`
- `docs/ROADMAP.md`

## Sub-tasks / Progress
- [x] Create spec `BUG-PERF-16-park-over-cap-avatars.md`
- [x] Expose `GpuCache.IsOverBudget`
- [x] Implement parking and instant unparking in `AvatarRenderer`
- [x] Implement `UnloadAvatarOutfit` and memory pressure eviction
- [x] Update `SelfTestAvatarReductionLifecycle` to verify the park/unpark/unload contract
- [x] Verify with `dotnet test`, `godot --headless --selftest`, and `dotnet format`
- [x] Bump `AppVersion` in `Boot.cs` and commit
