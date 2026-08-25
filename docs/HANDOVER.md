# HANDOVER

**One file.** Overwrite the section below when you hand over new work; do not create
`handover-<date>-<topic>.md` alongside it.

---

# Two V flips, a smoke test that never existed, and the avatar on the shader family

**State:** `v0.9.12-alpha`, **on `main`**, 2026-08-25. Solution and `app/` both build clean,
**291 tests green**, `dotnet format` reports **0** violations (it was 121), `--selftest`
passes 23/23, and CI's smoke-test job is green on `main` for the first time.

The session ran four threads, all closed and all confirmed in-world: a repo hygiene pass,
FEAT-RENDER-01 Phase 3, the close of FEAT-RENDER-02, and FEAT-UI-09 (worn HUDs responding to
clicks).

**26 commits**, `3ba2be0..c296337`. `git log --oneline 3ba2be0..c296337` reads them in order;
they are grouped by feature id and each one carries its own reasoning, so the commit messages
are the detailed record and this file is the map.

---

## Build and verify

```bash
. tools/dev-env.ps1                          # the .NET 8 SDK is per-user; a bare `dotnet` finds only the runtime
dotnet build SLNG.sln                        # 0 errors
dotnet build app/SLNG.App.csproj             # NOT part of SLNG.sln -- build both or the client runs yesterday's assembly
dotnet test                                  # 291 green (163 Core + 67 Assets + 61 Net)
dotnet format SLNG.sln --verify-no-changes   # clean
godot --headless --path app -- --selftest    # 23/23 PASS
python tools/check_shader_globals.py         # ok (one WARN, see below)
python tools/roadmap-dashboard.py --tests 291   # regenerates docs/dashboard.html
pwsh tools/run-client.ps1 -Diag              # in-world, with diagnostics on
```

`--selftest` and the dashboard are both new this session. The dashboard renders
`docs/ROADMAP.md` as a visual status page -- milestone bars, commit activity per week, and
cards for whatever is in flight -- so there is no second list to keep in sync.

---

## 1. FEAT-RENDER-02 is closed, and it was never the maths

The task had survived five rounds of measurement with "the numbers check out, but it still
looks wrong". The answer was **two texture-coordinate flips**, found days apart, both the
same convention: **SL's texture V is bottom-origin, Godot's is top-origin.**

| | |
|---|---|
| `sl_detail_uv` | every terrain detail texture sampled upside down |
| `sl_terrain_ramp` | the blend ramp's V (`rand_val`), same convention |

The s/t plane equations already matched `lldrawpoolterrain.cpp:242-248` exactly, offset
included. What was missing is the `v = 1 - v` that `ObjectRenderer.BuildArrayMesh` applies to
every world mesh — terrain never went through that path, because it carries no mesh UVs at
all and generates its coordinate in the shader from position.

The ramp flip bites harder than it sounds. Its rows are **not interchangeable**: row 128
crosses over in 62 texels, row 160 takes 137. So `v -> 1-v` does not change which rows are
used, it changes **where** — across a ~155 m field. The terrain came out crisp where the
viewer is soft.

**Why five rounds of measurement could not see either, and this is the transferable part:**
every number this task produced is computed from position. Composition values, per-slot area
splits, the heightmap comparison, an independent algorithm port agreeing to 221 of 224 cells.
**A texture-space flip is invisible to all of it.** Do not expect more measurement of that
kind to find this class of bug; it will keep passing.

What found them was deliberate asymmetry:

- the detail flip by the probe texture's corner marker, which exists for exactly this
  (`gen_terrain_probe.py:126`);
- the ramp flip by `tools/testassets/gen_terrain_map.py` — a new known-answer heightmap whose
  flat 50 m terrace sits at composition 0.50, where height contributes a constant and nothing
  but the blend decides the pixel.

**New instruments, both committed and documented in `tools/testassets/README.md`:**

- `gen_terrain_map.py` → `out/terrain_bands.raw`. Five flat terraces, one per band plus one on
  a crossfade. Linden RAW, read out of OpenSim's own `LLRAW.cs` rather than guessed, round-tripped
  through that same formula before committing. **It is only a known answer WITH estate low 20 /
  high 240** — the generator prints those settings rather than leaving them to be rediscovered.
