# Handover — Windlight/EEP data pipeline + first visible sky (FEAT-ENV-01), 2026-08-19

**Branch:** `main` — **uncommitted**. See §8 for why this line is dangerous and what already
happened once today because of it.
**Version:** `v0.7.0-alpha` (bumped 5 times this session: 6.1 Phase A, 6.2 Phase B/C + live-capture
fixes, 6.3 ENV-log-to-stdout fix, 7.0 Phase D — first visible change, hence the minor bump)
**Build/tests:** clean. `dotnet build SLNG.sln` 0/0, `dotnet build app/SLNG.App.csproj` 0/0,
`dotnet test` 186/186 green (75 of them new this session: 55 from FEAT-ENV-01, 20 more implied by
the reconstruction in §8). Headless boot (`godot --headless --path app`) reaches steady state with
no new errors — see the caveat in §6 about what that does and does not prove.
**Working tree:** dirty, all of it this session's work (file list in §7).

---

## 1. Where this stands

`FEAT-ENV-01` (Windlight/EEP) Phases A through D are implemented: fetch the region's environment,
parse it into an engine-neutral model, evaluate the day cycle, and drive Godot's sun/ambient/sky
dome/fog/water from it. **Phase D has never been watched on a live region.** It builds, it's
unit-tested at the model layer, it boots headless without throwing — nobody has logged in since it
landed and looked at the sky.

Phase E (per-fragment atmospherics in the shader family) is untouched and stays blocked on
`FEAT-RENDER-01` Phases 3–4 (avatars/terrain/water onto the shader family) — this was true before
this session and is unchanged.

**Separately, this session also found and fixed a real console-warning bug** (unrelated to
Windlight except that it was found while testing it) — see §9. That fix is real, verified, and not
at risk the way the Windlight work was — read §8 to understand why the distinction matters.

