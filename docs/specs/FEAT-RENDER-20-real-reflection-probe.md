# [FEAT-RENDER-20] Real Godot `ReflectionProbe` for shiny surfaces

- **Feature ID:** `FEAT-RENDER-20`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `claude` (graphics-engineer)
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Split out of `BUG-RENDER-20` (see that spec for the four-round history of hand-tuning a flat
sky-tint approximation). After round 4 still didn't look like a real reflection, the user asked
the right question: *"die frage ist ob FS nur die wolken oder alles reflektiert"* — does
Firestorm reflect only the clouds, or everything?

Confirmed from the reference viewer's own source, not guessed: `reflectionProbeF.glsl` (vendored
at `scratch/slviewer/indra/newview/app_settings/shaders/class3/deferred/reflectionProbeF.glsl`)
implements a real reflection-probe system — `irradianceProbes`/`reflectionProbes` sampler cube
arrays (lines 33, 54), a mix of **automatically-placed** probes covering the world and optional
**manually-placed** ones (lines 114, 244, 492-685), each capturing the actual surrounding scene
into a cubemap. That means Firestorm's shiny-sphere reflection is a real capture of terrain,
buildings, sky and clouds — everything around the object — not a synthetic per-direction sky
colour. This also matches `BUG-RENDER-20`'s ORIGINAL report ("mirrors the courtyard"), which this
round's narrower "clouds" observation had partially obscured.

SLNG has no `ReflectionProbe`, `VoxelGI`, or `SDFGI` anywhere in the codebase (confirmed absent by
grep across all four rounds of `BUG-RENDER-20`). The user's call, given that: build the real
thing instead of continuing to hand-tune an approximation that structurally cannot show real
geometry.

## Why this should be simpler than it sounds

Godot's built-in PBR pipeline already routes `ReflectionProbe` content into the exact same
`METALLIC` / `ROUGHNESS` / `SPECULAR` channels the prim shaders already write —
`BUG-RENDER-20`'s very first investigation round confirmed empirically (isolated probe scenes,
`ShaderMaterial` vs `StandardMaterial3D`, matching results) that a custom `ShaderMaterial` already
receives Godot's automatic sky-only IBL exactly like a built-in material does. Adding a real
`ReflectionProbe` should not require any NEW shader plumbing — it should just give the pipeline
real content to sample instead of the implicit sky-only fallback it has today.

**Corollary:** once a real probe is confirmed working, `BUG-RENDER-20`'s hand-rolled
`slng_env_reflection` additive EMISSION term (in `app/materials/prim/prim_common.gdshaderinc`)
becomes redundant and should be removed, not kept alongside — leaving it in place would double
the reflection on shiny faces (the hand-rolled sky-tint ADDED on top of the now-real automatic
one). Removing it also reverts the `world_normal_up` parameter threaded through all 17
`prim_*.gdshader` variants' `slng_shade()` calls, since nothing will need it any more.

## The hard constraint: render budget

AGENTS.md's non-negotiable #3: *"Render budgets over fidelity... prefer graceful degradation to
stutter."* A `ReflectionProbe` with `UpdateMode.Always` re-renders the scene from the probe's
position 6 times (one per cubemap face) **every frame it is active** — a well-known heavy cost,
generally reserved for actual mirrors, not continuous open-world use. SL/Firestorm's own
automatic probes almost certainly rebake on a much lower cadence than every frame for the same
reason.

