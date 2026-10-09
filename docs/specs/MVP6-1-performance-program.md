# [MVP6-1] Performance program: faster than the reference viewer on crowded sims

- **Feature ID:** `MVP6-1` (umbrella). Tasks `FEAT-PERF-12` … `FEAT-PERF-22`.
- **Track:** `render` / `assets`
- **Status:** `🚧 In Progress` (plan written 2026-10-09)
- **Owner:** `claude` (umbrella). Per-task owners in the table below.
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

The goal is not parity, it is a viewer that runs faster than Firestorm on the same machine and sim.
Firestorm is a single-threaded OpenGL renderer that is decades old. Godot 4 gives us things it
cannot have: a separate render thread, culling in C++, GPU-compressed textures and occluders.
We use almost none of them today. This plan orders the work by gain per effort, measured on a
fixed test spot.

## Baseline (2026-10-09, v0.27.28, Sirens Beach on Agni, 58 avatars, cap 7, RTX 4070 12 GB)

| Measure | Value |
|---|---|
| fps / meanMs | 10–11 / ~90 |
| Our C# phases (`scriptsMs`) | ~30 ms per frame |
| Godot `postFlushMs` | 12–17 ms |
| Render submission (`drawMs` / `renderCpuMs`) | ~45 / 25–28 ms |
| `renderGpuMs` | ~18 ms (GPU is not the limit) |
| Draw calls | 21.7k per frame: scene 11.2k, shadow 4.2k, planar mirror ~6k |
| Triangles | ~40M per frame (all passes) |
| VRAM | ~7.5 GB |
| `[PhaseCost]` ms per second | obj-ticks 60 (texture animation 51), avatar-render 58, cull 42, world-drain 37, extrapolate 13 |
| `[AvatarCost]` | 8 full, 50 reduced, boundSkinBinds 47k (was 306k at v0.27.27; postFlush did not move, so skin binds are not its cause) |

**How to read it.** The frame runs serially on one thread: our C# (~30 ms), then Godot's flush
(~13 ms), then render submission (~45 ms). The GPU finishes in ~18 ms. The frame is CPU-bound
three ways, and today every millisecond cut anywhere counts in full.

## Principles

1. **Measure before and after, at the same spot with the same settings.** Use the `[Perf]`,
   `[PhaseCost]`, `[WorkCost]` and `[AvatarCost]` lines. For the Firestorm reference, match the
   settings: draw distance, shadows, avatar cap, mirrors off.
2. **Move per-frame C# work into Godot's C++** (or onto the GPU) wherever Godot has the
   feature.
3. **Do not draw what cannot be seen or does not matter.** In order: cull by size, cull by
   occlusion, batch what is left.
4. **Match the reference viewer's cheap defaults.** Mirrors are off in every preset
   (`scratch/slviewer/indra/newview/featuretable.txt:120,163,206,248,290,332,374`). Sun
   shadows start at High (`RenderShadowDetail`, `:283`).
5. **One writer at a time.** `E:/Git/SLNG` is a single shared checkout with no worktrees, so a
   coding session (Claude or agy) owns it until it has committed. The queue below is the order.

## Plan

### Phase 0: groundwork

| ID | Task | Expected gain | Notes |
|---|---|---|---|
| — | Merge `fix/FEAT-PERF-08-reduced-avatar-release` (v0.27.23–28: BUG-PERF-13 + FEAT-PERF-08 follow-ups) to `main` | — | In-world 2026-10-09: avatars appear, fps slightly up. |
| FEAT-PERF-12 | Baseline protocol and breakdown | — (decides the order of everything below) | See the scope below. |

**FEAT-PERF-12 scope:**
- **Test spots:** one crowded spot (Sirens Beach) and one quiet spot.
- **Firestorm reference:** Firestorm's fps at both spots, with matched settings.
- **Split `postFlushMs` (~13 ms):** node and object counts from Godot `Performance` monitors,
  and what moves every frame (extrapolation, name tags, BoneAttachment3D).
- **Split `avatar-render` (~5 ms):** `[WorkCost]` sub-labels.
- **Output:** `docs/perf/baseline-2026-10.md`.

### Phase 1: quick wins (hours each)

| ID | Task | Expected gain | Risk |
|---|---|---|---|
| FEAT-PERF-13 | **Separate render thread.** Godot `rendering/driver/threads/thread_model`, starting as an A/B setting. | The frame becomes ≈ max(main, render) instead of their sum: ~90 ms toward ~50 ms at the baseline spot. The largest single lever. | RenderingServer calls or read-backs made off the main thread; getters that need an immediate RS result. Audit them first. |
| FEAT-PERF-14 | **Small-object culling by size.** `GeometryInstance3D.VisibilityRangeEnd` (+ margin) from the prim's bounding radius. The constant comes from the reference viewer's screen-size LOD rule (`LLVOVolume::calcLOD`, `RenderVolumeLODFactor`). | Godot culls in C++ at 0 ms of C#, and a club's small decor prims no longer draw (thousands of draws). | Must compose with the BUG-PERF-11 cull sweep, LOD swaps, FEAT-PERF-06 MultiMesh groups and selection. |
| FEAT-PERF-15 | **Mirrors setting**, off on every preset (viewer parity, see principle 4). Applies at runtime. | Where a mirror is active: −7 ms CPU, −4 ms GPU, −6k draws. | Low. |

### Phase 2: days each

