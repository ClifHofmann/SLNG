# HANDOVER

**One file.** Overwrite the section below when you hand over new work; do not create
`handover-<date>-<topic>.md` alongside it.

---

# FEAT-ENV-01 water + the Lbsa Plaza bug hunt

**State:** `v0.7.53-alpha`, three commits on `fix/FEAT-NET-04-viewer-version-and-anim-quaternion`,
which chains onto two earlier branches. **Not merged, not pushed.**

```
c20fa0b  fix(app):    [FEAT-NET-04] viewer version + rotation interpolation
a74bc58  fix(net):    [FEAT-ENV-01] stop flooding the region environment capability
e93005e  feat(render):[FEAT-ENV-01] port SL's water wave field from waterV/waterF
220386e  main
```

Each branch contains the ones below it, so merging the top branch brings all three. Build clean
(solution **and** `app/`), 230 tests green, `dotnet format` adds no new violations.

## Build and verify

```bash
dotnet build SLNG.sln && dotnet test SLNG.sln && dotnet build app/SLNG.App.csproj
```

**`dotnet build SLNG.sln` does not compile `app/`** — `SLNG.App` is deliberately outside the
solution. Always build the app project too. This bit again this session: a `CS1503` in
`GridSession.cs` only surfaced because the solution build happened to cover it.

**`--selftest` does not exist.** `AGENTS.md` documents
`godot --headless --path app -- --selftest`; nothing in the repo reads that argument. The command
just launches the client headless and sits at the login screen forever. To parse-check shaders
without a login, drop a `SceneTree` script in `app/` that `load()`s each `.gdshader` and run
`godot --headless --path app --script res://<name>.gd` — that does report parse errors.

---

## 1. Water — wave field ported, appearance still approximate

Confirmed by eye against the live sim: "sieht bisher ganz gut aus". Not yet A/B'd against
Firestorm.

Two EEP parameters were driving the wrong things, settled against `lldrawpoolwater.cpp:251-290`,
which is what actually feeds each uniform:

- **`scale_above` / `scale_below` is `refScale`** — the scale of the screen-space *refraction*
  offset, chosen per frame by whether the camera is under the surface. It was scaling the wave
  UVs. At its 0.0299 default standing in for the viewer's 0.04/0.45/0.1 wave scales, ripples came
  out roughly 30× too large.
- **`normal_scale` is neither UV tiling nor bump depth.** It shapes the normal used for the
  reflection sample only (`wave_ibl`). It was doing both.

Ported instead: the three wave UV sets from `waterV.glsl` including the non-linear sweep term,
the weighted combination `(wave1 + wave2*0.4 + wave3*0.6) * 0.5`, and `wave_ibl` with its
`z *= 2.0` — which makes the (2,2,2) default *flatten* the surface, not roughen it. Wave time is
`TIME * 0.5`, matching `phase_time`.

**Traps this cleared, worth not re-discovering:**

- `class1/environment/waterF.glsl` is a magenta "compilation failed" fallback, **not** a legacy
  water path. The only water fragment shader the viewer runs is the class3 one, with a
  `classic_mode` uniform selecting legacy behaviour inside it.
- The normal is set in **view space**, not through `NORMAL_MAP`. Godot's tangent frame for
  `PlaneMesh` does not guarantee a binormal sign matching SL's north, and a silent Y flip lights
  every wave from the wrong side.
- The water plane is at **7 subdivisions = 8 segments = 32 m per quad**, which is `LLVOWater`'s own
  tessellation (`llvowater.cpp:137-142`). Not a quality knob — the UVs are generated per vertex and
  the sweep term is piecewise-linearised by the mesh, so density is part of the pattern.

**Deliberately untouched:** `METALLIC` / `ALBEDO`. The viewer runs `metallic = 1.0` and gets the
water colour from the fresnel mix of the refracted framebuffer and the reflection. Without that
pass, changing it would undo the water colour measured against Firestorm on Howletts.

**Still open, in order:** the fresnel mix (`fresnel_scale` / `fresnel_offset` currently do
nothing), screen-space refraction using `refScale`, underwater fog (`water_fog_density` ×
`underwater_fog_mod` — Howletts sends density **16**, not the viewer default 2.0), and the second
normal map with `blend_factor`. One at a time, and A/B each.

`WaterSettings.Lerp` matches `LLSettingsWater::blend` field for field. The one simplification: the
viewer cross-fades two normal maps over a keyframe transition, we take the nearer keyframe's map.
Invisible on a region with one water frame.

---

## 2. The environment capability flood — this is what "your viewer is broken" meant

Someone told the user their viewer was broken and that it would show in the sim log. It did, but
not on their own sim — on **Lbsa Plaza**, which is OSGrid's, so its server log is not visible to
us. The evidence was in **our own client log** instead.

