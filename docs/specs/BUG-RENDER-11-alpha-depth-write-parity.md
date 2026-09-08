# [BUG-RENDER-11] Alpha world-prim faces (grass, foliage) flip/pop under camera rotation

- **Feature ID:** `BUG-RENDER-11`
- **Track:** `render`
- **Status:** `✅ Done` (grass confirmed in-world; hair split to BUG-RENDER-12)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Symptom

Grass / foliage world objects (example prim
`7e47328b-3f56-2d7c-cab2-7c8189059f44`) "flip away" — chunks pop in and out as the
camera rotates. **Firestorm renders the same content stable.**

## Evidence (`--diag`, SL region *Millenium*)

`[FaceAlpha]` verdicts, one per distinct world-prim texture:

| pattern | count |
|---|---|
| `legacyMat mode=None … -> Opaque` | 1293 |
| **`noMat detectAlpha=Blend tintTranslucent=False -> Blend (SORTED transparent pass)`** | **257** |
| `noMat detectAlpha=Blend tintTranslucent=True -> Blend` | 142 |
| `legacyMat mode=Mask … -> Scissor` | ~180 |
| `noMat detectAlpha=Bit tintTranslucent=False -> Scissor` | 18 |

The 257 are the bug: legacy faces with no material whose texture `Image.DetectAlpha()`
flagged `Blend` for a single intermediate-alpha texel, so `ApplyAlphaCutout` routed
them to `PrimShaderFamily.Blend` — the sorted transparent pass, no depth write →
z-fights itself → pops. Godot's transparent queue sorts only per object by AABB-centre
distance, far coarser than the viewer's, so content that is stable in Firestorm pops
here.

## Fix — undeclared alpha → cutout (a deliberate divergence)

A faithful port of `LLImageGL::analyzeAlphaData()` (llimagegl.cpp:2191) classified
only 8 of ~130 `DetectAlpha=Blend` foliage textures as maskable — the viewer's 2x2
box-sample "mid-skew" is *designed* to keep high-frequency alpha (thin grass, wispy
strands) OUT of the mask pass, so the reference viewer blends those too. The reason
Firestorm doesn't visibly pop is the **sort granularity**, which no shader mode fixes.

So Fix diverges on purpose: in `ObjectRenderer.ApplyAlphaCutout` (reached only for a
face with **no material and no translucent tint** — the creator declared nothing, the
texture merely carries an alpha channel), the face is treated as an alpha **cutout**
unconditionally.

- `GpuCache.AnalyzeAlphaMaskable` — worker-thread port of `analyzeAlphaData`, next to
  the existing `DetectAlpha()` call, same decoded pixels. `TryGetIsAlphaMaskable(id)`.
- `ApplyAlphaCutout` → `PrimShaderFamily.Scissor` always (depth-writing, opaque queue,
  order-independent; `alpha_to_coverage` + project 4x MSAA soften the edge). The
  `analyzeAlphaData` verdict only picks the cutoff: clean cutout `@0.5`,
  high-frequency `@0.33` (the viewer's own DoF-pass minimum-alpha).
- Genuine translucency (explicit legacy `mode=Blend`, glTF `AlphaMode.Blend`, or a
  translucent per-face tint) resolves to `Blend` *before* this method and is unaffected.
- `[FaceAlpha]` logs `maskable=<bool>` and the chosen `@threshold`.

**Verified in-world:** "grass passt".

## Also in this change

Removed the unconditional `[AnimPlayer] +id` and `[Locomotion] self anim set` /
`predict … (applied)` prints (deleted or moved behind `--diag`). While walking
through an AO they emitted ~183 of 644 log lines and buried the `[FaceAlpha]` /
`[AvatarAlpha]` diagnostics.

## Files

- `app/scripts/GpuCache.cs` — `AnalyzeAlphaMaskable`, `_alphaMaskable`,
  `TryGetIsAlphaMaskable`, wired into `PrepareImageAsync`.
- `app/scripts/ObjectRenderer.cs` — `ApplyAlphaCutout` cutout-unconditionally + log.
- `app/scripts/AvatarAnimationPlayer.cs`, `app/scripts/AvatarRenderer.cs` — log flood removed.
- `app/scripts/Boot.cs` — `AppVersion` → `v0.21.13-alpha`.

## Split out

Worn-mesh **hair** flicker → **BUG-RENDER-12**. Same class (undeclared alpha on a
mostly-hole cutout sheet) but avatar-side, where `Surface.Avatar` is `cull_disabled`,
there is no per-triangle transparency sort, and MSAA `alpha_to_coverage` shimmers on
thin high-frequency geometry without TAA. Five attempts here (route to `Kind.Scissor`
at a low threshold; `depth_draw_always`; `+ cull_back`; a double-sided twin;
`hairSheet → Surface.WorldPrim` cull_back) each only shifted the symptom — the last
produced "dunkelbraune Flächen beim Zoomen" (inverted-lit backfaces or self-shadow).
All reverted; hair is back to its prior state (still flickering, not worse). Needs a
dedicated `graphics-engineer` / `viewer-parity` pass with in-editor experimentation.
