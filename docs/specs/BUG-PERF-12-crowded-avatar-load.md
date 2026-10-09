# [BUG-PERF-12] Arriving among many avatars: rig load and the frame rate that stays low afterwards

- **Feature ID:** `BUG-PERF-12`
- **Track:** `render`
- **Status:** `🧪 Review` -- measures 1-2 merged (v0.27.17 with BUG-AVATAR-10); rest-fps target met in-world, load and per-second targets not (see In-world measurement)
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md) -- follows BUG-PERF-05, BUG-PERF-06, BUG-PERF-08 and BUG-PERF-11

## Overview & Goal

After BUG-PERF-10/11 the object side of a cached region load is cheap (collision shapes, cull sweep, region
unload, work queue). What is left when the destination is crowded with avatars is **their worn rigged meshes**:

Measured 2026-10-09, v0.27.12, teleport at 10:46:57 into a place with 16 avatars (`slng-perf.log`; Godot writes
UTF-8 with decimal commas -- read it with a script, the rtk shell hook truncates `grep` output):

| window | fps | queue peak | `avatar.rig` | `avatar-render` ms/s |
|---|---|---|---|---|
| 10:46:57 | 31 | 15.4k | -- | 13.9 |
| 10:47:03 | 26 | 13.6k | n=1573, 994 ms main thread | 28.3 |
| 10:47:08 | 17 | 15.1k | n=830, 408 ms | 34.3 |
| 10:47:13-23 | 19 / 20 / 17 | 8.4k -> 0.1k | n=35-50 | 35-42 |
| 10:47:28-38 (queue empty) | **29 / 29 / 29** | 0 | -- | **66-69** |

`[AvatarCost]` at 10:47:38: `avatars=16 skinnedMeshes=2902 drawnSkinnedMeshes=1068 surfaces=5612 hiddenSurfaces=4314
skinBinds=162563 boundSkinBinds=55569 controlAvatars=5`; `[Perf]`: `drawMs=18.8 draws=10519 drawsShadow=6179 vramMB=8089`,
frame 34 ms with the queue empty. Two earlier runs show the same shape: Millenium (7 avatars, 1606 skinned meshes,
734 rigs in one window) and the Secret Love login (10 avatars, 1385 skinned meshes, 1311 rigs).

Two problems, one cause (a lot of skinned geometry that is mostly not visible):

1. **Load:** ~25 s at 17-20 fps while the rigs are built (worker `avatar.rig.prepare` 5-10 ms avg, of which tangents
   2.7-8.5 ms; main thread `avatar.rig` 0.6-0.9 ms + commit 0.33 + `avatar.split_sorted` 0.28 per rig).
2. **Rest:** 29 fps with an empty queue; `cull`, `workqueue` and `coll-urgent` are all small then, the frame is the
   per-frame posing and drawing of 16 animated skeletons carrying 2,902 skinned meshes.

## Findings so far (read these before changing anything)

- 4,314 of 5,612 surfaces draw nothing: `IsHiddenFaceMaterial` -- a face whose colour alpha is 0 or whose texture is
  the SL "transparent" id gets `PrimShaderFamily.Hidden` (`BuildFaceMaterialAsync`, `AvatarRenderer.cs`). Mesh bodies
  do this on purpose for unused onion layers and auto-alpha cut segments. BUG-PERF-05 already hides an instance whose
  EVERY face is hidden (`UpdateWornMeshVisibility`, the comment above it explains the skin/skeleton detach). What is
  left is the instance with SOME hidden faces: still skinned, still drawn in every pass, one draw call per hidden surface.
- Godot has no per-surface visibility; a hidden surface is still a draw call in the depth prepass, the colour pass and
  each shadow cascade (`drawsShadow=6179`).
- The rig is prepared on a worker (`AvatarRenderer.RigWorker.cs`, `SLNG.Assets.RiggedMeshBuilder`); tangents are
  Godot's MikkTSpace (`GenerateTangents`, `RigWorker.cs` ~line 306). They are only needed for normal-mapped faces.
- The pump already budgets the main-thread part (`MainThreadWorkQueue`, up to 9 ms a frame while the queue is deep);
  the cost is the amount of work, not one slow item.

## Acceptance Criteria

Measured in-world at the same kind of place (a crowded one, >= 10 avatars), one warm start, `--diag`:

- [ ] `[WorkCost] avatar.rig` main-thread total per 5 s window at least halved (n x avg), or no window above ~500 ms.
- [ ] `[PhaseCost] avatar-render` at rest <= 30 ms/s (was 66-69) and fps at rest >= 40 (was 29) with the queue empty.
- [ ] fps during the load >= 30 in every window (was 17-20).
- [x] Visually identical to v0.27.12: BoM mesh bodies/heads, alpha layers, hair, clothing; toggling a layer with the
      body HUD still shows and hides it; own avatar and others' attachments complete; no avatar left in a T-pose.
- [ ] Unit / self test for every new rule; `/slng-verify` steps all green.

## Technical Specs & Affected Files

Candidate measures, in the order worth trying. Measure each against the table above before keeping it; drop the ones
that do not move a number.