Measured in one hour-long session: **2649 environment re-polls, ~45 a minute, sustained.** Each
fired three HTTP capability GETs and each re-delivered a byte-identical 6060-character payload,
which was then re-parsed, re-logged and re-written to the Phase-A LLSD dump file. Roughly **8000
requests aimed at someone else's server**, and an 18 MB client log that was 96 % `[ENV]` lines.

**The trigger is correct and stays.** `RegionInfo` is how an EEP-capable viewer is told the
environment changed (OpenSim's `EnvironmentModule.WindlightRefresh` routes AdvEnv clients to
`HandleRegionInfoRequest`), and `llenvironment.cpp:886` wires `requestRegion()` to the same signal
without filtering it either. **We do not cause the packets** — every `WindlightRefresh` call site in
OpenSim is a write path (POST/PUT/DELETE); a GET does not feed back. Lbsa genuinely sends them that
often. The real viewer survives because `requestRegion` issues one coalesced request, not three
uncoordinated ones.

So the fix is the response, not the trigger. In `GridSession`:

1. One re-poll at a time — overlapping fetches collapse into a latch.
2. A minimum 2.5 s gap. **Not invented**: OpenSim's own `UpdateEnvTime` refuses to push a client
   environment update more often than that, so polling faster cannot learn anything new.
3. The legacy Windlight GET is skipped on live re-polls. A region with no EEP cap still always asks.
4. **Nothing is published unless the payload changed** — this is what takes the downstream cost to
   zero. `EnvironmentFingerprint` + `EnvironmentRepollTests`, including the cases that must still
   republish: a parcel gaining its own environment, a region losing its day cycle, re-entering a
   region seen earlier.

**How to verify:** log in to Lbsa with the new build and count `[ENV]` lines in the client log.
Thousands → a handful is the whole test.

The **shutdown `ObjectDisposedException`** (two per close, on `RichTextLabel.AppendText`) was the
same code path: the re-poll delivers through `CallDeferred` from a background task, so a capture in
flight when the client closes lands a frame after the UI is gone. `LogMessage` now checks
`IsInstanceValid` — a null check does not work, because the C# wrapper outlives the native object.

---

## 3. Viewer version string, and rotation interpolation

**Every login announced `SLNG 0.1.0`.** `LoginCredentials.Version` was never set, so its hardcoded
default went to every sim we have ever touched while the build moved on to 0.7.x. The grid records
it and prints it on each arrival. Now carries `AppVersion`.

**27 `ArgumentException`s from `AvatarAnimationPlayer` in one session** — "Argument is not
normalized" / "Quaternion is not normalized", both out of `Quaternion.Slerp`.

The decode was **not** at fault. SL stores a keyframe rotation as three U16-quantized components
and rebuilds w as `sqrt(1 - |xyz|²)`; when quantization pushes `|xyz|` past 1 the viewer clamps the
negative radicand to `w = 0` rather than making a NaN (`LLQuaternion::unpackFromVector3`,
llquaternion.cpp:943). We port that faithfully, so what arrives is occasionally a little longer
than unit. `LLQuaternion`'s own math shrugs that off; Godot's does not, in **two** ways:
`Quaternion.Slerp` throws, **and a non-unit bone pose silently SCALES the bone** — the quieter half
of the same bug, which would have outlived the exceptions as "the avatar looks a bit off".
`ToGodotQuat` normalizes, which is the single point where SL rotations become Godot ones.

While reading the viewer for that, the interpolation itself turned out wrong:
`LLKeyframeMotion::RotationCurve::interp` calls **`nlerp`** (llkeyframemotion.cpp:277 →
llquaternion.cpp:694) — a normalized componentwise lerp, falling back to slerp only when the two
keys sit in opposite hemispheres. **We always slerped.** Both curves agree at the endpoints and
differ in between, so this reads as animation timing drifting from the real viewer, worst on widely
spaced keys.

The w reconstruction moved into `AnimationDecodeService.UnpackRotation` so the port is pinned by
known-answer tests **including the over-unit case** — otherwise the clamp reads like an oversight
and someone eventually "fixes" it into a normalize, moving the bug somewhere harder to find.

---

## 4. Still open

- **`[FaceTex] texture <id> fetch/decode returned null — face renders untextured`, 266× in one
  session.** Not investigated at all.
- **Water:** fresnel, refraction, underwater fog, `blend_factor` (§1).
- **`--selftest`** is documented in `AGENTS.md` and does not exist.
- **FEAT-RENDER-02 (terrain detail blending) has one unexplained visual impression left**:
  Firestorm appears to show more of the low band around the island's edge than we do, and no
  measurement reproduces it — every input is verified identical against the sim's own `terrain.raw`.
  Five known-answer points in `tools/testassets/README.md` settle it; stand at each in both viewers.
  **Do not tune against a screenshot to close this** — that caused the two worst detours in that
  task. Its `LogCompositionDiagnostics` output is still in the build via `GD.Print`, deliberately
  and temporarily; remove it once the point test is done.
