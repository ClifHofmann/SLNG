# [FEAT-RENDER-01] Custom Spatial Shader Family for World Surfaces

- **Feature ID:** `FEAT-RENDER-01`
- **Track:** `render`
- **Status:** `⏸️ Pending`
- **Owner:** _(unclaimed)_
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **ADR:** [0002-custom-spatial-shader-family.md](file:///E:/Git/SLNG/docs/adr/0002-custom-spatial-shader-family.md)

## Overview & Goal

Replace `StandardMaterial3D` with one custom Godot **spatial** shader family shared by
`ObjectRenderer`, `AvatarRenderer`, terrain and water. Two things force it (full argument
in ADR 0002):

- **SL face rotation cannot be expressed today.** `StandardMaterial3D` has `Uv1Scale` and
  `Uv1Offset` only, so `ObjectRenderer.BuildFaceMaterialAsync` drops any rotation that is
  not 0 or ±π. 49 faces at exactly π/2 were measured in a single view of OSGrid's Dangazi
  Forest, visibly mis-placing textures against Firestorm.
- **Windlight / EEP needs a per-fragment atmospherics hook on every surface.** There is
  no such seam in `StandardMaterial3D`, and a hybrid (custom shader only where needed)
  would leave neighbouring surfaces visibly diverging at sunset.

The UV transform (scale, offset, rotation) goes in the **vertex** shader. The shader
carries an atmospherics `#include` seam that starts as a no-op. Windlight parameters go
in Godot **global shader uniforms** (`RenderingServer.GlobalShaderParameterSet`), set
once per frame, never per-material.

**This is not a PBR reimplementation.** The shader assigns `ALBEDO`, `ALPHA`,
`NORMAL_MAP`, `METALLIC`, `ROUGHNESS`, `EMISSION`; Godot's lighting, shadows, GI and
post-processing continue to apply. Do not write lighting code.

Scope note: this is the highest-blast-radius change in the project so far — it touches
every surface in view, and live-test rounds are expensive. The phasing below exists
specifically so each step is independently verifiable. **Do not collapse phases.**

## Functional Requirements

- **One shader family, not one shader.** `render_mode` is compile-time in Godot, so the
  family needs a small set of variants: opaque + back-culled, alpha-blend, alpha-scissor.
  Keep the count minimal; `StandardMaterial3D` does the same internally. Any pressure to
  add a fourth/fifth variant is a signal to re-read ADR 0002's revisit condition.
- **Vertex-shader UV transform.** SL's centred convention must be preserved exactly:
  `u' = (u - 0.5) * repeat + 0.5 + off`, with rotation applied about the face centre
  (0.5, 0.5). ±π must remain equivalent to the current negated-repeat handling.
- **Atmospherics seam.** A single include point in the fragment path, shipping as a
  no-op that does not alter output. Phase 1 must be byte-for-byte indistinguishable with
  the seam present.
- **Global uniforms for anything frame-varying.** Windlight (blue-horizon, blue-density,
  haze-horizon, haze-density, distance-multiplier and friends) is set once per frame via
  `RenderingServer.GlobalShaderParameterSet`. Per-material properties for these would
  mean CPU-side churn across thousands of objects every frame — explicitly rejected.
- **Feature parity checklist.** All of the following are in use today and must survive:
  `AlbedoColor` + `AlbedoTexture`; per-face `Uv1Scale`/`Uv1Offset`; `TextureFilter`
  including `LinearWithMipmapsAnisotropic`; `CullMode.Back`; the three transparency modes
  (`Alpha`, `AlphaScissor` with `AlphaScissorThreshold` + `AlphaAntialiasingMode`,
  `AlphaHash` with `AlphaHashScale`); `Metallic`/`Roughness`/`EmissionEnabled`/`Emission`
  and the PBR texture set (base colour, normal, metallic-roughness, emission);
  `AvatarRenderer`'s `AlphaHash` path.
- **Do not disturb mesh sharing.** `ObjectRenderer._primShapeKeys` shares meshes between
  identical prim shapes. Rotation must not be baked into UVs and must not become part of
  the shape key — that was rejected in ADR 0002 on both VRAM (256 MB `GpuCache` budget)
  and correctness grounds.
- **Threading unchanged.** Material construction stays on the existing async path;
  resource assignment and scene mutation stay on the main thread via the current
  `Callable.From` / deferred pattern. This task changes *what* material is built, not
  *where*.

## Pre-migration performance baseline

Captured 2026-08-01 on `v0.3.71-alpha`, i.e. the last build before any shader work, via
**Developer → Measure Render Baseline (10s)** (`app/scripts/RenderBaselineSampler.cs`).
The after-measurement must use the same menu action, the same build machine, the same
place and the same camera transform — a comparison is meaningless otherwise.

```
frames=835 medianMs=11.46 p95Ms=14.81 worstMs=91.45
drawCalls=5867 primitives=32262752 objects=6698 videoMemMB=2573.6
```

Protocol, and it must be followed exactly for the after-run: **log in to OSGrid's The
Dangazi Forest, do not move the avatar or the camera at all, and take the measurement from
the login spawn point.** Standing still at the spawn is what makes this reproducible —
position and camera transform are then identical between runs by construction, with no
attempt to walk back to a remembered spot.

Wait for the scene to finish streaming before triggering it. `objects` climbed 6646 -> 6698
between two runs taken at slightly different moments after login, and the 91 ms outlier is
most likely an asset upload still in flight. Watching `drawCalls` settle (it was byte-identical
at 5867 once loaded) is the cheapest way to tell the scene has stopped changing.

Notes on reading these:
- The sampler disables V-Sync for its window on purpose. An earlier baseline taken with
  V-Sync on reported `medianMs=31.25` — exactly two refresh periods on this ~64 Hz display.
  Frame times snap to whole refresh intervals in that mode, so a change of ±40% can report
  an identical figure. Any measurement showing a suspiciously round median is invalid.
- Judge Phase 1 on **median and p95**, not on `worstMs`: the 91 ms outlier here is a
  one-off hitch (asset upload or GC), and a single sample of it says nothing about the
  shader swap either way.
- `drawCalls` was byte-identical across two independent runs, so it is the most sensitive
  regression signal available here: material-batch fragmentation would show up there long
  before it moved a noisy timing figure.
- `videoMemMB=2573` against the `GpuCache`'s nominal 256 MB budget is a pre-existing
  finding, unrelated to this task and not caused by it. Recorded so a later reading of the
  same number is not mistaken for a regression introduced by the shader family.

## Phase 1 implementation notes

**Status: implemented, awaiting the live visual + performance comparison.** `ObjectRenderer`
now builds `ShaderMaterial`s from the family; `PrimShaderFamily` (`app/scripts/`) holds the three
`Shader` resources and the cached uniform `StringName`s.

**Structural gotcha, resolved as follows.** The material's transparency used to be *mutated after
creation*: `ApplyAlphaCutout` runs in a deferred callback once the texture has loaded and flipped
`Transparency`. With `ShaderMaterial` the equivalent is **swapping `material.Shader`**, since
`render_mode` is compile-time. So the variant decision lives in two places (creation, and again on
texture arrival) and they must agree. `ApplyAlphaCutout` now takes `tintIsTranslucent` as a
parameter instead of reading the old `Transparency != Alpha` back off the material — the caller
already knows the fact, and re-deriving it from "which shader is assigned" would be an indirect
restatement that can silently drift.

**Shader-parameter survival across a swap was verified, not assumed.** A throwaway headless probe
set `albedo_color` / `uv_scale` / `albedo_texture` / `has_albedo_texture`, then reassigned
`material.shader` twice; all values survived. This was worth checking because the failure mode is
silent: every alpha-tested face would render untextured. Note that
`RenderingServer.material_get_param` returns `<null>` under `--headless` (dummy renderer), so only
the `ShaderMaterial` side of that probe is informative.

Property mapping — every one of these must survive, they are the parity checklist:

| StandardMaterial3D | Shader uniform / variant |
|---|---|
| `AlbedoColor` | `albedo_color` |
| `AlbedoTexture` | `albedo_texture` + `has_albedo_texture = true` |
| `Uv1Scale = (rU, rV, 1)` | `uv_scale = (rU, rV)` |
| `Uv1Offset = (0.5-0.5*rU+oU, …, 0)` | `uv_offset` — same first two components, unchanged formula |
| `TextureFilter = LinearWithMipmapsAnisotropic` | baked into the sampler hints (`filter_linear_mipmap_anisotropic`) |
| `CullMode = Back` | `render_mode cull_back` in all three variants |
| `Transparency.Disabled` | `prim_opaque.gdshader` |
| `Transparency.Alpha` | `prim_blend.gdshader` |
| `Transparency.AlphaScissor` + `AlphaScissorThreshold` | `prim_scissor.gdshader` + `alpha_scissor_threshold` |
| `Metallic` / `Roughness` | `metallic_factor` / `roughness_factor` |
| `Emission` / `EmissionEnabled` | `emission_color` / `emission_enabled` |
| `NormalTexture` + `NormalEnabled` | `normal_texture` + `has_normal_texture` |
| `OrmTexture` | `orm_texture` + `has_orm_texture` |
| `EmissionTexture` | `emission_texture` + `has_emission_texture` |

Notes:
- The async PBR texture callbacks assign `SetShaderParameter("xxx_texture", tex)` **plus** the
  matching `has_xxx_texture` flag — forgetting the flag renders the texture invisible rather than
  erroring, so it is the likely silent bug in any future addition here.
- `AlphaAntialiasingMode = AlphaToCoverage` became `alpha_to_coverage` in
  `prim_scissor.gdshader`'s `render_mode`. It is **not** a no-op: `project.godot` sets
  `msaa_3d=2` (4x MSAA), so it is what keeps cutout foliage/fence edges smooth. An older comment
  in `ObjectRenderer` asserted MSAA 3D was off project-wide; that was simply wrong.
- `_highlightMaterial` (selection overlay) stays a `StandardMaterial3D`; it is a `MaterialOverlay`,
  not a face material, and is out of scope.
- `uv_rotation` stays `0.0` in Phase 1. It is Phase 2's payload and wiring it early makes
  "visually identical" unverifiable.

**One deliberate deviation from "identical", and the reasoning for allowing it.** `OrmTexture`
was being set on a `StandardMaterial3D`, which *never samples it* — Godot only reads
`texture_orm` for an `ORMMaterial3D` — so glTF metallicRoughness maps were silently discarded on
every world prim. The shader family honours the map, so those faces now get their authored
roughness/metallic. Reproducing the Godot quirk deliberately in new code would have been worse
than a documented one-line difference, but it does make "the scene looks identical" ambiguous, so
`ObjectRenderer` logs once per ORM texture id at **Info** level (not `Debug` — `Logger`'s default
level is `Info`, so a `Debug` line would never print and the note would be worthless exactly when
it is needed). If the comparison shows a difference, that log says whether an ORM face was even
involved.

**Verify shaders by compiling them, not by reading them.** A throwaway `SceneTree` script run via
`godot --headless --path app --script <file>` that `load()`s each `.gdshader` reports real compile
errors. This immediately caught that `METALLIC`/`ROUGHNESS`/`NORMAL_MAP`/`EMISSION` cannot be
assigned from a user function — a mistake that reads as perfectly fine GLSL.

## Acceptance Criteria

### Phase 1 result (measured 2026-08-01, `v0.3.72-alpha`)

Same site, same protocol as the baseline (Dangazi Forest, logged in, stationary):

| | baseline `v0.3.71` (StandardMaterial3D) | `v0.3.72` (shader family) |
|---|---|---|
| frames in 10 s | 835 | 917 |
| medianMs | 11.46 | **10.61** |
| p95Ms | 14.81 | **12.50** |
| worstMs | 91.45 | 77.99 |
| drawCalls | 5867 | 5841 |
| objects | 6698 | 6724 |

Not worse — the acceptance criterion — and in fact slightly faster.

A **repeat run** of `v0.3.72` landed on `medianMs=10.61` again to the decimal, with identical
`drawCalls=5841 / objects=6724`, and `p95Ms` at 12.96 (vs 12.50). So the median is highly
repeatable and p95 carries roughly ±0.5 ms of noise. That makes the ~0.85 ms median gain over the
baseline more credible than first assumed — but it is still not clean evidence, because the
baseline run had a different object count (6698 vs 6724) and so was not the same scene. Treat it
as "no regression, probably a small real gain"; Phase 2 is where a substantial gain is expected.

**Method note for later phases:** median is the number to compare. It reproduced exactly across
runs, while p95 moved and `worstMs` (91 / 78 / 83) is pure outlier noise. Always confirm
`drawCalls` and `objects` match between the runs being compared — that is what tells you whether
you measured the same scene at all.

Log evidence from the same session (`client-output.log`):
- `[ObjectRenderer] BUILD MARKER: 2026-08-01-prim-shader-family-phase1` — proves the shader path
  actually ran, and not a stale assembly.
- **Zero `[FaceTex] ORM map now sampled` lines** — the one deliberate deviation was not exercised
  in this view, so it cannot account for any visual difference here.
- No shader compile or link errors.
- Pre-existing and unrelated: several `[FaceTex] object texture … fetch/decode returned null`.
  That is the FEAT-PERF-02 diagnostic firing on textures that genuinely failed to arrive; it
  predates this change and is its own issue.

Visual check was a live first-look comparison, not a pixel diff: no difference reported, and in
particular none of the two failure modes that would be obvious (glassy see-through shells if
back-face culling were lost, smeared sculpt-pole grain if anisotropic filtering were lost).

### Phase 1 — Swap `ObjectRenderer` to the shader family, visually identical

- [x] `ObjectRenderer` builds `ShaderMaterial`s from the new family instead of
      `StandardMaterial3D`; no `StandardMaterial3D` remains in its face path.
- [x] **The scene looks identical to today.** Live first-look check on Dangazi Forest; no visible
      difference. Not a pixel-diff — if something subtle surfaces later, this is the criterion
      that was checked least rigorously.
- [x] Every item on the parity checklist above is demonstrably still applied — including
      anisotropic filtering (the sculpt-pole grain case) and back-face culling (the
      glassy-shell case). Both were live-verified fixes; do not regress them.
- [x] Alpha behaviour unchanged: translucent tints, glTF `BLEND`/`MASK` alpha modes, and
      the `ApplyAlphaCutout` scissor path all render as before.
- [x] The atmospherics `#include` seam exists and is a no-op.
- [x] Frame time on a busy scene is no worse than the `StandardMaterial3D` baseline — see the
      result table above.
- [x] `dotnet build` + `dotnet test` clean, `dotnet format` clean, `AppVersion` bumped.

### Phase 2 — UV rotation (first real gain)

- [ ] The vertex shader applies arbitrary face rotation about the face centre.
- [ ] The Dangazi Forest pillar's plaster patch renders **top-left**, matching Firestorm.
- [ ] The `[FaceTex] unsupported face rotation` debug log in
      `ObjectRenderer.BuildFaceMaterialAsync` is removed — no rotation is unsupported.
- [ ] 0 and ±π render exactly as they did in Phase 1 (no regression on the cases that
      already worked).
- [ ] `_primShapeKeys` mesh sharing is unchanged: two prims of identical shape with
      different face rotations still share one mesh.

### Phase 3 — `AvatarRenderer` onto the same family

- [ ] `AvatarRenderer` face materials use the family; its `AlphaHash` path is preserved
      exactly (unconditional `AlphaHash` for the bake/attachment path — *not* the
      `ApplyAlphaCutout` scissor choice; the two are deliberately different).
- [ ] Own avatar and other avatars render identically to Phase 2, including worn mesh
      attachments and BoM-baked faces.
- [ ] Face rotation now works on avatar attachment faces too.

### Phase 4 — Terrain and water onto the same family

- [ ] `terrain.gdshader` and `water.gdshader` are refactored onto the shared family's
      structure and share the same atmospherics seam and global uniforms.
- [ ] Terrain detail-texture blending, height ranges and the water plane render
      identically to Phase 3.

### Phase 5 — Fill the atmospherics seam (Windlight / EEP)

- [ ] Windlight parameters are published once per frame as global shader uniforms; no
      per-material updates for them.
- [ ] All four renderers receive the same atmospherics — no visible seam between a prim,
      an avatar, terrain and water at sunset (the failure mode a hybrid would have had).
- [ ] Godot's built-in fog approximation is retired or explicitly reconciled with the new
      model, not left double-applying.
- [ ] Compared against Firestorm at matching sun positions on the same region.

## Technical Specs & Affected Files

- `app/materials/` (new `.gdshader` files) — the shader family and its shared includes,
  alongside the existing `terrain.gdshader` / `water.gdshader`.
- `app/scripts/ObjectRenderer.cs` — `BuildFaceMaterialAsync` and its callers; the
  `StandardMaterial3D` return type, `ApplyAlphaCutout`, and the PBR texture assignment
  continuations. `_primShapeKeys` must not change shape.
- `app/scripts/AvatarRenderer.cs` — its own `BuildFaceMaterialAsync`, the `AlphaHash`
  path, and the debug/placeholder `StandardMaterial3D`s.
- `app/scripts/TerrainRenderer.cs` — `_terrainMaterial` / `_waterMaterial` and the
  `SetShaderParameter` calls.
- `app/scripts/Boot.cs` — `AppVersion` bump per phase; likely home for the once-per-frame
  global-uniform publish in Phase 5.
- `src/` — **untouched.** Shaders are an `app/` concern; the layering rule stands.

## Sub-tasks / Progress

- [x] Phase 1 — `ObjectRenderer` on the family, visually identical, atmospherics seam
      (`v0.3.72-alpha`, measured 2026-08-01)
      stubbed.
- [ ] Phase 2 — arbitrary UV rotation in the vertex shader.
- [ ] Phase 3 — `AvatarRenderer` migration.
- [ ] Phase 4 — terrain + water refactor onto the family.
- [ ] Phase 5 — Windlight / EEP atmospherics via global shader uniforms.
- [ ] Follow-up (not this spec): `llSetTextureAnim` and media-on-a-prim as uniform
      updates instead of material rebuilds — enabled by, but not part of, this work.

## Side investigation: the "mirrored texture" sculpt (object 233207937) — UNRESOLVED

Reported as a light plaster patch sitting top-RIGHT where Firestorm shows it top-LEFT. Chosen as
Phase 2's acceptance target on the assumption it was a UV-rotation case. It is not. Recorded here
because six explanations were checked and eliminated, and none of them should be re-walked.

Object: plane sculpt (stitching 3), `sculptType = 0x43` (INVERT set, MIRROR clear), map
`757167bb`, at `<523.7, 855.9, 37.2>` in The Dangazi Forest.

| # | Hypothesis | How it was killed |
|---|---|---|
| 1 | UV rotation not applied | `[FaceParams]`: `rot=0°` on **every** face. Also repeat=(1,1), offset=(0,0) — placement is entirely neutral, so no placement fix can change this object. |
| 2 | `slng_transform_uv`'s `inout` on a built-in silently not writing back | Changed to a return value; no visual change. (Kept — it was a real hazard.) |
| 3 | Sculpt invert/mirror handled differently than the viewer | The viewer's `reverse_horizontal` (llvolume.cpp:3049, :6821) reads map columns backwards **and** flips `ss`. Substituting `c = T-1-t` shows column `c` still pairs with `U = c/W`, i.e. it reduces to a pure traversal reversal — a winding change, which is exactly what PrimMesher applies. Confirmed a third time by llvolume.cpp:2622-2640, where `do_invert` = invert normals + reverse triangles and leaves UVs alone. |
| 4 | Base sculpt U/V direction mirrored vs the viewer | Both map column→U and row→V ascending; with `flipV` the SL row-from-bottom `y` lands on Godot's row-from-top `H−y`. Matches. |
| 5 | Highlight on the wrong side because the sun is wrong | Was true and **is now fixed** (region sun direction, previously hardcoded) — but it did not move the patch. User: "Ich kann schon eine Reflexion von falscher Textur unterscheiden." |
| 6 | Sculpt surface generated inside-out, so back-face culling shows the mirrored far inner wall | Offline probe over all 63 cached sculpt maps meshed flag-free: 50 mostly outward, 13 mostly inward — and those 13 are ~50% cases, i.e. flat planes where a centroid-relative test is meaningless. No systematic reversal. This map alone is 0.9% outward, so it genuinely IS authored inside-out and its INVERT flag is the creator's correction, which we apply correctly (0x43 → 99% outward). |

**What was never obtained:** a camera-matched screenshot pair. The one comparison taken had the
two viewpoints ~11 m apart (`514,861` vs `525,856`), which is not close enough to judge a
left-right swap on a symmetric column.

**Suggested next step** — new evidence, not a seventh hypothesis. Cheapest first: dump the face's
texture (`14326c2a`) to PNG and locate the plaster patch *within the texture*, which makes the
expected on-screen position predictable instead of arguable. If that is inconclusive, render the
sculpt offline from both our pipeline and a direct port of `sculptGenerateMapVertices` +
`createSide` and diff the images — no live viewer needed either way.

### 2026-08-02 update: a second instance of the sculpt placement bug

Side-by-side at The Dangazi Forest `<516.6, 862.8, 38.8>` (Firestorm left, SLNG right) on the
fallen log — one of the objects that used to render white:

- Firestorm draws **brown bark** on the trunk.
- SLNG draws **green moss across the whole surface**.

That is not a sharpness difference, it is a different REGION of the texture, i.e. the same class
of fault as the pillar (object 233207937): placement on a sculpt. Two independent instances now,
which makes this worth solving properly rather than treating as a one-object oddity.

Useful as a test case because far more is known about it than about the pillar: its texture
(`6d9be86d`, 1024x1024) is one of the assets that only decodes via the reduced-resolution path, so
the image handed to the material is **smaller than the asset's nominal size**. Whether anything
downstream assumes nominal dimensions — texel-area/LOD maths, the GpuCache downsample, the
per-face UV scale — has NOT been checked, and is the first thing to check, since it is the one
factor this object has that the pillar does not.

Verified in the same screenshot: the avatar renders correctly (SendAppearance=false holds), and
the formerly-white objects are textured, which confirms the reduced-resolution decode (v0.3.98).

### Deep research: how the viewer builds sculpt UVs (2026-08-02)

Requested after inspection-by-derivation kept concluding "already correct". Every stage below was
read in the vendored viewer source and compared against ours. **All of them match.**

| Stage | Viewer | SLNG | Verdict |
|---|---|---|---|
| Grid resolution | `sculpt_calc_mesh_resolution`: `vertices = min(sculpt_sides(detail)², w·h/4)`, split by aspect ratio. For 64×64 → 32×32. | `PrimMeshService` mesherLod 32, `SculptMap` halves 64→32 (+1 seam row/col) | same 32×32 sampling |
| Profile texture coord | `genNGon`: `pt.set(cos(ang)·scale, sin(ang)·scale, t)` — the S coord IS the running parameter, linear in index | `uvs.Add(widthUnit·imageX, …)`, `widthUnit = 1/(coordsAcross-1)` | both linear 0..1 |
| Path texture coord | `tt = path_data[t].mTexT`, 0..1 along the path | `heightUnit·imageY`, then flipV at mesh build | equivalent under the flip |
| Seam at `x == sculpt_width` | wrap to 0 for sphere/torus/cylinder, **clamp to w-1 for PLANE** (llvolume.cpp:3101-3115) | `if (sculptType != SculptType.plane)` guard, already citing that line | same |
| invert / mirror | `reverse_horizontal = invert XOR mirror`: read columns backwards **and** flip `ss` | PrimMesher flips triangle winding | algebraically the same (see the earlier table) |
| Vertex order | profile fastest, path outer | `for imageY { for imageX }` — column fastest | same |

**Sculpt map decode verified visually, not inferred.** Decoding the statue's map (`6c3d8605`)
through the client's own `DecodeTexture(isSculpt: true)` and rendering it produces a healthy dense
RGB field, `degraded=False`. The geometry input is sound.

**A decoder-disagreement scare, and its correction.** The same map decoded via CoreJ2K appeared as
a near-white starburst, which looked like proof that CoreJ2K mis-decodes the very assets Magick
refuses. It was an artefact of the probe: CoreJ2K's SKBitmap carries alpha≈0, so the PNG export
rendered white. A control over **60 assets both decoders can read found zero disagreements** (mean
|CoreJ2K − Magick| ≤ 8 per channel). CoreJ2K is trustworthy; that theory is dead.

**The texture is a baked atlas.** `6d9be86d` (1024×1024) is painted specifically for this sculpt —
the statue's own forms are visible in it, pale stone in the middle, dense ivy toward the edges.
So the placement error is not subtle tiling: we sample the ivy region where Firestorm samples the
stone. It is also the right asset, so "wrong texture" is ruled out.

**Conclusion.** Every structural comparison says our sculpt pipeline matches the viewer, and the
inputs (map, texture) are correct. What has NOT been done is a **numerical** comparison — the checks
above all ask "does the code do the same thing", never "do the arrays contain the same values". That
is the next step, and the research above makes it cheap: port `sculpt_calc_mesh_resolution` +
`LLProfile::genNGon` + `sculptGenerateMapVertices` + `createSide`'s tc loop, run both over map
`6c3d8605`, and find the first index where the (position, uv) pairs diverge.