1. **Do not build what is known to be invisible.** The face records are known when the rig is requested
   (`ApplyAttachmentMeshDataAsync` -> `RequestRig`, `AvatarRenderer.cs` ~2845): a face with colour alpha <= 0.001 or the
   transparent texture id never draws. Leave those submeshes out of the arrays the worker builds (smaller skin, fewer
   surfaces, fewer draw calls, less tangent work). A face that later becomes visible (`ApplyFaceMaterialsAsync` after a
   TextureEntry change) must trigger a rebuild of that rig -- the existing re-request path. Risk: surface/face index
   mapping (`faceIndices`, `RunStart`, the BUG-RENDER-16 surface merge) -- keep the SL face number per surface exact.
2. **Order and distance.** Prepare rigs nearest avatar first and let a far avatar's attachments wait (`_pendingRigs`,
   `RequestRig` in `AvatarRenderer.RigWorker.cs`; distance from the camera). The load then shows the people in front
   of you first and the per-frame budget is spent where it is seen.
3. **Tangents only where a normal map exists.** Check how many rigs have a normal-mapped face at all (add a counter
   first). If few, compute tangents lazily when a normal map is applied; if the arrays have to be final at commit,
   decide by the material info available at request time.
4. **Posing cost at rest.** 16 animated skeletons: animate far or off-screen avatars at a reduced rate
   (`AvatarAnimationPlayer`, `AvatarRenderer._Process`), or skip skeleton updates for avatars outside the view frustum.
   Keep the self avatar and anything within a few metres at full rate.

Files: `app/scripts/AvatarRenderer.cs`, `AvatarRenderer.RigWorker.cs`, `AvatarRenderer.AttachWorker.cs`,
`AvatarRenderer.ControlAvatar*.cs`, `src/SLNG.Assets/RiggedMeshBuilder.cs`, self tests in `app/scripts/SelfTest.MeshPrepare.cs`
and `tests/SLNG.Assets.Tests`.

## Rules for the implementer

- Read `AGENTS.md`, `.claude/skills/app-rules/SKILL.md` and the BUG-PERF-05/06/08/11 rows in `docs/ROADMAP.md` first.
  Work on branch `fix/BUG-PERF-12-crowded-avatar-load` in `E:/Git/SLNG` (no `git worktree`), claim the row, bump
  `AppVersion` (`app/scripts/Boot.cs`) by a patch per fix, commit as `perf(render): [BUG-PERF-12] ... (vX.Y.Z-alpha)`.
- Nothing from `src/` may reference Godot; decode and mesh preparation stay on workers, the engine calls on the main thread.
- Verification: `dotnet build SLNG.sln` AND `dotnet build app/SLNG.App.csproj`, `dotnet test`, `dotnet format SLNG.sln
  --verify-no-changes`, `python tools/check_shader_globals.py`, `godot --headless --path app --log-file
  user://logs/selftest/godot.log -- --selftest`, then revert any change in `app/project.godot`.
- Files must stay LF (a Python script that rewrites a file on Windows writes CRLF; open with `newline=''`).
- A headless benchmark is possible without a login: a temporary env-var hook in `SelfTest.Run`, deleted afterwards; a
  `git archive HEAD` copy under the scratchpad gives the "before" number from the same bench.
- No push and no PR unless the owner asks.

## Sub-tasks / Progress

- [x] Counters first: rigs with a normal map, faces hidden at request time, per-avatar skinned mesh count (`[AvatarCost]`)
- [x] Measure 1 (skip invisible submeshes)
- [x] Measure 2 (nearest avatar first)
- [ ] Measure 3 (tangents) -- only if the counter says it pays
- [ ] Measure 4 (pose rate) -- only if rest fps is still below 40
- [x] In-world check by the owner, numbers back into the ROADMAP row

## In-world measurement (2026-10-09)

Same region (handle 499178279373312), same crowd, camera still; baseline = v0.27.15 (main + BUG-AVATAR-10, without this task), run = v0.27.16.

| | baseline | v0.27.16 | target |
|---|---|---|---|
| avatars / skinned meshes | 17 / 3,841 | 15-16 / 1,926-2,019 | - |
| fps during load | 16-22 | 24-28 | >= 30 (not met) |
| fps at rest (low1%) | ~24 (7-20) | 42-47 (23-37) | >= 40 (met) |
| avatar-render at rest | 60-70 ms/s (~2.7 ms/frame) | 80-97 ms/s (~1.9 ms/frame) | <= 30 ms/s (not met; restate per frame) |
| worst 5 s window of avatar.rig | 687 ms | 1,286 ms (arrival), 706 ms (zoom) | <= 500 ms (not met) |
| rigs per 5 s at rest | 20-100 | 1-3 | - |

Visual check by the owner: every avatar complete, nothing missing, no T-pose.

Still open: the rig work arrives as one burst (2,300+ rigs in a 5 s window on arrival; ~950 again when a zoom changes the attachment detail level), which is what keeps load fps under 30. Measures 3 (tangents) and 4 (pose rate) were not needed for the rest target.
