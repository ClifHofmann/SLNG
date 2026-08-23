# HANDOVER

**One file.** Overwrite the section below when you hand over new work; do not create
`handover-<date>-<topic>.md` alongside it.

---

# The Dangazi Forest reef — sculpts, texture animation, legacy materials

**State:** `v0.8.0-alpha`, **on `main`**, 2026-08-23. Solution and `app/` both build clean,
**281 tests green**, `dotnet format` reports **121** violations against a starting baseline of
**125** (it went *down*: auto-formatting `FaceTexture.cs` took four pre-existing ones with it).

Thirteen commits, thematically separated:

```
1d1ec58  docs:         [FEAT-RENDER-04] confirmed in-world, v0.8.0-alpha
70dac5d  fix(render):  [FEAT-RENDER-04] port SL's specular exponent instead of guessing it
f332f50  feat(render): [FEAT-RENDER-04] bind the legacy material's specular map
faadfa8  fix(assets):  [FEAT-RENDER-04] sample sculpts on the viewer's grid, not PrimMesher's
6badf3d  chore(assets):[FEAT-RENDER-04] delete two dead, misleading sculpt samplers
04825b1  feat(render): [FEAT-RENDER-04] bind the legacy material's normal map
cdf8a64  feat(render): [FEAT-RENDER-04] generate mesh tangents, without the NaN
d00f659  feat(app):    [FEAT-RENDER-04] resolve each face's legacy material and report it
755aafe  feat(net):    [FEAT-RENDER-04] read legacy Blinn-Phong material ids and resolve them
d3aa566  feat(app):    [FEAT-DIAG-01] make missing geometry visible, and quiet the log
7866935  fix(assets):  [FEAT-ASSET-01] detect truncated J2C, and use degraded sculpt maps
8dcb1a4  feat(render): [FEAT-RENDER-03] implement llSetTextureAnim
3a5614d  fix(net):     [FEAT-NET-05] treat a sculpt with stitching None as sculpted
```

It started as one user report — "the reefs are discs in SLNG and rocks in Firestorm" — and every
one of the findings below came out of that single object.

---

## Build and verify

```bash
. tools/dev-env.ps1          # the .NET 8 SDK is per-user; a bare `dotnet` finds only the runtime
dotnet build SLNG.sln
dotnet build app/SLNG.App.csproj    # NOT covered by the solution build
dotnet test SLNG.sln
dotnet format SLNG.sln --verify-no-changes   # expect 121; anything more is new
```

---

## 1. The reef: five separate bugs behind one symptom

**FEAT-NET-05 — a sculpt with stitching `None` was thrown away.** The rock's sculpt type byte is
`0x40` (Invert, stitching 0). `SculptData.Type` masks with `& 7`, so that reads as `None`, and our
wire code required `Type != None` before treating a prim as sculpted. The prim fell back to its
base profile/path curve — a smooth 26×7×71 torus sitting exactly where the rock should be.

The trap worth remembering: `LLVolumeParams::isSculpt()` *does* test
`(mSculptType & MASK) != NONE`, which made the old condition look right. The render path calls a
different predicate — `LLVOVolume::isSculpted()` (llvovolume.cpp:3633) is just
`if (getSculptParams()) return true;`. Presence of the block, not its value.

**FEAT-RENDER-04 prerequisite — mesh tangents did not exist.** `SurfaceTool.GenerateTangents()`
had been disabled with a comment about a Vulkan NaN crash, so Godot had no tangent basis and could
not apply *any* normal map — including the ones the glTF PBR path already bound.
`SLNG.Assets.MeshTangents` computes them with finite output as the defining property.

**FEAT-RENDER-04 prerequisite — sculpts were sampled on the wrong grid.** PrimMesher halves both
map dimensions together; the viewer spends its vertex budget in the map's actual proportion
(`sculpt_calc_mesh_resolution`). On the rock's **512×16** map that is 645 vertices against the
viewer's 1230 — half the resolution along the axis the strata run along. The user found it with a
reference cube: it rested on the surface here and sank into it in Firestorm. Extents now match to
the last decimal.