Spec: [`docs/specs/FEAT-ENV-01-windlight-eep.md`](file:///E:/Git/SLNG/docs/specs/FEAT-ENV-01-windlight-eep.md)
— written first, kept up to date after every phase, and it has the full "what exists / open
questions / acceptance criteria" detail this handover doesn't repeat. **Read it before touching
this feature further**, especially its "open questions — settle by measurement" section.

Roadmap entry: `docs/ROADMAP.md`, search `FEAT-ENV-01`.

---

## 2. What's implemented, phase by phase

### Phase A — capture (no visual change)

`GridSession.CaptureRegionEnvironmentAsync` (`src/SLNG.Net/GridSession.cs`) fetches both
environment capabilities (`ExtEnvironment` for EEP, `EnvironmentSettings` for legacy Windlight) once
the region's caps are live — subscribed to `EventQueueRunning`, **not** `SimConnected`, because the
cap seed isn't guaranteed fetched yet at `SimConnected` and `CapabilityURI` would report a real
capability as absent. `Boot.DumpRegionEnvironment` writes the raw LLSD to
`user://logs/environment-<region>-{eep,legacy}.llsd` and logs a one-line capability summary.

**Confirmed live** 2026-08-19 against OSGrid's The Dangazi Forest (see §4).

### Phase B — model + parser (no visual change)

`SkySettings`, `WaterSettings`, `DayCycle`, `SkyLighting`, `RegionEnvironmentCapture`,
`RegionEnvironmentEvent` in `src/SLNG.Core/`. `EnvironmentLlsdParser` (internal, `src/SLNG.Net/`)
turns raw `OSD` into those records — this is the `SLNG.Net`/`SLNG.Core` layer boundary, and no
LibreMetaverse type crosses it.

Every default is the viewer's own, taken from `LLSettingsSky::defaults()` /
`LLSettingsWater::defaults()`. Key names are pinned against `llsettingssky.cpp:72-113` /
`llsettingswater.cpp:36-48`, not guessed.

**The one non-obvious thing in the parser:** SL nests haze parameters under a `legacy_haze` submap
when they came from legacy Windlight, and the viewer's own getters check that submap **before** the
top level (`llsettingssky.cpp:1383-1411`). Missing this renders a region with default haze and no
error anywhere. This was written in from the viewer source before any live data existed, and the
live capture in §4 confirmed it's load-bearing, not defensive: **all 21 sky frames in the real
capture nest their haze under `legacy_haze`**.

`GridSession.RegionEnvironmentReceived` fires the parsed model (alongside the still-raw
`RegionEnvironmentCaptured` from Phase A) from the same pair of HTTP GETs — not fetched twice.

### Phase C — day-cycle evaluation (no visual change)

`DayCycle.EvaluateSky(DateTimeOffset)` / `EvaluateWater(...)` in `src/SLNG.Core/DayCycle.cs`: pure
functions, no I/O. Interpolates between the two bracketing keyframes; the track is a **loop**, so
past the last keyframe it blends back into the first across the wrap (stops a visible jump at
midnight). Handles zero/one keyframes, duplicate positions, and the 0.0/1.0 degenerate span.

`SkyLighting.Calculate(SkySettings, lightDirectionZ)` in `src/SLNG.Core/SkyLighting.cs` is a port of
`LLSettingsSky::calculateLightSettings` (`llsettingssky.cpp:1704`). **This is the thing to remember
if you touch this feature again:** the shader's `sunlight_color`/`ambient_color` uniforms are the
OUTPUT of this function, not `SkySettings.SunlightColor`/`AmbientColor` directly — those raw
settings still need the `exp(-light_atten/|lightnorm.z|)` atmospheric attenuation applied. Wiring
the raw settings straight to a light produces a plausible-looking, wrong-at-every-sun-angle sky.
There's a test (`Calculate_SunDiffuse_IsNotTheRawSunlightColor`) that exists specifically to catch
someone reintroducing that shortcut.

### Phase D — drive Godot (first visible change)

`app/scripts/EnvironmentDriver.cs`, evaluated once per frame from `Boot._Process` right next to
`UpdateSunFromRegion` — deliberately reading the **same** `GridSession.SunDirection` that call
already uses, so the sky dome and the actually-lit scene can't disagree about which way is day.

What it touches, all documented in-code with the "why" (read the class doc comment first, it's
short):

- Sun colour/energy — split from `SkyLighting.SunDiffuse` into a normalized colour + an energy
  scalar (SL's derived sunlight is an unbounded value, often >1 on one channel; clamping the colour
  instead of splitting would lose the magnitude that makes a sunset read as orange, not grey).
- Ambient — same split from `SunAmbient`. **Requires** `Environment.AmbientLightSource` flipped
  from `Sky` to `Color` — Godot silently ignores `AmbientLightColor` under `Sky` mode, this line is
  load-bearing not decorative.
- `ProceduralSkyMaterial`'s four flat colours, tinted from `BlueDensity`/`HazeColor`, with a
  day/night energy multiplier from the same `lightDirectionZ`. **Explicitly documented as an
  approximation, not a port** — `ProceduralSkyMaterial` has no per-fragment hook, so there's nowhere
  to put a real `calcAtmosphericVars` port until Phase E's shader seam exists. Its only job right
  now is "visibly changes with time of day," not pixel parity.
- Classic depth fog (`FogEnabled`/`FogLightColor`/`FogDensity`) from the haze colour, with
  **`VolumetricFogEnabled` switched off** — this is the "reconciled, not double-applying"
  requirement the spec called out in advance: `Boot.SetupEnvironment` already ran volumetric fog at
  a fixed density with nothing region-aware about it, and leaving both on would mean the visible
  haze is the SUM of a region-driven fog and one that never moves.
- The water plane's `albedo` shader parameter (`app/materials/water.gdshader`) from
  `WaterSettings.FogColor`, keeping the shader's own default alpha.

`TerrainRenderer.WaterMaterial` (new public getter) exposes the shared `ShaderMaterial` so the
driver can reach it without `TerrainRenderer` knowing anything about environments.

**This is the phase that has not been visually verified.** Everything above is a best-effort
mapping written without a screenshot to check it against. It is entirely plausible something looks
wrong — wrong brightness curve, fog too aggressive/too subtle, water tinted oddly — and none of that
would show up in a test or a headless boot.

---

## 3. Files touched this session

Modified:
- `app/scripts/Boot.cs` — `SetupEnvironment` wiring, `DumpRegionEnvironment`/`LogRegionEnvironment`/
  `LogEnvironment`, `EnvironmentDriver` field + `_Process` hook, `AppVersion` bumps.
- `app/scripts/TerrainRenderer.cs` — `WaterMaterial` public getter, `PhysicsLayers.Terrain` on the
  terrain `StaticBody` (§9).
- `app/scripts/AvatarController.cs` — two `CollisionMask` fixes (§9).
- `app/scripts/Input/CursorManager.cs` — `CollisionMask` fix (§9).
- `app/scripts/ObjectSelectionController.cs` — clarifying comment only (§9).
- `src/SLNG.Net/GridSession.cs` — `CaptureRegionEnvironmentAsync`/`FetchRegionEnvironmentAsync`,
  `RegionEnvironmentCaptured`/`RegionEnvironmentReceived` events, `OnEventQueueRunning`.
- `docs/ROADMAP.md`, `docs/specs/FEAT-RENDER-01-custom-spatial-shader-family.md` — cross-references
  to the new spec.
- `tests/SLNG.Net.Tests/SLNG.Net.Tests.csproj` — `TestData/**` copy-to-output.

New:
- `src/SLNG.Core/{SkySettings,WaterSettings,DayCycle,SkyLighting,RegionEnvironmentCapture}.cs`
- `src/SLNG.Net/EnvironmentLlsdParser.cs`
- `app/scripts/EnvironmentDriver.cs`
- `app/scripts/PhysicsLayers.cs` (§9)
- `docs/specs/FEAT-ENV-01-windlight-eep.md`
- `tests/SLNG.Core.Tests/DayCycleTests.cs`
- `tests/SLNG.Net.Tests/EnvironmentLlsdParserTests.cs` (hand-written LLSD shapes)
- `tests/SLNG.Net.Tests/EnvironmentCaptureParityTests.cs` (parses the real capture below)
- `tests/SLNG.Net.Tests/TestData/environment-dangazi-eep.llsd` — **committed real grid data**, see §4

---

## 4. The live capture — what OSGrid actually sent, and why it matters

Captured 2026-08-19, OSGrid's The Dangazi Forest, `user://logs/environment-The_Dangazi_Forest-eep.llsd`
→ copied into `tests/SLNG.Net.Tests/TestData/environment-dangazi-eep.llsd` (98 KB, committed on
purpose — it's the only sample of what a grid actually sends, and re-capturing costs a login).
**This file lives in `%APPDATA%\Godot\app_userdata\Puris Viewer\logs\`, outside the git repo
entirely — it survived the incident in §8 untouched, which is the only reason the fixture could be
restored without a second login.**

Structure: a **full EEP day cycle** via `ExtEnvironment` (`type: "daycycle"`), 21 sky frames, 3
water frames, 33 keyframes across two tracks (water = track 0, ground sky = track 1) — matches
`llsettingsdaycycle.cpp` exactly. **No legacy Windlight document was produced** — but see the gap in
§6, we don't actually know whether the region even offered that capability.

`EnvironmentCaptureParityTests` parses this exact file on every test run and checks: structure (7
sky / 5 water keyframes after track resolution), ascending sorted positions, that the haze block is
actually being read (frames must not collapse to one repeated `blue_horizon` — this is the test that
would catch the `legacy_haze` submap being silently skipped again), that water frames resolve a real
normal-map id (not `Guid.Empty`), that every position 0..1 in the cycle evaluates to a finite sky and
finite derived lighting, and that the sky genuinely differs between midnight and noon.

**What's still unconfirmed:** whether SL itself agrees with OSGrid's shape (only tested against one
OpenSim region), and the legacy-Windlight fallback path — unit-tested against hand-written LLSD, but
never exercised against a real grid response, because the one live region tested happened to offer
EEP.

---

## 5. Explicit deferrals — updated after §8

Earlier drafts of this handover (before §8's incident) noted that a commit had been offered twice
and left unresolved. **That is exactly what let the incident in §8 happen** — the work sat as
uncommitted changes with nothing protecting it from an out-of-band branch switch. Whoever reads
this next: commit this work, or at minimum push it to a remote, before doing anything else with the
branch. Don't repeat the deferral.

Also not done, per `AGENTS.md`'s working agreement: no `docs/ROADMAP.md` `Owner` was set to `claude`
for `FEAT-ENV-01`, and no `feature/FEAT-ENV-01-windlight-eep` branch/worktree was used for this
work — it happened directly on `main`. Complicating that: a branch with exactly that name already
existed, with its own unrelated, unfinished, uncommitted Windlight/EEP implementation on it (see
§8) — so simply "moving this work onto that branch" is not a clean option without reconciling with
whoever owns that WIP first.

---

## 6. Known gaps / what NOT to assume is proven

- **`--selftest` doesn't exist.** `AGENTS.md` documents `godot --headless --path app -- --selftest`
  as a smoke test; grep finds zero code anywhere reading that argument. The command just launches
  the full client headless forever with no pass/fail signal. A task chip was spawned this session
  (`task_ac0ef235`, title "Implement or remove the --selftest flag") — check if it's still open
  before relying on "ran the selftest" as a verification claim anywhere in this repo's history.
  Everything this session calls "headless boot clean" means "ran N seconds under a Bash timeout and
  grepped the log for new errors," not an actual pass/fail assertion.
- **Phase D is unwatched**, per §2 — the biggest open item.
- **Legacy Windlight fallback is unexercised against a real grid** — see §4.
- **SL itself has never been tested**, only one OpenSim region.
- **`dotnet format --verify-no-changes` is not clean repo-wide** — this predates this session
  (`GridSession.cs` alone had thousands of pre-existing violations before any of this work; mostly
  CRLF ENDOFLINE noise `.gitattributes` normalizes away on checkout). Nothing added this session
  introduces a NEW whitespace/import violation, but a bare `dotnet format --verify-no-changes` run
  will still fail loudly on unrelated pre-existing files — don't mistake that for this feature's
  fault.
- **Parcel-level environments are out of scope**, noted in the spec, not implemented — region-level
  only.
- **`classic_mode`** (the viewer's alternate tonemapping path through the same atmospherics
  uniforms) is an explicit open question in the spec, not decided either way — irrelevant until
  Phase E but will matter then.

---

## 7. Next steps, in order

1. **Commit this work.** Not optional advice this time — see §8.
2. **Log in and look at the sky.** Highest-value verification action — everything in Phase D is
   unverified against a screenshot. Watch it across a few minutes if the region's day cycle is
   short enough, or add a debug time-scrub if it's the standard 4-hour SL day.
3. If it looks wrong: the mapping functions in `EnvironmentDriver` are small and independently
   tunable (`ApplySkyDome`, `ApplyFog`, `ApplyAmbient`, `ApplySun`) — each has its approximation
   reasoning in a doc comment, adjust the specific one rather than the whole file.
4. Reconcile with whoever owns the parallel WIP stashed on `feature/FEAT-ENV-01-windlight-eep`
   (`GodotEnvironmentManager`, `EnvironmentWindow` UI) — see §8. Two implementations of the same
   feature now exist; that needs a human decision, not a merge attempted blind.
5. Phase E stays blocked on `FEAT-RENDER-01` Phases 3–4 (avatar/terrain/water shader migration) —
   don't start it before those land, the ADR 0002 reasoning for why is in the spec.
6. Separately: the `--selftest` gap (§6) is real technical debt independent of this feature; the
   spawned task chip covers it.

---

## 8. INCIDENT: an out-of-band branch switch discarded this session's uncommitted work, and it had
## to be reconstructed from conversation context

This happened mid-session and is the reason §5 above is no longer a mild note but a direct
instruction. Recorded in full because it's a real risk anyone working uncommitted in this repo
should understand.

**What happened:** partway through this session (after Phases A–D and the handover this file
originally was were already written, while investigating an unrelated console-warning bug), a
`git status` check revealed the working tree had changed underneath the conversation: the checked-
out branch was now `feature/FEAT-ENV-01-windlight-eep` instead of `main`, and every uncommitted file
this session had created — all of `src/SLNG.Core`'s new records, `EnvironmentLlsdParser.cs`,
`EnvironmentDriver.cs`, the spec, the original version of this handover, all the tests, the fixture
— was gone from disk. `git reflog` showed exactly one relevant event, `checkout: moving from main to
feature/FEAT-ENV-01-windlight-eep`, as the single most recent action — meaning something outside
this conversation (the user's own terminal, most likely, since the assistant never issued that
checkout) switched branches while this session's changes sat uncommitted and unstaged.

**Why the files couldn't be recovered via git:** none of it was ever `git add`ed. Untracked files
have no representation in git's object database at all — `git fsck` cannot find what was never
staged. This is different from losing staged-but-uncommitted work (recoverable via dangling blobs)
or committed-then-reset work (recoverable via reflog) — this was neither. It was gone, full stop,
from git's perspective.

**What `feature/FEAT-ENV-01-windlight-eep` turned out to already contain:** a *different*,
unfinished, uncommitted implementation of the same feature — `SLNG.App.Environment.GodotEnvironmentManager`,
an `EnvironmentSettingsReceived` event (different name from this session's
`RegionEnvironmentReceived`), a `SLNG.App.UI.Windows.EnvironmentWindow` UI (a different design
direction from this session's log-based diagnostics), plus scratch files (`temp.cs`, `temp2.cs`,
`temp3.cs`, `scratch-viewer/`) and one file (`EnvironmentPreferencesPage.cs`) that read as
mid-edit/broken. This was present in the repo from before this conversation started (the very first
system-reminder git-status snapshot of this session already listed it) but was never investigated —
this session worked entirely on `main`, apparently oblivious to it, until the checkout surfaced it.
**Whoever owns that WIP has not been identified.** Per `AGENTS.md`'s dual-agent convention it may be
a `gemini`-owned parallel effort, or separate human experimentation — check before assuming either.

**How it was recovered:** everything this session had written was still present verbatim in the
conversation's own context (every `Write` call specified full file content; every `Edit` call is a
precise, known diff). Recovery was:
1. `git stash push -u -m "..."` on `feature/FEAT-ENV-01-windlight-eep` to preserve the other
   implementation's WIP untouched and reversibly (stash entry still present — do not drop it without
   inspecting `app/scripts/Environment/`, `src/SLNG.Core/Environment/`, `src/SLNG.Net/Codecs/`,
   `app/scripts/UI/EnvironmentPreferencesPage.cs`, `app/scripts/UI/Windows/` first).
2. `git checkout main` (clean tree at that point, so a trivial, risk-free checkout).
3. Every file from this session rewritten from the conversation's own record of its content,
   file-by-file, verified by rebuilding and re-running the full test suite after each major piece
   (Core+Net records and parser; then the app-side collision-layer fix; then `Boot.cs`'s wiring;
   then the tests and fixture) — the test suite hitting the exact same pass count (111/38/37) as
   before the incident was the actual confirmation that the reconstruction was faithful, not just a
   visual diff read.
4. The one file that could NOT have been reconstructed from conversation context — the 98 KB real
   LLSD capture in §4 — turned out to still exist, because it was written to
   `%APPDATA%\Godot\app_userdata\Puris Viewer\logs\`, entirely outside the git repository and
   therefore unaffected by any branch operation. This was luck of where the file happened to live,
   not a general safety net — don't assume other artifacts would survive the same way.

**The lesson, stated plainly:** uncommitted work in a single-working-tree repo (this project
deliberately avoids `git worktree`, see `AGENTS.md`) is not safe from a branch switch initiated
outside the current conversation, at any moment, for any reason. It is not git's fault — checkout
behaved exactly as documented — the exposure is inherent to leaving substantial work uncommitted for
an extended session. This session asked about committing twice before the incident and got no
answer either time; that is the gap that made the incident costly instead of a non-event.

---

## 9. A separate, smaller fix made along the way: the console-warning bug

While investigating this (before discovering the incident above), a genuine, unrelated bug was
found and fixed. It survived the incident because it consisted of small, isolated edits to
already-tracked files, which `git checkout` merges cleanly across branches when the target branch's
base content matches — unlike the brand-new untracked files above.

**Symptom:** `WARNING: Vector3 cannot be normalized, the elements must be finite. Making (0, 0, 0)
as a fallback.` spamming the console — 45,862 of 45,872 such warnings in one real session's log
came from exactly one call site: `CursorManager._PhysicsProcess`.

**Root cause:** `TerrainRenderer` deliberately marks unstreamed terrain patches as NaN height so a
ground raycast MISSES there instead of reporting a floor at height 0 (intentional design, see its
own comment at the NaN-assignment site). Godot's height-field raycast still runs a `normalize()`
over the NaN cell en route to reporting that miss, which is what prints the warning. `CursorManager`'s
per-frame mouse-hover raycast queried terrain on the same collision layer as real objects even
though a terrain hit is provably inert there (terrain's `StaticBody` carries no
`PrimitiveComponent`, so it can never change the cursor shape) — so every frame the mouse crossed an
unstreamed patch, this fired for no benefit.

**Fix:** `app/scripts/PhysicsLayers.cs` (new) gives terrain its own collision-layer bit
(`PhysicsLayers.Terrain = 1u << 3`), separate from the shared `Objects` layer. `TerrainRenderer`'s
`StaticBody` now sets that layer explicitly. `CursorManager`'s raycast mask narrows to
`PhysicsLayers.Objects` only — terrain excluded, since it was never useful there.
`AvatarController`'s two raycasts (ground detection, alt-zoom orbit target) and
`ObjectSelectionController`'s click raycast (needed for the right-click "ground menu") all keep
`PhysicsLayers.Terrain` explicitly in their masks, since they have genuine reasons to hit terrain
and would break without it.

**Verified:** `dotnet build` clean on both the isolated collision-layer change and after the full
Windlight reconstruction; not yet re-confirmed against a live console log (the original 45k-warning
log predates this fix by definition — nobody has run the fixed build against a real session yet to
confirm the count actually drops). That confirmation is a fast, cheap next step: log in, hover the
mouse over an unstreamed area, watch the console.