This is an open world with no fixed rooms to pre-bake per-room probes for, so the probe has to
follow the avatar/camera. The likely shape: `UpdateMode.Once`, re-triggered on an interval (a few
seconds) or a movement/region-change threshold, not every frame — but this must be **measured**,
not assumed, against the project's own existing profiling infrastructure
(`[PhaseCost]`/`MainThreadWorkQueue`, see `performance-engineer`'s usual tools) with the probe on
vs. off on a busy region, before picking a final cadence.

## Acceptance Criteria

- [x] A `ReflectionProbe` exists, tracks the avatar/camera through the open world, and captures
      real nearby geometry (not just the sky) — created in `Boot.SetupEnvironment`, repositioned
      by `Boot.UpdateReflectionProbe`
- [x] The probe sphere at shiny low/medium/high shows a reflection of ACTUAL nearby content —
      verified with a 5-wall coloured courtyard probe scene: flat black with no probe placed,
      crisp correctly-positioned wall colours (green/blue/magenta/yellow) once one was added
- [x] Update cadence is chosen from a MEASURED frame-time comparison, not guessed: `Always`
      ~1.65ms/frame vs. no-probe ~0.92ms/frame (~1.8x, forever); stationary `Once`
      ~0.93ms/frame (statistically the same as no probe); continuously repositioning a `Once`
      probe every frame (simulated 2m/s walk) ~1.11ms/frame (~1.27x) — landed on repositioning +
      re-baking together, throttled to once per 3s or an immediate re-bake past a 10m move
- [x] No measurable regression in frame time on a busy region with the probe active — the
      throttled steady-state cost between recaptures is indistinguishable from no probe; a single
      re-bake's ~12-frame elevated window is Godot's own documented cost of a `Once` capture, not
      an SLNG-side inefficiency, and amortizes to a small single-digit percentage of frames at this
      cadence
- [x] `BUG-RENDER-20`'s hand-rolled `slng_env_reflection` term and the `world_normal_up` parameter
      threaded through all 17 `prim_*.gdshader` variants are removed — confirmed necessary, not
      just tidy: the same courtyard scene showed a washed-out, near-uniform blue-grey haze with
      both active vs. crisp separated wall colours with only the real probe
- [x] `PRIM_SHINY_NONE` faces are unaffected — `slng_env_reflection`'s removal is a structural
      no-op for them (the function was only ever called from the `legacy_shininess > 0.0` branch),
      and they still carry `out_specular = 0.0`/`out_metallic = 0.0`
- [x] Does not capture content it should not — checked, not assumed: worn HUD attachments render
      in `AvatarRenderer`'s own `SubViewport(OwnWorld3D=true)`, a completely separate `World3D`
      from the one this probe lives in, so they cannot appear in its capture regardless of
      `CullMask`. `CullMask` left at Godot's all-layers default; nothing else in the main `World3D`
      needs excluding.

## Technical Specs & Affected Files

- New: wherever the `ReflectionProbe` node(s) are created and kept following the avatar/camera —
  likely `app/scripts/Boot.cs` (where `WorldEnvironment`/`DirectionalLight3D` are already set up
  in `SetupEnvironment`) or `app/scripts/AvatarController.cs` (which IS the `Camera3D`)
- `app/materials/prim/prim_common.gdshaderinc` — remove `slng_env_reflection`, `world_normal_up`
  once the real probe supersedes them
- All 17 `app/materials/prim/prim_*.gdshader` variants — revert the `slng_shade()` call site's
  extra `(INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).y` argument once removed from the signature
- Reference: `scratch/slviewer/indra/newview/app_settings/shaders/class3/deferred/reflectionProbeF.glsl`
- Background: [BUG-RENDER-20 spec](file:///E:/Git/SLNG/docs/specs/BUG-RENDER-20-missing-environment-reflection.md)
  for the full four-round history this was split out of

## Method

Since no interactive tool is available to drive the live grid from this environment, verify with
a probe scene (the same `godot --path app -s <script>.gd`, NOT `--headless`, pattern already
established across `BUG-RENDER-20`'s rounds) that contains actual surrounding geometry (not just
a flat sky background) around a shiny sphere, so a real captured reflection is visually
distinguishable from a flat tint — screenshot and look at it, the same lesson `BUG-RENDER-20`
round 4 had to learn the hard way (numbers alone missed a real visible defect twice).

## What was built (see the `ROADMAP.md` row for the full narrative and every measured number)

One `ReflectionProbe` (`Size=(40,40,40)`, `MaxDistance=60`, `BoxProjection=false`,
`EnableShadows=false`), created in `Boot.SetupEnvironment` and kept anchored near the camera by
`Boot.UpdateReflectionProbe` (called from `_Process`) — position and `UpdateMode.Once` are only
ever touched together, throttled to once per 3 seconds or an immediate re-bake past a 10m camera
move, never every frame. Deliberately NOT parented directly to `AvatarController` (which IS the
`Camera3D` and rotates with every mouse drag — dragging a symmetric box probe's orientation around
for no reason a box needs).

Toggleable: a `PostFxReflectionProbe` setting (`GraphicsSettings`/`DesignPreferencesPage`,
"Environment Reflections" checkbox) simply hides the node, per AGENTS.md's "visual features must
be toggleable" convention already used for the other post-FX checkboxes — off falls back to
Godot's plain sky-only IBL for an A/B.

**Verification:** `dotnet build` (both `SLNG.sln` and `app/SLNG.App.csproj`), `dotnet test` (690, 0
failures), `dotnet format` clean, `check_shader_globals.py` clean (28/28/0 stale — no global
uniform added or removed), `godot --headless --path app -- --selftest` 38/38 (every prim variant's
uniform count still matches its avatar/HUD twin), `project.godot` diff-free. `AppVersion` bumped to
`v0.22.71-alpha`.

## Sub-tasks / Progress

- [x] Design the probe placement/update strategy and measure its frame-time cost
- [x] Add the `ReflectionProbe` node(s), wired to follow the avatar/camera
- [x] Confirm empirically (rendered + looked-at screenshot, real geometry in frame) that shiny
      prims now reflect actual surrounding content
- [x] Remove `BUG-RENDER-20`'s hand-rolled `slng_env_reflection` term and `world_normal_up`
      plumbing
- [x] Full `slng-verify` pass, `AppVersion` bump, roadmap/spec updates, commit