| ID | Task | Expected gain | Risk |
|---|---|---|---|
| FEAT-PERF-16 | **Texture animation in the shader.** Write the parameters once and evaluate from `TIME`, replacing the per-frame `TickTextureAnimations`. | −4–5 ms per frame | Exact parity with `TextureAnimator` (flipbook, ping-pong, slide, rotate, scale); `TIME` rollover. |
| FEAT-PERF-17 | **GPU-compressed textures** (BC1/BC3/BC7), both in VRAM and in the decoded disk cache (FEAT-PERF-11). | ~4× less VRAM (7.5 GB → ~2 GB), faster uploads, no budget shrinking, so sharper textures. Firestorm does not do this by default. | Godot's `Image.Compress` encoders may be editor-only in export templates. Check that first; otherwise use a C# or native BC encoder on workers. Alpha and Bakes-on-Mesh quality. |
| FEAT-PERF-18 | **Avatar per-frame cost**, approach chosen from FEAT-PERF-12's breakdown. | Part of avatar-render's ~5 ms, scaling with the avatar cap | Candidates: native Godot `Animation` playback of SL anims (C++), parallel sampling on workers, name-tag projection only for tags in range. SL priority blending must stay exact. |
| FEAT-PERF-19 | **FPS target ("auto-tune").** A governor adjusts draw distance, avatar cap, shadows and LOD factor within the chosen preset to hold a target fps. Model it on the reference viewer's auto-tune (`llperfstats`, `llfloaterperformance`). | Steady fps on any sim, without the user tuning settings | Oscillation. Needs hysteresis and slow steps. |

### Phase 3: weeks each

| ID | Task | Expected gain | Risk |
|---|---|---|---|
| FEAT-PERF-20 | **Batch static prims.** FEAT-PERF-06 P1 covers only identical meshes (MultiMesh, ~15 %). Go beyond it: merge different static prims that share a material per spatial cell, and/or draw static prims as RenderingServer instances instead of scene nodes (that also attacks `postFlushMs`). ADR first. | Draws scale with materials per cell rather than faces. This is the reference viewer's `LLSpatialGroup` batching. | Picking, selection, per-face updates, texture animation, sorted alpha surfaces and LOD swaps all need per-prim handles. |
| FEAT-PERF-21 | **Occlusion culling.** Runtime `BoxOccluder3D`/`ArrayOccluder3D` from large opaque prims (walls, floors, shells), plus `rendering/occlusion_culling/use_occlusion_culling`. FEAT-PERF-06 P2 parked this. | Indoors and in clubs, most of the scene behind walls stops drawing | False occlusion from alpha or phantom-looking prims. Occluder rebuild cost. |
| FEAT-PERF-22 | **Avatar impostors** for over-cap avatars (design first). | Visual quality of reduced avatars (jelly doll → real image). Not fps. | Needs the outfit loaded, which v0.27.28 stopped doing for over-cap avatars. Needs a load policy (lowest LOD, reduced discard, within N m). |

## Order (one writer at a time)

Merge v0.27.28 → FEAT-PERF-12 → FEAT-PERF-13 → FEAT-PERF-15 → FEAT-PERF-14 → FEAT-PERF-16 →
FEAT-PERF-17 → FEAT-PERF-18 → FEAT-PERF-19 → FEAT-PERF-20 → FEAT-PERF-21 → FEAT-PERF-22.

- **Why FEAT-PERF-13 comes early:** with a separate render thread the frame becomes max(main,
  render) instead of their sum. Which side then dominates decides whether C# savings (16, 18)
  or draw-call savings (14, 20, 21) come first.
- **Reorder after each step** from its measurement.

**File ownership** (one item per set at a time):

| Set | Files | Items |
|---|---|---|
| Engine | `app/project.godot`, RenderingServer call sites | 13 |
| Objects | `ObjectRenderer.cs`, prim shaders | 14, 16, 20, 21 |
| Settings | `GraphicsSettings.cs`, `GraphicsPreferencesPage.cs`, `Boot.cs` | 15, 19 |
| Assets | `GpuCache.cs`, `AssetService.cs`, `DecodedTextureCache.cs` | 17 |
| Avatars | `AvatarRenderer*.cs`, the animation player | 18, 22 |

## Targets (crowded spot, settings matched to Firestorm)

These are targets, to be revised once FEAT-PERF-12 has Firestorm's numbers.

- After Phase 1: ≥ 20 fps.
- After Phase 2: ≥ 30 fps, VRAM ≤ 4 GB.
- After Phase 3: at or above Firestorm's fps, and draws < 6k.

## Acceptance Criteria

- [ ] Every item ships a before/after measurement at the baseline spot in its spec.
- [ ] No visual regression is accepted for a gain unless the user signs off.
- [ ] The Phase targets above are met, or revised with the reason written here.

## Sub-tasks / Progress

- [ ] Merge v0.27.28
- [ ] FEAT-PERF-12 Baseline protocol and breakdown
- [ ] FEAT-PERF-13 Separate render thread
- [ ] FEAT-PERF-15 Mirrors setting, off by default
- [ ] FEAT-PERF-14 Small-object culling by size
- [ ] FEAT-PERF-16 Texture animation in the shader
- [ ] FEAT-PERF-17 GPU-compressed textures
- [ ] FEAT-PERF-18 Avatar per-frame cost
- [ ] FEAT-PERF-19 FPS target (auto-tune)
- [ ] FEAT-PERF-20 Batch static prims
- [ ] FEAT-PERF-21 Occlusion culling
- [ ] FEAT-PERF-22 Avatar impostors
