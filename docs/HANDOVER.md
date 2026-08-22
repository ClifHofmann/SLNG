# HANDOVER

**One file.** Overwrite the section below when you hand over new work; do not create
`handover-<date>-<topic>.md` alongside it. There is also a stale
`docs/handover-2026-08-19-windlight-eep-phases-a-d.md` which nothing links to. It is **not** a
leftover to bin casually: 345 lines of live FEAT-ENV-01 context for a task still in progress. Fold
it in here when EEP is next picked up, and delete it only then.

---

# FEAT-RENDER-02 — Terrain detail-texture blending parity

**State:** `v0.7.47-alpha`, committed as `178ad20` on branch
`feature/FEAT-RENDER-02-terrain-detail-blending-parity`.

That branch was cut from `feature/FEAT-ENV-01-eep-step1`, so it **carries the in-progress EEP
commits underneath it**. Merge FEAT-ENV-01 first, or rebase this onto `main` once EEP lands —
do not merge this to `main` expecting it to contain only terrain work.
**Spec:** [FEAT-RENDER-02](specs/FEAT-RENDER-02-terrain-detail-blending-parity.md) — read its
"ruled out by measurement" sections before forming any new hypothesis.

## Build and verify

```bash
dotnet build SLNG.sln && dotnet test SLNG.sln && dotnet build app/SLNG.App.csproj
```

**`dotnet build SLNG.sln` does not compile `app/`.** `SLNG.App` is deliberately outside the
solution (AGENTS.md scopes it to the engine-agnostic libraries plus tests). Every "build clean" in
this task covered `src/` and `tests/` only until a real `CS1501` in `TerrainRenderer.cs` slipped
through with zero reported errors. Godot compiles the project on launch, which is why nobody
noticed. Always build the app project too.

Current state: both build, 220 tests pass, `dotnet format` clean for the touched files.

## What was wrong and what fixed it

The old shader blended detail0/1/2 by elevation with three hardcoded ±2 m `smoothstep` bands and
put detail3 on steep faces via a slope test. The viewer does neither: it turns elevation into one
continuous composition value in [0, 3] indexing all four textures, and **has no slope/cliff texture
at all**.

Landed, in order of how much each mattered:

1. **sRGB blending.** The viewer's *legacy* terrain path mixes the four textures while they are
   still sRGB-encoded and linearizes once later; the PBR path is the one that linearizes first.
   We had `source_color` on the samplers, i.e. the PBR behaviour, against a viewer running the
   legacy one. Linear blending weights the brighter sample more, and SL's default terrain set has
   exactly one green slot against three brown ones — so the whole island read brown. Error is
   **zero at the pure bands and maximal mid-transition**, which is the fingerprint to look for.
2. **Perlin `twiddle`** ported exactly, including the four traps listed in the spec
   (`llmath/llperlin.cpp` is dead code, `scratch/noise2D.glsl` is the wrong noise variant, the
   noise is purely horizontal, and the tables depend on the C runtime — MSVC is our target).
3. **The viewer's real blend ramp** (`alpha_gradient_2d.j2c`) is shipped, not approximated.
4. **Per-vertex composition** on the 1 m grid, as `LLSurfacePatch::eval` does — not per fragment.
5. **Anisotropic filtering** on the detail samplers.
6. **Detail UVs** repeat every 12 m (`RenderTerrainScale`), not 5 m.

## Verified against ground truth — do not re-derive

The region's `terrain.raw` estate export settled every input:

| Link | Result |
|---|---|
| Our heightmap vs the sim's export | **1024/1024 sampled cells identical** |
| `start_height` / `height_range` off the wire | 10 / 60, all four corners |
| `noise2` / `turbulence2` vs a replication written from the C source | exact to 7 decimals |
| Client's composition map vs an independent Python port over that export | **221/224 cells** |

RAW format, since it is not obvious: 256×256 cells, 13 bytes each, `height = red * green / 128`,
byte 2 is the water height, and **row 0 is north** (established by scoring all orientations against
our own map, not assumed).

## The one thing still open

Firestorm appears to show more of the low band around the island's edge than we do. **No
measurement reproduces this**, and every input is now verified identical. It is either a misreading
of the screenshots or Firestorm departing from Linden's `llvlcomposition.cpp`.

Five known-answer points are in [`tools/testassets/README.md`](../tools/testassets/README.md) —
stand at each in both viewers. Predictions come from the viewer's algorithm applied to the sim's
own heightmap, with 81–99% band weight, so they are unambiguous.

**Do not tune against a screenshot to close this.** That is how the two worst detours in this task
happened.

## Traps this task fell into — worth internalising

- **Contrast is spread, not mean.** The ramp was once "ruled out" by measuring mean slot error
  (0.088 units) when the real gap was p10–p90 spread (viewer ~0.50, ours 0.00). A summary statistic
  that cannot express the symptom cannot exonerate anything.
- **A camouflaged bug is worse than an obvious one.** A guard turning `height_range = 0` into
  `0.001` made the composition clamp to 3.0, painting whole settings-less regions in the *top*
  detail band. With a rock texture that is invisible. It took bright magenta probe textures to
  expose it.
- **Check the licence before routing around it.** The real ramp was approximated for a while on the
  assumption that Linden artwork is not redistributable. `doc/LICENSE-logos.txt` puts it under
  **CC BY-SA 3.0** — redistribution is fine with attribution. The approximation was measurably
  worse and the detour was unnecessary.
- **Godot import settings are load-bearing.** `detect_3d/compress_to=0` on the ramp texture is
  critical: Godot's default silently re-imports as VRAM-compressed on first use in a 3D material,
  corrupting the lookup table *after* it verified correct. Godot strips `.import` comments, so
  `TerrainRampAssetTests` asserts the settings instead.

## Licensing — now a standing obligation

We ship two Second Life viewer artwork files under CC BY-SA 3.0:
`app/textures/sl_alpha_gradient_2d.png` (the blend ramp, re-encoded J2K → PNG, values unchanged)
and `app/textures/clouds2.tga` (byte-identical; **it was already in the repo unattributed** before
this task).

The notice lives at [`app/THIRD-PARTY-NOTICES.md`](../app/THIRD-PARTY-NOTICES.md) — inside the
Godot project so an export includes it — and the client shows it under **Preferences → Licences**.

**Adding any third-party asset means adding it to that notice, including what you changed about
it.** Identifying changes is a licence condition, not a courtesy.

## Diagnostics currently in the build

`LogCompositionDiagnostics` prints four lines plus a 32×32 composition map per region, via
`GD.Print` (not `Logger.Info` — that sits at Warning unless diagnostics are on, so it was silent in
exactly the runs it exists for).

This contradicts the standing "quiet console" preference and is **deliberate but temporary**:
remove it once the point test above is settled.

## Next up: water

Not started. Pointers gathered while here:

- `app/materials/water.gdshader`; `TerrainRenderer.WaterMaterial` is already exposed and driven by
  `EnvironmentDriver` (FEAT-ENV-01 Phase D) — that is the anchor point.
- Viewer source in `scratch/slviewer`: `class1/environment/waterF.glsl` / `waterV.glsl`,
  `llsettingswater.cpp`.
- The water normal texture has failed to load before: `EnvironmentDriver` latched texture ids at
  the login screen, before `AssetService` existed. Check that first.
- FEAT-RENDER-01 Phase 4 covers water as well as terrain — coordinate rather than refactoring
  twice.

Measure the inputs before interpreting the picture. The `terrain.raw` export clarified more in ten
minutes than five rounds of screenshots; the water equivalent is checking the region's EEP water
settings from the log against `llsettingswater.cpp` before touching a shader.
