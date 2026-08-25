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

- **FEAT-RENDER-01 Phase 4** — terrain and water onto the shader family. This is the next step,
  and Phase 5 (atmospherics; underwater fog; FEAT-ENV-01 Phase E) is blocked behind it.
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
- **Particles are not implemented at all.** Needs its own spec.
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