- A **parallel, unfinished EEP implementation** is parked in `stash@{0}`
  (`GodotEnvironmentManager`, `EnvironmentWindow`), plus a `feature/FEAT-ENV-01-windlight-eep`
  branch. Not ours. Two implementations of the same feature exist; that needs a human decision, not
  a blind merge.
- A leftover worktree sits at `.claude/worktrees/sad-yalow-b74c97` on `7b80e58`. The user does not
  want worktrees for this project — everything stays in `E:\Git\SLNG`. Safe to remove.

**Checked and NOT a bug:** Lbsa's sky reads as `blueHorizon = (2, 0, 0)`, `hazeDensity = 0`, which
looks like a parse failure. The capture file says the region really publishes `[2, 0, 0]`. Our
parser is right; Lbsa's EEP data is odd.

---

## 5. Watching the sim log

The user's own OpenSim runs in Docker on their server. A background follower may still be running
from the previous session — leave it if so.

```bash
ssh meernet 'docker exec os-osgrid-1 tail -n 0 -F /opt/opensim/bin/OpenSim.log'
```

`~/.ssh/config` already has the host and key. `docker logs os-osgrid-1` is **not** the sim log —
that shows only syslog/cron; OpenSim writes to the path above inside the container.

Two things in that log that look alarming and are not ours: `Malformed data, cannot parse N byte
packet` is SIP scanner traffic from random internet IPs hitting the UDP port, and `GETASSET: asset
with wrong type` came from another agent's uploads. A genuine viewer-side signature is
`No packets received from root agent ... for 60000ms. Disconnecting.` — 21 of those, last on
2026-08-21 00:35, none since.

---

## 6. FEAT-ENV-01 background, folded in from the old phase A–D handover

That file is now deleted; these are the parts still worth knowing.

- **`legacy_haze` is load-bearing.** SL nests haze parameters under a `legacy_haze` submap when
  they came from legacy Windlight, and the viewer's getters check that submap **before** the top
  level (`llsettingssky.cpp:1383-1411`). All 21 sky frames in the real OSGrid capture nest it
  there. Missing it renders a region with default haze and no error anywhere.
- **Cap timing:** the capture subscribes to `EventQueueRunning`, **not** `SimConnected` — at
  `SimConnected` the cap seed is not guaranteed fetched and `CapabilityURI` reports a real
  capability as absent.
- **`AmbientLightSource` must be `Color`, not `Sky`.** Godot silently ignores `AmbientLightColor`
  under `Sky` mode. That line is load-bearing, not decorative.
- **`tests/SLNG.Net.Tests/TestData/environment-dangazi-eep.llsd`** is committed real grid data —
  the only sample of what a grid actually sends, and re-capturing costs a login.
  `EnvironmentCaptureParityTests` parses it every run.
- **Correction to that old handover:** it presented `SkyLighting.Calculate`
  (`LLSettingsSky::calculateLightSettings`) as the required port for the shader's
  `sunlight_color`/`ambient_color`. It was later established that
  `calculateLightSettings` is **dead code** in the modern viewer — `getSunDiffuse`, `getMoonDiffuse`
  and `getLightDiffuse` have no consumer in `indra/newview`. Do not treat that section as current.
- **Never confirmed against SL itself**, only OpenSim regions. The legacy-Windlight fallback is
  unit-tested against hand-written LLSD but has never been exercised against a real grid response.
- **`dotnet format --verify-no-changes` is not clean repo-wide** and predates all of this
  (`GridSession.cs` alone carries 33 pre-existing violations, `Boot.cs` more). Verify that your
  change adds none rather than expecting a clean run.

---

## 7. Method notes that keep paying off

- **Read the client log before asking the user anything.** The Lbsa flood, the quaternion crashes
  and the 266 texture failures were all sitting in `%APPDATA%\Godot\app_userdata\Puris Viewer\logs\`
  the whole time. Normalising log lines and counting them found in one pass what screenshots would
  not have found at all.
- **Check whether the symptom is even ours.** Three of the five things that looked like viewer bugs
  this session were not: the SIP packets, the wrong-type asset requests, and Lbsa's odd sky. Each
  took under a minute to exclude and would have cost hours to "fix".
- **Verify the viewer's behaviour before deciding what parity means.** The rotation fix started as
  "normalize the quaternion" and became "we were using the wrong interpolation entirely" only
  because the viewer source got read. Likewise the water: the bug was not the shader math but what
  two parameters *are*.
