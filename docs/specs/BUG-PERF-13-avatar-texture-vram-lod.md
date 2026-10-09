# [BUG-PERF-13] Avatar textures: distance LOD + count them in the VRAM budget

- **Feature ID:** `BUG-PERF-13`
- **Track:** `render`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md)

## Reference Viewer Analysis

In Second Life reference viewer (`scratch/slviewer`):
- `indra/llrender/llgltexture.h:50-63`:
  Defines `EBoostLevel`:
  - `BOOST_NONE = 0`
  - `BOOST_AVATAR = 1`
  - `BOOST_AVATAR_BAKED = 2`
  - `BOOST_HIGH = 10`
  - `BOOST_AVATAR_BAKED_SELF = 61`
  - `BOOST_AVATAR_SELF = 62`
- `indra/newview/llvoavatar.h:778-779` & `indra/newview/llvoavatarself.h:167-168`:
  `LLVOAvatar` returns `BOOST_AVATAR` and `BOOST_AVATAR_BAKED` for other avatars.
  `LLVOAvatarSelf` overrides these to return `BOOST_AVATAR_SELF` and `BOOST_AVATAR_BAKED_SELF`.
- `indra/newview/llvoavatar.cpp:5794-5799` & `5900-5914`:
  `addBakedTextureStats(imagep, mPixelArea, texel_area_ratio, boost_level)` sets
  `imagep->addTextureStats(pixel_area / texel_area_ratio)` and `imagep->setBoostLevel(boost_level)`.
- `indra/newview/llviewertexturelist.cpp:898-988` (`LLViewerTextureList::updateImageDecodePriority`):
  For `imagep->getBoostLevel() < LLViewerFetchedTexture::BOOST_HIGH` (which includes other avatars' `BOOST_AVATAR` and `BOOST_AVATAR_BAKED`),
  the texture list iterates faces and scales virtual size `vsize` based on screen pixel area (`face->calcPixelArea(...)` / `face->getPixelArea()`).
  Self avatar textures have boost level >= `BOOST_HIGH`, so they skip face virtual size down-scaling and remain full resolution.
- `indra/newview/llviewertexture.cpp:3022-3115` (`LLViewerLODTexture::processTextureStats`):
  Desired discard level is computed as `floorf(log(mTexelsPerImage / mMaxVirtualSize) / log_4)`.
  When memory pressure demands downscale, `scaleDown()` executes for `mBoostLevel < LLGLTexture::BOOST_AVATAR_BAKED`.

**Summary:**
- **Own avatar:** Stays full resolution (`BOOST_AVATAR_SELF` / `BOOST_AVATAR_BAKED_SELF` >= `BOOST_HIGH`).
- **Other avatars:** Follow screen area (`mMaxVirtualSize` from distance and size). They take discard based on distance and can be shrunk when under VRAM budget pressure.

## Overview & Goal

Measured 2026-10-09 at Secret Love (Agni) with 15 avatars on an RTX 4070 (12 GB):
Dedicated VRAM hit 11.6/12 GB, spilling ~1 GB into shared memory, causing frame times of 345–427 ms (7 fps).
`[GpuCache] sizeMB=5761/3840 pinned=7096 pinnedMB=5761 noShrinkMB=4817 lodBias=2`.
Avatar textures (~4.8 GB) were unconditionally uploaded at full resolution because `AvatarRenderer` passed `screenPixelArea: 0f` and `rejectDegraded: true` / `bakeChannel: ...`, which added every texture to `GpuCache._noShrink`. They were pinned forever with `initialRefCount: 1` and never released when avatars left or were culled far away.

This feature implements:
1. Pure distance-to-screen-area calculation for avatars in `TextureLod` (`ScreenPixelAreaForSphere`).
2. Other avatars' worn-mesh and BoM textures receive screen-area-based discard computed from avatar distance and size.
3. Own avatar textures stay full-resolution (`screenPixelArea: 0f`).
4. Distance bands: When an avatar moves closer (area grows 4x), textures re-sharpen in place using `TryUpgradeCachedTexture`.
5. Avatar textures count toward `texture_memory_mb`. Only self textures go into `_noShrink`; remote bakes stay full-resolution at upload (`screenPixelArea 0`) but are NOT in `_noShrink` and can be shrunk or evicted.
6. When an avatar visual is removed (`RemoveVisual`), detached, or reduced, its pinned refcounts are released and texture IDs are unregistered from `_avatarTextures` and `_noShrink`, with unreferenced bake textures evicted immediately.
7. `[GpuCache]` stats log extended with `avatarMB` and `reducibleMB`.

## Acceptance Criteria
- [x] Pure arithmetic in `TextureLod` for sphere screen area and discard level.
- [x] Unit tests for discard/budget arithmetic passing.
- [x] Regression check in `--selftest`: remote bakes exempt from `_noShrink`, and avatar removal unpins, unregisters, and evicts textures.
- [ ] In-world (unverified): At a 15-avatar spot, `[GpuCache] sizeMB` stays at or under budget, dedicated VRAM stays below card size, and GPU time returns to ~10 ms baseline while near avatars stay sharp. Teleporting to an empty sim reclaims remote VRAM within ~30s.