- `band_markers.lsl`. Plants a coloured pole at six known coordinates on the crossfade terrace,
  each carrying its own expected answer as its colour. This is what turned "the crop looks
  different" into six readings, and it ruled out a sign flip, a y-mirror and an x/y transpose
  before the ramp was even suspected.

**Left open deliberately:** `terrainF.glsl` samples the ramp's ALPHA channel
(`texture(alpha_ramp, ...).a`); we sample `.r`. Our shipped PNG is greyscale with no alpha, so
`.r` is the only data present and it now matches in-world — but whether the hand extraction
from `alpha_gradient_2d.j2c` took that channel was never verified. Settle it if the ramp is
ever touched again.

---

## 2. FEAT-RENDER-01 Phase 3 — avatars on the shader family

`AvatarRenderer` builds `ShaderMaterial`s on the family instead of `StandardMaterial3D`.
Every alpha threshold and branch is unchanged — `ApplyAlphaCutout` became the pure classifier
`ClassifyAlpha`, returning which variant a texture needs instead of assigning a property.

What visibly changes is per-face **rotation** on avatar faces. It was a ±π approximation: a
half-turn faked by negating both repeats, every other angle logged as unsupported and drawn
unrotated.

**Two compile-time axes the spec did not anticipate.** Cull mode (avatar faces ran on
`CullMode.Disabled`) and shading mode (HUD faces need `unshaded`, because the HUD SubViewport
has its own `World3D` with no lights). Both are `render_mode`, so neither can be a uniform.
They are folded into `PrimShaderFamily.Surface` — `WorldPrim`, `Avatar`, `Hud` — named after
the three real call sites, closing the set at 3 × 3 = 9 instead of growing with the next
render_mode. The unshaded HUD variant also settles a Phase 5 question by construction: an
unshaded shader cannot receive atmospherics, and fogging a HUD by camera distance is meaningless.

**Read this before touching the system bake.** The spec's Phase 3 criterion demanded preserving
"unconditional AlphaHash" for it. That code has not existed since `175d308` (2026-08-21), which
switched it to `AlphaScissor(0.5)` and said so in its own message. Following the spec literally
would have silently reverted a deliberate decision. The spec is now corrected; the code/comment
disagreement above that line is still parked and still wants a live A/B.

**Confirmed in-world 2026-08-25**, and Phase 3 is closed. HUD attachments were the load-bearing
check: `Surface.Hud` is the variant this phase invented, and a broken `unshaded` render_mode
would have shown them black rather than subtly wrong. They render, and after FEAT-UI-09 they
respond to clicks too.

---

## 3. Hygiene, and a smoke test that finally exists

`--selftest` had been documented in `AGENTS.md` since the first commit with **no handler at
all**; the command sat at the login screen. It now loads every shader, every locale (with a
key-coverage diff) and the Bento skeleton through the engine, and exits non-zero on the first
failure. 23/23.

Two things about it worth keeping in mind:

- **The shader check reads the uniform LIST, not the load result.** `ResourceLoader.Load`
  returns a Shader object for a file that did not parse; a rejected shader exposes no uniforms.
  Verified by deliberately breaking `water.gdshader` — the check must be able to fail, and that
  was tested rather than assumed.
- **It cannot prove a shader fits the geometry it will meet.** It passed 23/23 both before and
  after a real Phase 3 regression where the HUD variants demanded tangents from meshes that have
  none. That surfaced in a user's session log, not in any check here.

CI now runs it on `windows-latest`. Getting there took two failures, both of which **hung**
rather than failed: a fresh checkout has no `app/.godot`, so nothing loads and `_Ready` never
runs; and `-c Release` puts the C# assembly where a non-exported run does not look. Both
reproduced locally before fixing. The step now has a timeout that prints a readable error.

Also: `dotnet format` is clean at 0 (vendored `PrimMesher/` is out of scope via
`generated_code = true` rather than being rewritten), 17 local and 4 remote dead branches are
gone, and `tools/roadmap-dashboard.py` renders `docs/ROADMAP.md` as a visual status page.

---

## 4. Still open

### Start here

