# [FEAT-ENV-01] Region Environment — Windlight / EEP acquisition, model and publish

- **Feature ID:** `FEAT-ENV-01`
- **Track:** `net` / `core` / `render`
- **Status:** `✅ Done` (Phases A–D confirmed live against Firestorm on two OSGrid regions, 2026-08-21; Phase E — the atmospherics consumption half — landed under FEAT-RENDER-08 and was verified against the code on 2026-09-09, `v0.22.2-alpha`)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md) · [ADR 0002](file:///E:/Git/SLNG/docs/adr/0002-custom-spatial-shader-family.md) · [ADR 0003](file:///E:/Git/SLNG/docs/adr/0003-eep-parameter-model-and-lighting-seam.md) · [FEAT-RENDER-01](file:///E:/Git/SLNG/docs/specs/FEAT-RENDER-01-custom-spatial-shader-family.md)

> **Read [ADR 0003](file:///E:/Git/SLNG/docs/adr/0003-eep-parameter-model-and-lighting-seam.md) before touching Phase E.** It inventories the full
> 54-key sky / 12-setting water parameter set with real ranges and real consumers,
> and records the load-bearing finding that the viewer's own
> `calculateLightSettings` is **dead code** — `getSunDiffuse`, `getMoonDiffuse`,
> `getLightDiffuse` and `getMoonAmbient` have no caller in `indra/newview`. Our
> `DirectionalLight3D` + global-ambient mapping is built on it, which is why the
> day/night lighting cannot be brought to parity by tuning constants.

## Overview & Goal

Every region in SL and OpenSim ships its own **environment**: sky colours, haze, sun and
moon, clouds, and water. It is what makes a sim read as noon, sunset or midnight. SLNG
currently ignores all of it — `Boot.SetupEnvironment` builds one hardcoded
`ProceduralSkyMaterial` plus Godot's own volumetric fog and never changes it, so every
region on every grid looks the same.

This feature is the **data half** of Windlight/EEP: fetch the region's environment, turn
it into an engine-neutral model, evaluate the day cycle over time, and publish it to the
renderer. It deliberately stops at the shader boundary.

The **render half** — filling `slng_apply_atmospherics` in the shader family so haze is
applied per fragment on every surface — is `FEAT-RENDER-01` Phase 5 and stays there. The
split is on purpose: the two halves have different dependencies (this one needs nothing
from the shader migration; Phase 5 needs `FEAT-RENDER-01` Phases 3–4 finished, or prims
would get atmospherics while avatars, terrain and water do not, which is exactly the
visible seam ADR 0002 exists to avoid).

## What exists today (verified, 2026-08-19)

| Piece | State |
|---|---|
| Sun **direction** | Live. `GridSession.SunDirection` reads `GridClient.Grid.SunDirection` (`SimulatorViewerTimeMessage`); `Boot.UpdateSunFromRegion` aims the `DirectionalLight3D` at it every `_Process`. |
| Sun **colour**, ambient, haze, sky, clouds, water | **Nothing.** Hardcoded `ProceduralSkyMaterial`, fixed `AmbientLightEnergy = 1.0`, Godot `VolumetricFogEnabled` at a fixed density. |
| Atmospherics shader seam | Present and a deliberate no-op: `slng_apply_atmospherics(color, view_position)` in `app/materials/prim/prim_common.gdshaderinc`. |
| Shader family coverage | Prims only. `AvatarRenderer`, terrain and water are still `StandardMaterial3D` / their own one-off shaders (FEAT-RENDER-01 Phases 3–4 open). |
| Protocol support | **Already available, unused.** See below. |

## Data sources (verified against the pinned dependency)

`LibreMetaverse 3.1.3` — the version `src/SLNG.Net/SLNG.Net.csproj` already pins — ships
`LibreMetaverse.EnvironmentManager`, reachable as `GridClient.Environment`. This was
confirmed by compiling a probe against the exact NuGet package, not by reading the
vendored `scratch/libremetaverse_src` checkout, which is newer than the pinned package and
could have diverged.

```csharp
EnvironmentManager env = client.Environment;
await env.GetRegionEnvironmentAsync();          // ExtEnvironment cap      -> ExtEnvironmentMessage?
await env.GetParcelEnvironmentAsync(parcelId);  // ExtEnvironment cap      -> ExtEnvironmentMessage?
await env.GetLegacyEnvironmentAsync();          // EnvironmentSettings cap -> LegacyEnvironmentMessage?
env.RegionEnvironmentUpdated += ...;            // fires ONLY from the GETs above -- see below
env.RegionEnvironment;  env.LegacyEnvironment;  // last retrieved
```

Both capability names are already in LibreMetaverse's requested set (`Caps.cs:166`
`EnvironmentSettings`, `Caps.cs:170` `ExtEnvironment`), so no cap-list change is needed on
our side.

**Payload shapes.** `ExtEnvironmentMessage` carries `Success`, `ParcelId` (`-1` = region),
`Version`, `Message` and an `EnvironmentData?`. `EnvironmentData` gives `DayLength`
(default 14400 s), `DayOffset` (default 57600 s), `Flags`, `IsDefault`, and `DayCycle` as
**raw `OSD`** — LibreMetaverse does not decode the settings themselves. Its `"type"` key
discriminates `daycycle` / `sky` / `water`. `LegacyEnvironmentMessage.Settings` is likewise
raw `OSD`. **Parsing the settings LLSD is our job**, and it is the bulk of Phase B.

Two things measured during Phase A that change the design:

- **`EnvironmentData.SkyTrack` does not exist in 3.1.3.** It is present only in the newer
  vendored `scratch/libremetaverse_src` checkout. This is the divergence warned about
  above, now concrete: it compiled against the vendored source and failed against the
  pinned package. Anything read off the vendored tree must be re-checked against 3.1.3
  before it is designed in.
- **There is no push notification for environment changes.** `EnvironmentManager` raises
  `RegionEnvironmentUpdated` / `ParcelEnvironmentUpdated` / `LegacyEnvironmentUpdated` at
  exactly one place each — inside its own GET methods. It registers no EventQueue
  callback. Subscribing to those events therefore tells us nothing we did not already
  learn by awaiting the call, and an environment changed server-side after login will not
  reach us at all until something re-polls. Live updates need either polling or an
  EventQueue handler contributed upstream; a design that assumes the events arrive on
  their own is wrong.

**LLSD key names** are pinned by the viewer source, not guessed:

- Sky (`llsettingssky.cpp:72-113`): `ambient`, `blue_density`, `blue_horizon`,
  `haze_density`, `haze_horizon`, `density_multiplier`, `distance_multiplier`, `max_y`,
  `glow`, `gamma`, `lightnorm`, `cloud_color`, `cloud_scale`, `cloud_shadow`,
  `cloud_pos_density1`, `cloud_pos_density2`, `cloud_scroll_rate`, `cloud_variance`,
  `cloud_id`, `sunlight_color`, `sun_rotation`, `sun_scale`, `sun_id`, `moon_rotation`,
  `moon_scale`, `moon_brightness`, `moon_id`, `star_brightness`, `bloom_id`.
  Legacy Windlight variants: `east_angle`, `sun_angle`, `enable_cloud_scroll`.
- Water (`llsettingswater.cpp:36-48`): `water_fog_color`, `water_fog_density`,
  `underwater_fog_mod`, `fresnel_offset`, `fresnel_scale`, `blur_multiplier`,
  `normal_scale`, `scale_above`, `scale_below`, `wave1_direction`, `wave2_direction`,
  `normal_map`, `transparent_texture`. Legacy variants are the same names in camelCase
  (`waterFogColor`, `fresnelScale`, …, `llsettingswater.cpp:50+`).

**Out of scope, and this is a measurement rather than an opinion:** the
advanced-atmospherics fields (`planet_radius`, `sky_top_radius`, `sky_bottom_radius`,
`rayleigh_config`, `mie_config`, `absorption_config`) are read by `llsettingssky` and
`llenvadapters.h` and reach **no shader in the viewer tree** — grepping
`indra/newview/app_settings/shaders/` for them finds zero uses. The surface atmospherics
the viewer actually runs is the haze model below. Parse them into the model if convenient,
but nothing consumes them.

## The target math

`class1/windlight/atmosphericsFuncs.glsl` is the function to port, and its uniform list is
exactly the publish surface this feature has to produce:

```
lightnorm, sunlight_color, moonlight_color, sun_up_factor, ambient_color,
blue_horizon, blue_density, haze_horizon, haze_density, cloud_shadow,
density_multiplier, distance_multiplier, max_y, glow, scene_light_strength,
sun_moon_glow_factor, sky_sunlight_scale, sky_ambient_scale, classic_mode
```

`calcAtmosphericVars` returns four values per fragment — `sunlit`, `amblit`, `additive`,
`atten` — which the viewer's fragment shaders combine as roughly `color * atten + additive`.

Note that these uniforms are **not** the raw LLSD fields. `sunlight_color` and
`ambient_color` are *derived* from the settings by `LLSettingsSky::calculateLightSettings`
(`llsettingssky.cpp:1704`), which applies the atmospheric attenuation
`exp(-light_atten / |lightnorm.z|)` and the cloud-shadow ambient lift. The two functions
name each other in their own comments as "similar/shared algorithms"; keep that pairing
intact when porting, because the CPU/GPU split between them is where a naive port goes
wrong — feeding the raw LLSD values straight into the shader gives a plausible-looking but
wrong result at every sun angle.

## Design

### Layering

```
SLNG.Net    fetches the caps, parses OSD -> Core records   (OSD is a LibreMetaverse type
                                                            and must not cross the boundary)
SLNG.Core   engine-neutral SkySettings / WaterSettings / DayCycle records,
            plus the pure day-cycle evaluation (a function of time; no I/O, no Godot)
app         one EnvironmentDriver node: applies the evaluated settings to the
            WorldEnvironment, the sun, and the global shader uniforms
```

This mirrors the existing terrain path (`TerrainSettingsEvent`,
`src/SLNG.Core/GridEvents.cs:163`) — same shape, same direction.

### Threading

Cap fetches are async HTTP and `EnvironmentManager`'s events fire on background threads.
Per AGENTS.md, **buffer and apply on the main thread once per frame** — the rule the rest
of `GridSession` already follows. Nothing here may touch `World` or a Godot node from a
network thread.

### Publish mechanism

Once per frame, via `RenderingServer.GlobalShaderParameterSet` — **not** per-material
properties. ADR 0002 already settled this: per-material would mean CPU-side churn across
every object in view, every frame. Uniform names mirror the viewer's
(`slng_blue_horizon`, `slng_haze_density`, …) so the port stays readable against the
source it came from.

### Day cycle

`EnvironmentData` gives `DayLength` and `DayOffset`; the day-cycle LLSD gives keyframes
(0..1 along the cycle) per track. Evaluating "what are the sky settings right now" is a
pure interpolation between the two bracketing keyframes — which is why it belongs in
`SLNG.Core` and is unit-testable with no grid at all.

## Phases

Each phase is independently verifiable; the first three change nothing on screen.

### Phase A — Acquisition and diagnostics (no visual change)

Fetch the region environment on connect and on `RegionEnvironmentUpdated`; log which of
`ExtEnvironment` / `EnvironmentSettings` the sim actually advertises, and dump the raw
LLSD to `user://logs/environment-<region>.llsd`.

Rationale: **we do not currently know what OSGrid returns.** OpenSim's EEP support varies
by version, and the fallback chain (EEP → legacy Windlight → viewer default) cannot be
designed against a guess. One live login produces the fixture Phase B is then tested
against. This is deliberately the cheapest phase, because it is the one that removes
assumptions.

### Phase B — Engine-neutral model + parser (no visual change)

`SkySettings`, `WaterSettings`, `DayCycle` records in `SLNG.Core`; an internal LLSD parser
in `SLNG.Net` producing them; xUnit tests against the Phase A capture **and** against the
viewer's own defaults. Test-first, per AGENTS.md — this is protocol code.

### Phase C — Day-cycle evaluation (no visual change)

Pure `SLNG.Core` function: `(DayCycle, dayLength, dayOffset, regionTime) -> SkySettings`.
Unit-tested at keyframe boundaries, wrap-around, and single-frame ("fixed sky") cycles.

### Phase D — Drive Godot's environment (first visible change)

**Landed, `v0.7.0-alpha`.** `app/scripts/EnvironmentDriver.cs`, evaluated once per frame from
`Boot._Process` alongside `UpdateSunFromRegion` (same `SunDirection` authority — the sky dome
and the actual lit scene cannot disagree about which way is day, since both read it). Applies:

- Sun colour/energy from `SkyLighting.SunDiffuse`, split into a normalized colour + an energy
  scalar (Godot's own convention — SL's derived sunlight is an unbounded scattering value,
  often >1 on one channel, and clamping the colour would lose exactly the magnitude that makes
  a sunset read as orange rather than grey).
- Ambient from `SkyLighting.SunAmbient`, same split. Requires flipping
  `Environment.AmbientLightSource` from `Sky` to `Color` — Godot silently ignores
  `AmbientLightColor` under `Sky` mode, so this line is not decorative.
- `ProceduralSkyMaterial`'s four flat colours (zenith/horizon × sky/ground) tinted from
  `BlueDensity`/`HazeColor`, with a day/night energy multiplier read from the SAME
  `lightDirectionZ` the lighting uses. **Documented in-code as a deliberate approximation** —
  `ProceduralSkyMaterial` has no per-fragment atmospherics hook, so this cannot be a port of
  `calcAtmosphericVars`; it exists to make the scene visibly change with time of day before
  Phase E can do that properly.
- Classic depth fog (`Environment.FogEnabled`/`FogLightColor`/`FogDensity`) from the haze
  colour, with **`VolumetricFogEnabled` turned off** — this is the "reconciled, not left
  double-applying" requirement: before this driver, volumetric fog ran at a fixed density with
  nothing region-aware about it, and leaving both active would make the visible haze the SUM of
  a region-driven fog and a hardcoded one that never moves.
- The water plane's `albedo` shader parameter (`water.gdshader`) from `WaterSettings.FogColor`,
  keeping the shader's own default alpha (SL has no separate surface-transparency setting).

Still `StandardMaterial3D`/hand-written shaders everywhere else — this produces a real
sunset without touching the shader family, and it is the phase that proves the whole data
chain end to end. **Not yet verified against a live login** — built, unit-tested at the model
layer, and headless-booted clean, but nobody has watched the sky actually move on a real
region yet.

Godot's `VolumetricFogEnabled` and the `ProceduralSkyMaterial` must be **reconciled here,
not left double-applying** once Phase E lands.

### Phase E — Hand off to FEAT-RENDER-01 Phase 5 ✅

Fill `slng_apply_atmospherics` with the `calcAtmosphericVars` port, reading the global
uniforms this feature publishes. **Blocked on FEAT-RENDER-01 Phases 3–4**: until avatars,
terrain and water are on the shader family, only prims would receive atmospherics and the
seam would be visible at exactly the sun angles this feature exists to render.

**Done.** The port landed incrementally under **FEAT-RENDER-08** (`a911215`…`fff825f`,
`v0.20.63`–`v0.20.93`) rather than under either of the two ids that specify it, which is why
this phase and FEAT-RENDER-01 Phase 5 both stayed unticked well after the work was live. It is
`calcAtmosphericVars` + `atmosFragLighting` with both halves — extinction `color * atten.r` and
in-scatter returned separately so the caller can route it through `EMISSION` rather than
`ALBEDO` (Godot would otherwise scale the haze by the scene lighting, so dusk went dark instead
of bright). Prims and avatars reach it through one shared call site inside `slng_shade`;
terrain calls it directly; water takes only `atten.r` and gets its haze from the already-hazed
screen texture, exactly as `class3/environment/waterF.glsl` does. See FEAT-RENDER-01's
acceptance list for the per-criterion evidence, and FEAT-RENDER-08 for the five deviations the
Firestorm A/B found.

## Open questions — settle by measurement, not by reasoning

1. ~~Does OSGrid advertise `ExtEnvironment`?~~ **Answered:** yes, and it returns a full day
   cycle. Whether SL agrees, and which sims fall back to `EnvironmentSettings`, is still open —
   the fallback path exists and is unit-tested but has never run against a real grid.
2. Whether the legacy `EnvironmentSettings` capability was advertised on that region is
   **unknown**: the summary line naming the capabilities went only to the on-screen panel, which
   is capped at 200 lines, so it was gone before anyone looked. Fixed in `v0.6.3-alpha` — the
   `[ENV]` diagnostics now also go to stdout and therefore to `godot.log`.
3. **Sun position authority.** We already aim the sun from `SimulatorViewerTimeMessage`,
   which is live and correct. EEP also carries `sun_rotation` per keyframe. Which wins,
   and do they agree? Keep `SimulatorViewerTime` as the authority until a measurement says
   otherwise — changing it blind would regress a fix that already cost a session (see the
   comment block on `Boot.UpdateSunFromRegion`).
4. Parcel-level environments need parcel tracking we do not have yet. Region-level only
   for now; note it rather than half-building it.
5. `classic_mode` — the viewer runs two tonemapping paths through these same uniforms
   (`calcAtmosphericVarsLinear` branches on it). Pick one and record which, or the colours
   will not match Firestorm at any sun angle.

## What the live capture showed (OSGrid, The Dangazi Forest, 2026-08-19)

Phase A's whole purpose, answered in one login. The region returned a **full EEP day cycle**
through `ExtEnvironment` — `type: "daycycle"`, 21 sky frames, 3 water frames, 33 keyframes
spread across the tracks, with track 0 water and track 1 ground-level sky exactly as
`llsettingsdaycycle.cpp` describes. No legacy Windlight dump was produced.

The finding that mattered: **all 21 sky frames nest their haze parameters under
`legacy_haze`.** Not one carries them at the top level. A parser reading only the top level
would have rendered this region — and, going by how OpenSim generates these documents, most of
OpenSim — with default haze, silently and with nothing in any log to explain it. The two-level
lookup was written from `llsettingssky.cpp:1383-1411` before the capture existed; the capture
turned it from a defensive read into a load-bearing one.

`EnvironmentCaptureParityTests` now parses that exact document on every test run and asserts the
structure, that the haze block is actually being read (the frames must not all collapse to the
same `blue_horizon`), that water frames resolve their real normal map, that every position in the
cycle evaluates to a finite sky and finite lighting, and that the sky genuinely changes between
midnight and noon.

## Acceptance Criteria

- [x] Region environment is fetched once the region's capabilities are live
      (`EventQueueRunning`, not `SimConnected` — the caps seed may not have been fetched
      yet at `SimConnected`), on a background thread, applied on the main thread.
- [x] Re-fetch strategy for server-side environment changes, since no push notification
      exists (see above). Settled in two halves by
      [BUG-NET-09](file:///E:/Git/SLNG/docs/specs/BUG-NET-09-parcel-only-environment-poll.md),
      against real reference-viewer source rather than by picking an interval:
      a **region-wide** change already pushes — it arrives as a `RegionInfo` packet →
      `RepollEnvironment()`, the same wiring as `llenvironment.cpp`'s `LLRegionInfoModel` →
      `requestRegion()`. A **parcel-only** edit has no reachable push at all: the real viewer
      reads `ParcelEnvironmentVersion` out of an unsolicited `ParcelProperties`, and
      LibreMetaverse's `ParcelPropertiesMessage` never parses that field, with no raw-LLSD
      fallback in its public API. So that half polls: `ParcelEnvironmentPollLoopAsync`, 30 s,
      reusing the existing single-flight / 2.5 s-gap / publish-only-on-change throttling.
      **Confirmed in-world 2026-09-07 (Agni)** — a parcel-only Windlight change is picked up
      within one interval without a relog.
- [x] No LibreMetaverse type (`OSD`, `UUID`, `ExtEnvironmentMessage`) crosses a public
      `SLNG.Net` boundary; `SLNG.Core` stays free of protocol and Godot types. Checked, not
      assumed: `SLNG.Core.csproj` has no `ProjectReference` or `PackageReference` at all and no
      `using OpenMetaverse` / `using LibreMetaverse` / `using Godot` anywhere under
      `src/SLNG.Core/`. `RegionEnvironmentCapture` and `RegionEnvironmentEvent` — the two types
      on `GridSession`'s public environment events — carry only primitives and Core records;
      the LLSD travels as `string?` notation text, never as `OSD`. `EnvironmentLlsdParser` does
      take `OSD`/`OSDMap`, but the class is `internal`, so those signatures are not a public
      boundary. A recursive sweep of every `public`/`protected` member in `src/SLNG.Net/` and
      `src/SLNG.Assets/` for `OSD`, `OSDMap`, `OSDArray`, `UUID`, `ExtEnvironmentMessage`,
      `Primitive`, `Simulator`, `FacetedMesh`, `AssetMesh`, `AgentManager` and `GridClient`
      returns nothing.
- [x] Day-cycle evaluation is a pure `SLNG.Core` function with unit tests covering
      keyframe interpolation, wrap-around and fixed-sky cycles.
- [x] EEP and legacy Windlight both parse into the same engine-neutral model; the fallback
      chain is explicit (`EnvironmentSource` on the event) and logged. **Exercised by tests, not
      yet by a real grid.**
- [x] Sky, sun and water visibly change with the region's time of day (Phase D,
      `v0.7.0-alpha`) — implemented and headless-boot clean; **needs a live login to confirm it
      actually looks right**, not just that it runs.
- [x] Windlight parameters are published as global shader uniforms once per frame — no
      per-material updates. `Boot._Process` → `EnvironmentDriver.Update` →
      `UpdateGlobalShaderParameters`, with no change-guard on the path;
      `tools/check_shader_globals.py` reports 28 registered / 28 typed / 0 stale. Live-verified
      with real region values (`[SkyAtmos]`, Millenium), not defaults. Same criterion as
      FEAT-RENDER-01 Phase 5's first.
- [x] `dotnet build` + `dotnet test` clean; `dotnet format` clean. Verified at
      `v0.22.1-alpha`: `SLNG.sln` 8 projects / 0 errors, `app/SLNG.App.csproj` 4 projects /
      0 errors (built separately — the solution compiles none of `app/`), 657 tests green
      (229 Core + 102 Assets + 326 Net), `dotnet format --verify-no-changes` clean,
      `check_shader_globals.py` 28/28/0, `--selftest` 32/32 with `project.godot` untouched.

## Technical Specs & Affected Files

- `src/SLNG.Core/SkySettings.cs`, `WaterSettings.cs`, `DayCycle.cs` *(new)* — engine- and
  protocol-neutral records, alongside the existing `RegionTerrain.cs`.
- `src/SLNG.Core/GridEvents.cs` — a `RegionEnvironmentEvent`, shaped like
  `TerrainSettingsEvent`.
- `src/SLNG.Net/EnvironmentLlsdParser.cs` *(new, internal)* — OSD → Core records.
- `src/SLNG.Net/GridSession.cs` — subscribe to `EnvironmentManager`, fetch on connect,
  buffer and raise.
- `app/scripts/EnvironmentDriver.cs` *(new)* — evaluates the cycle per frame and applies it
  to `WorldEnvironment`, the sun, and `RenderingServer.GlobalShaderParameterSet`.
- `app/scripts/Boot.cs` — `SetupEnvironment` hands ownership to the driver; `AppVersion`
  bump per phase.
- `app/materials/` — Phase E only, under FEAT-RENDER-01.
- `tests/SLNG.Core.Tests/`, `tests/SLNG.Net.Tests/` — parser and day-cycle tests.

## Sub-tasks / Progress

- [x] Phase A — fetch + cap diagnostics + raw LLSD capture (`v0.6.1-alpha`). Implemented as
      `GridSession.CaptureRegionEnvironmentAsync` → `RegionEnvironmentCaptured` →
      `Boot.DumpRegionEnvironment`, writing `user://logs/environment-<region>-eep.llsd` and
      `-legacy.llsd`. **Captured live 2026-08-19** from OSGrid's The Dangazi Forest — see the
      findings section below. Fixture committed at
      `tests/SLNG.Net.Tests/TestData/environment-dangazi-eep.llsd`.
- [x] Phase B — Core model (`SkySettings`, `WaterSettings`, `SkyLighting`) + the
      `EnvironmentLlsdParser` in `SLNG.Net` + 12 parser tests (`v0.6.2-alpha`). `GridSession`
      raises `RegionEnvironmentReceived` with the parsed `DayCycle` alongside the raw capture,
      from the same pair of GETs. Written against the viewer source, **not** against a live
      capture — the capture still has to confirm which shapes OpenSim actually sends.
- [x] Phase C — day-cycle evaluation in `SLNG.Core` + 11 tests covering keyframe interpolation,
      loop wrap-around, fixed skies, duplicate keyframes and the 0.0/1.0 degenerate span
      (`v0.6.2-alpha`).
- [x] Phase D — `EnvironmentDriver` drives Godot's sun / ambient / sky dome / fog / water
      (`v0.7.0-alpha`). Not yet confirmed against a live login.
- [ ] Phase E — atmospherics seam filled (FEAT-RENDER-01 Phase 5; blocked on its Phases 3–4).