**FEAT-RENDER-04 — the legacy Blinn-Phong material was never read.** A face carries *two*
independent material ids; we read only `RenderMaterialID` (glTF) and never `MaterialID` (legacy
normal + specular maps). See §2.

**FEAT-ASSET-01 — truncated J2C and the degraded-sculpt refusal.** Detailed in §3.

---

## 2. FEAT-RENDER-04 — where it stands

Phases 1–4 landed and are confirmed in-world. **Phase 5 (alpha modes) is open** — status is
`🧪 Review`, not `✅ Done`. [Spec](file:///E:/Git/SLNG/docs/specs/FEAT-RENDER-04-legacy-blinn-phong-materials.md)

LibreMetaverse already implements the whole `RenderMaterials` capability path
(`Materials.LegacyMaterial`, `ObjectManager.RequestMaterialsAsync`), so the work was boundary
conversion, batching and rendering — not protocol.

**The one part that is an approximation, not a port:** Godot's metallic-roughness model has no
per-texel specular colour. A greyscale specular map (nearly all SL content) is faithful; a coloured
one loses its tint.

**The glossiness→roughness relation, which cost a round:** it is not in the shader that samples the
specular LUT, it is in the code that *builds* it (`LLPipeline::createLUTBuffers`, pipeline.cpp:1447):

    n = glossiness² × RenderSpecularExponent      // default 368
    spec = pow(N·H, n)

then Blinn-Phong→GGX (`α = √(2/(n+2))`) and one more square root because Godot's `ROUGHNESS` is
perceptual. For the rock: **0.59**. The first attempt used `1 - glossiness` = 0.8 and produced no
highlight at all. Also found in the same pass: the specular map scales the highlight's *strength*
(Godot's `SPECULAR`), not its roughness, and glossiness is modulated per texel by the **normal**
map's alpha channel (materialF.glsl:227).

---

## 3. FEAT-ASSET-01 — truncated textures, and a refusal that was measured wrong

`J2cCodestream.IsTruncated` reads the tile-part's declared `Psot` (ISO/IEC 15444-1 A.4.2) and
compares it against the bytes in hand. Verified over a real 14,821-entry cache: **all 94**
previously-degraded assets flagged, **0 of 7,667** clean ones. A truncated HTTP body now fails
immediately and falls through to UDP in the *same* attempt.

Degraded **sculpt maps are now used instead of refused**, which is what the real viewer does
(`opj_decoder_set_strict_mode(decoder, OPJ_FALSE)`, "enable decoding partially loaded images",
llimagej2coj.cpp:420). Magick.NET exposes no way to reach that flag — `reduce-factor`,
`quality-layers` and `non-strict` all still throw on all 95 truncated assets in the cache. Measured
over 12 real assets, decoding only the first N% and comparing against each one's own full decode:
mean error 1.4/255 at 75% of the bytes, 1.9 at 30%, 4.2 at 2%. No cliff. And a sculpt map is
point-sampled onto at most a 32×32 grid anyway (`SCULPT_REZ_4`, llvolume.cpp:3137), so the lost
detail is detail nothing reads. Bake/avatar textures keep the refusal — that one has its own
measured symptom.

---

## 4. FEAT-DIAG-01 — the tooling that made the rest possible

This is the part to read first next time something "looks wrong".

- **Library logs now reach `godot.log`.** `SLNG.Net`/`SLNG.Assets` cannot call `GD.Print`, so they
  log through `Console`, which goes to a stdout Godot's own log file does not capture. Every
  `[TextureFetch]`, `[TextureGiveUp]` and `[DecodeFail]` line — the ones that say *why* an asset
  never arrived — was missing from the log of a session whose objects were visibly broken.
  `ConsoleToGodotLog` forwards them.
- **Log volume: ~16,000 lines → ~1,300.** `[GpuUpload]` moved behind `--diag`; `[ENV]` drops exact
  repeats (13,908 of ~16,000 lines were one six-line block).
- **Three silent geometry fallbacks now warn**: `[SculptFallback]`, `[MeshFallback]`,
  `[PrimMeshFallback]`. Each had been substituting a plausible-looking cylinder/sphere/box at the
  object's real scale, which at scenery size reads as content rather than as failure.
- **Click an object** → its whole *linkset* is dumped: every part with type, size, position,
  rotation in build-floater terms, its rendered AABB in metres, and whether it is drawn.
- **F6** → what is nearby, every object in the region *failing to draw* (attachments excluded), and
  the largest objects held with UUID and name. That last one is the only search available without a
  UI to type a UUID into, and it is how you match a landmark Firestorm names by size.

---

## 5. Still open

**From this session:**

- **Particles are not implemented at all.** No `ParticleSys` anywhere in `src/` or `app/`;
  LibreMetaverse parses the block and we never read it. The reef's spray clouds are missing
  entirely. Needs its own spec — `GPUParticles3D` driven by SL semantics (burst rate, lifetime,
  start/end colour and size, acceleration, wind, follow-source/velocity, the Explode/Angle/AngleCone
  patterns).
- **Underwater fog / atmospherics.** `slng_apply_atmospherics` in `prim_common.gdshaderinc` is an
  explicit no-op ("Windlight / EEP goes here (Phase 5)"), and the water surface fogs nothing behind
  it. Every submerged object therefore renders at full contrast — a sandbank that is barely visible
  in Firestorm is a hard brown ellipse here. This is FEAT-RENDER-01 Phase 5.
- **FEAT-RENDER-04 Phase 5 — alpha modes.** `DiffuseAlphaMode` and `AlphaMaskCutoff` come from the
  material outright; today the renderer *guesses* them with `Image.DetectAlpha()`.
- **Texture animation direction is unverified.** The user reported animations looking "falsch rum"
  and it was never settled. The maths matches `LLViewerTextureAnim::animateTextures` and the shader
  composition was re-derived, so a blind sign flip would be a mistake.
  `tools/testassets/texanim_probe.lsl` is ready: rez a box, put `out/uvprobe_1024.png` on it, drop
  the script in. Three phases isolate U-scroll, the same scroll reversed, and a stepped 2×2
  flipbook — which separates "a sign on U", "the REVERSE bit" and "the V axis".
- **The reef's two flat wave sculpts report an AABB Z of exactly 0 m** (`08b4c8c9`, `b41566be`, both
  64×64). Our mesh matches the viewer-algorithm port exactly, so it is probably correct — the maps'
  blue channel really is constant. Worth one glance if the waves ever look wrong.
- **`[ENV]` re-applies roughly 2,300 times per session — investigated, mostly fixed** (`3586a6a`,
  `v0.8.1-alpha`). Two separate causes, and the second one hid behind the first's fix.

  Cause one: a `RegionInfo` packet is how an EEP-capable viewer is told the environment changed,
  and we answered every one of them with three uncoordinated HTTP capability GETs. On OSGrid's
  Lbsa Plaza those packets arrive ~45/min, sustained — about 8000 requests per session aimed at
  someone else's server. The trigger is correct and matches `llenvironment.cpp:886`; the response
  was not. Now: one poll at a time, a 2.5 s floor (OpenSim's own `UpdateEnvTime` granularity), the
  legacy GET dropped from live re-polls, and nothing published unless the payload changed.

  Cause two: that change gate then did not hold — 217 republishes in 52 minutes on Lbsa. The
  parcel id comes from a UDP `ParcelProperties` round-trip that sometimes times out, and both the
  id and the parcel LLSD went into the fingerprint, so every timeout flipped two fields on timing
  alone. An unresolved lookup now contributes the last established scope instead of a fresh
  "no parcel".

  **Still open:** the parcel lookup itself still goes out on every re-poll, so a busy region costs
  one UDP round-trip per poll on top of the GET. The real viewer does not do this — it reads the
  parcel from the land layer it already has.

  **How to verify, and the trap:** count `[ENV]` lines after a Lbsa session. The meaningful line is
  `source=…position=…`, **not** `caps:` — `LogEnvironment`'s dedup drops exact repeats, and the
  `caps:` line is byte-identical every time, so it shows once no matter how often it fired. Only
  `position=` varies, so only that line survives to be counted. Reading `caps:` as a poll count
  produced a wrong all-clear once already.
- **Sculpt map `bb745170` is truncated on the sim itself**: 33,600 bytes for a tile-part declaring
  113,049, and UDP delivers the same. Not our bug, but it is why that particular pair of rocks
  needs the degraded-decode path at all.
- **`SLNG_Test_Sky.xml`** sits untracked at the repo root — a known-answer EEP sky probe (primary
  colours per channel) from the FEAT-ENV-01 work, not from this session. It is committed as-is
  rather than moved; it arguably belongs in `tools/testassets/` with the other probes.

**Carried forward, still true:**

- **`--selftest` is documented in `AGENTS.md` and does not exist.** `app/` has no handler, so
  `godot --headless --path app -- --selftest` just sits at the login screen. This bit again: the
  shader changes in FEAT-RENDER-04 could not be smoke-tested before handing them over.
- **FEAT-RENDER-02 has one unexplained visual impression left.** Five known-answer points in
  `tools/testassets/README.md` settle it. **Do not tune against a screenshot** — that caused the two
  worst detours in that task.
- **A parallel, unfinished EEP implementation is parked in `stash@{0}`** (`GodotEnvironmentManager`,
  `EnvironmentWindow`) plus a `feature/FEAT-ENV-01-windlight-eep` branch. Not ours. Two
  implementations of one feature exist; that needs a human decision, not a blind merge.
- **A leftover worktree sits at `.claude/worktrees/sad-yalow-b74c97`.** The user does not want
  worktrees for this project — everything stays in `E:\Git\SLNG`. Safe to remove.
- **Water:** fresnel, refraction, `blend_factor`.

---

## 6. Method notes, updated by what actually worked this time

- **Read the client log before asking the user anything** — and check the log can even *contain*
  the answer. Three rounds were spent on screenshots while the relevant diagnostics were going to a
  stdout nothing captured, and another two on `Logger.Info` lines that are off without `--diag`.
- **If an object's id appears nowhere in the log, it was never loaded.** Not broken — absent. Draw
  distance defaults to 96 m while Firestorm draws much further, and culling is silent. Three
  comparison rounds died on this.
- **Two screenshots cannot settle a size or shape question** — the cameras and fields of view
  differ. Print the AABB in metres and read it against the build floater. The user's reference cube
  did in one image what four screenshots had not.
- **When a viewer predicate seems to contradict observed viewer behaviour, look for a same-named
  sibling on another class.** `LLVolumeParams::isSculpt()` vs `LLVOVolume::isSculpted()` cost a
  round and a confidently wrong conclusion.
- **For a derived render parameter, find the code that BUILDS the lookup table, not the shader that
  samples it.** The glossiness exponent was in `createLUTBuffers`, nowhere near the material shader.
- **Delete dead code that reads like the live path.** Two unused sculpt samplers — one box-filtering
  the map, one dividing by 256 instead of 255 — cost an investigation each.
- **State a hypothesis, then try to kill it.** Six died this session (placeholder cylinder, texture
  blur, rotated UVs, an occluding disc, taper/twist being ignored, a degraded sculpt map). Each was
  cheap to exclude and would have been expensive to "fix".

---

## 7. Watching the sim log

The user's own OpenSim runs in Docker on their server.

```bash
ssh meernet 'docker exec os-osgrid-1 tail -n 0 -F /opt/opensim/bin/OpenSim.log'
```

`~/.ssh/config` already has the host and key. `docker logs os-osgrid-1` is **not** the sim log.
`Malformed data, cannot parse N byte packet` is SIP scanner traffic, and `GETASSET: asset with
wrong type` came from another agent's uploads — neither is ours. A genuine viewer-side signature is
`No packets received from root agent ... Disconnecting.`