**FEAT-RENDER-01 Phase 4 — terrain and water onto the shader family.** Everything visible is
dammed behind it: Phase 5 is the atmospherics seam (underwater fog, EEP on every surface) and
FEAT-ENV-01 Phase E falls out with it, but ADR 0002 exists precisely to stop atmospherics
landing on prims and avatars while terrain and water are still on their own shaders.

Two things make it cheaper than it looks. `sl_terrain_composition.gdshaderinc` was written
during FEAT-RENDER-02 **specifically so Phase 4 can include it** rather than rewrite it. And
Phase 3 already built the machinery: `PrimShaderFamily.Surface` is the place a terrain/water
surface would be added, and `--selftest` already compares each variant's uniform set against
its base, so a uniform added to one and forgotten on the others fails the smoke test.

Be warned by Phase 3's own history, though: its spec asked for behaviour that had been
deliberately reverted months earlier, and following it literally would have shipped a
regression. Check what the code does before trusting what the spec says it should.

### The rest

- **FEAT-RENDER-04 Phase 5** — `DiffuseAlphaMode` and `AlphaMaskCutoff` come from the legacy
  material outright; the renderer still guesses them with `Image.DetectAlpha()`. Small, fully
  specified, no research risk, and it is the last task sitting at Review.
- **FEAT-PERF-01** (login → usable takes ~1 min) has never been profiled; the first task is a
  baseline, not a change. **FEAT-PERF-02** Phase 1 is part-done.
- **CI is red on Linux**, unchanged: three `SculptStitchingNoneTests` fail on ubuntu and pass on
  Windows. Deliberate deferral from 2026-08-23. The `selftest` job was put on `windows-latest`
  specifically so it does not inherit this.
- **`slng_moon_direction`** — a global shader uniform `EnvironmentDriver.cs:454` sets every frame
  and no shader reads. Left alone: plausibly groundwork for the moon.
- **`linden_llvoavatar.cpp`** (874 KB) tracked at the repo root. Not junk — four source comments
  cite it by name and it differs from `scratch/slviewer`'s copy — but it is Linden source at the
  top level with no entry in `app/THIRD-PARTY-NOTICES.md`. Wants a decision.
- **A parallel, unfinished EEP implementation in `stash@{0}`.** Not ours. Needs a human decision,
  not a blind merge. `git stash show -p stash@{0}`.
- **`.claude/worktrees/sad-yalow-b74c97`** — empty, held by a process, deletable after a restart.
- **Particles now work, but still have no Feature ID, no spec and no roadmap entry**, and the
  branch they were built on is named after the long-finished FEAT-RENDER-02. Section 6 below
  is what exists instead. Still unimplemented on purpose, and documented in
  `SlParticleDataFlags`: Bounce, Wind, FollowVelocity, target/beam/ribbon, glow, custom blend
  function. Worn objects cannot show particles at all -- `ObjectRenderer.UpdateVisual` returns
  early for attachments and `AvatarRenderer` has never heard of `ObjectParticles`.
- **Particles straddling the water surface are cut by the water's depth write.** They now draw
  at a fixed priority above water, so the artefact no longer flips with the camera, but the
  viewer splits its alpha pass in two around water and clips per fragment
  (`lldrawpool.h:74-78`, `lldrawpoolalpha.cpp:149-159`). The second half of that belongs to the
  water pass.
- **Texture animation direction unverified.** `tools/testassets/texanim_probe.lsl` is ready.
- **Water:** fresnel, refraction, `blend_factor`.

---

## 5. Method notes, from what actually worked

- **A tool that fails silently is worse than one that does not start.** This came up four
  separate times in one session: a composition diagnostic keyed so it only ever logged once per
  region (so it described terrain that had been replaced), LSL markers that could not report
  they had no script inside them, `llSetRegionPos` returning 0 with no error, and six silent
  exits in the HUD click path. Every one of them cost a round trip, and every fix was the same:
  make the failure name itself.
- **A failure must be at least as loud as a success.** The HUD diagnostics went in with the
  failures behind `--diag` while the success line printed unconditionally. That made a broken
  click quieter than a working one and cost another round trip.
- **Read the client log before asking anything.** `client-output.log` is on disk and answered
  four questions this session that were about to be asked as screenshots.
- **When a screenshot comparison stalls, the problem is usually the camera.** Three rounds on
  the crossfade terrace went nowhere because the two viewers never stood in the same place, so
  "green on the left" described the crop. Poles at known coordinates ended it in one round.
- **Check what a measurement CAN see before trusting it.** A prediction image built from
  `SlTerrainComposition.Weights()` was compared against a screenshot for a round before noticing
  that function documents itself as having no contrast and ignoring the ramp entirely.

## 6. Particles, and four bugs that all looked like nothing

Particles were "implemented" and had never once been seen working for longer than a single
session. Four separate bugs, and the reason they took so long is the thing worth carrying
forward: **not one of them failed. Every one produced a complete, plausible, wrong result.**

| what was wrong | what it looked like |
|---|---|
| `ParticleSystem.MaxAge` (the EMITTER's lifetime) read as the particle's | a pool of exactly 1 particle |
| `PartFlags` (2 source bits) read where `PartDataFlags` belongs | no colour interpolation, not fullbright |
| `Guid.Empty` meaning both "no texture" and "not resolved yet" | opaque tinted squares |
| a `CRC == 0` gate the real viewer does not have | whole systems silently discarded |

The first two are LibreMetaverse name collisions: in both pairs the obvious-looking field is the
wrong one, and they overlap numerically, so reading the wrong one is invisible. The DTO now names
the section explicitly (`SourceMaxAge`/`PartMaxAge`, `SourceFlags`/`PartDataFlags`) and the bits
are enums, so repeating the mistake is a compile error.

### The one that mattered: LibreMetaverse loses particles on every compressed update

`ObjectManager.PacketHandlers.cs:810` hands the particle-block parser the offset into the whole
compressed object blob, and that parser takes its length from `data.Length - pos` -- i.e. "the
block runs to the end of the object", which it never does. The length never matches the 86-byte
block, no branch runs, every field stays at its default. The handler still advances its cursor by
the correct 86 bytes, so nothing else decodes wrong. Completely silent.

Full updates are fine, because there the block arrives in its own message field whose length is
exactly right. That is the whole "it worked the first time and never again" shape: setting a
particle system schedules a full update, so it works the moment the script runs, and never again
after a relog, when the object arrives compressed.

`CompressedParticleRepair` recomputes the offset and re-raises the update with the block parsed
from exactly its own bytes. **Two warnings if you touch it:**

- Between the owner id and the particle block sit FIVE optional sections -- angular velocity,
  parent id, tree/scratch pad, floating text, media URL. The first version handled only the media
  URL, and a child prim in a linkset (parent id, 4 bytes) then read 86 bytes four bytes early. It
  did not fail. It produced a 3-second emitter reading as 1.15 s, a burst of 100 as 128, and a
  texture id shifted by four bytes -- which is what finally identified it, because a UUID is the
  one field where a shift is legible by eye. The tests now cover every section and combination.
- `HasParticlesNew` (extended block, glow or custom blend) is worse and is NOT repaired here:
  LibreMetaverse has no branch for it at all, so it does not skip the block either, and every
  field it decodes after it for that object is read from the wrong offset.

`CompressedParticleRepairTests` pins the upstream bug itself -- it decodes a block the way the
library does and asserts that nothing comes out. If LibreMetaverse ever fixes this, that test says so.

### Instruments

`tools/testassets/particle_probe.lsl` re-sends its system every 20 s, because a particle system is
only put on the wire when a script sets it -- a probe that sets it once in `state_entry` can only
be observed by someone who was already standing there. Two `--diag` log lines bracket the path:
`[ParticleWire]` (arrived at the protocol boundary) and `[Particles]` (renderer configured it).
Between them they localise any future failure to one half in a single run; that is how all of this
was actually found. The README documents both, and how to re-measure the default particle texture's
falloff out of the vendored `pixiesmall.j2c`.

**One trap, named because it cost a wrong conclusion here:** a viewer keeps a particle source alive
once it has one. A Firestorm that has been open all afternoon will happily display an emitter the
simulator stopped sending hours ago. Relog it before concluding anything from a side-by-side.
