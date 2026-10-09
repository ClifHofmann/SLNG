# [FEAT-PERF-13] Separate render thread (A/B setting)

- **Feature ID:** `FEAT-PERF-13`
- **Track:** `render` | `perf`
- **Status:** `🧪 Review` (built and verified headless and in a window at the login screen; waiting for the in-world A/B below)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md), plan in [MVP6-1-performance-program.md](MVP6-1-performance-program.md)
- **Shipped in:** `v0.27.29-alpha`, **off by default**

## Overview & Goal

At the 58-avatar baseline (Sirens Beach, v0.27.28) the frame runs serially on one thread: our C#
~30 ms, Godot's flush ~13 ms, render submission ~45 ms = ~90 ms. The GPU needs only ~18 ms. Godot
can run its `RenderingServer` on a second thread, so the frame becomes roughly
`max(main side, render side)` instead of their sum. Expected: ~90 ms toward ~50–60 ms at the same
spot. The engine marks the mode experimental, so this task ships it as an **A/B setting that is off**
and changes the default only after the A/B below.

## What Godot 4.7 does (read from the 4.7-stable source)

| Fact | Source |
|---|---|
| `rendering/driver/threads/thread_model` is `1` Safe (default) or `2` Separate. "Unsafe" is gone. The engine flag is `--render-thread safe\|separate`. The editor and project manager always force Safe. | `main/main.cpp:2770-2781`, `1563-1587`; `core/config/project_settings.cpp:1775-1777` |
| The engine marks it experimental: "known bugs which can lead to crashing, especially when using particles or resizing the window". It prints a warning at startup. | `doc/classes/ProjectSettings.xml` (`thread_model`), `main/main.cpp:3513-3517` |
| Separate mode pumps the server from a high-priority `WorkerThreadPool` task (`"Rendering Server pump task"`). | `servers/rendering/rendering_server_default.cpp:277-287` |
| The main thread only **queues** commands. `Main::iteration` calls `RenderingServer::sync()` (wait until the render thread has drained everything queued so far, including the previous `_draw`) and then `draw()`, which queues the next `_draw`. So the main thread runs at most one frame ahead. | `main/main.cpp:5071-5075`; `rendering_server_default.cpp:435-451` |
| The command queue is an unbounded, mutex-guarded buffer. Pushing never blocks except for a **synchronous** command. (The old fixed-size `command_queue/multithreading_queue_size_kb` limit no longer exists.) | `core/templates/command_queue_mt.h:84-160` |
| **Setters are asynchronous, getters are round trips.** A `FUNC*RC` / `FUNC*S` call from any thread but the render thread is queued and the caller sleeps until the render thread executes it. The render thread reaches it only **after the frame it is drawing**, so such a call costs up to a whole draw (~45 ms here). | `servers/rendering/rendering_server_default.h:117-128`, the `FUNC*RC` list |
| `frame_pre_draw` is emitted on the main thread by `draw()`. `frame_post_draw` and `request_frame_drawn_callback` are `call_deferred` from the render thread and arrive at the main thread's next message-queue flush, i.e. **out of step with the frame**. | `rendering_server_default.cpp:443-446`, `131` |
| `get_rendering_info` (so every `Performance` render monitor) and `get_frame_setup_time_cpu` are plain reads, **not** round trips. | `rendering_server_default.cpp:232`, `307-326` |
| `RenderingServer.CallOnRenderThread(callable)` runs the callable inline on the main thread in Safe mode and on the render thread in Separate mode. | `rendering_server_default.h:1216-1224` |
| Setting a project setting from a file the client writes: `application/config/project_settings_override` is merged after `project.godot` and `override.cfg`, before `thread_model` is read. A missing file is ignored. | `core/config/project_settings.cpp:872-880` |

**Synchronous (round-trip) RenderingServer API, the ones that matter here:** `texture_2d_get`
(`ImageTexture.GetImage()`, `Texture2D.GetImage()` of a viewport), `mesh_get_surface`
(`ArrayMesh.SurfaceGetArrays`, `SurfaceGetBlendShapeArrays`, and through them `CreateTrimeshShape`,
`GetFaces`, `GenerateTriangleMesh`, `CreateOutline`), `viewport_get_measured_render_time_cpu/gpu`,
`viewport_get_render_info`, `get_shader_parameter_list` (`Shader.GetShaderUniformList`),
`multimesh_get_*` / `multimesh_instance_get_*`, `instance_geometry_get_shader_parameter`,
`global_shader_parameter_get`, `get_video_adapter_name`, and the `Viewport` constructor
(`viewport_get_texture`). **Not** round trips: `ArrayMesh.GetSurfaceCount/SurfaceGetMaterial/
SurfaceGetPrimitiveType/GetAabb`, `ShaderMaterial.GetShaderParameter` (a CPU cache), `Image` /
`GetData`, `Performance.GetMonitor`.

**What changes for code that works in Safe mode:** (1) a main-thread getter now waits for the render
thread; (2) a main-thread setter no longer takes effect immediately, so anything that **changes a
buffer after handing it over** races with the render thread; (3) `frame_post_draw` is late;
(4) an engine error raised by a setter appears on the render thread, without our C# stack.
**Off the main thread nothing changes:** Safe mode already queues those calls, they just go to the
render thread instead of waiting for the main thread's next flush.

## Audit of `app/scripts`

`RenderingServer.*` in the client is small: every setter is asynchronous and fine. Verdicts:

| Where | What | Verdict |
|---|---|---|
| `RenderTimes.cs:109-110` (was per frame) | `ViewportGetMeasuredRenderTimeCpu/Gpu` per tracked viewport | **Broke it, fixed.** A per-frame round trip would make the main thread wait out every draw, i.e. measure itself. Now read on `RenderTimeSampler`'s own thread (`RenderTimes.cs:146-147`) when separate; unchanged inline in Safe mode. |
| `RenderTimes.cs:264` | `ViewportGetRenderInfo` x3 per window | **Fixed**, same sampler thread, one window stale. |
| `RenderTimes.cs:96`, `77` | `GetFrameSetupTimeCpu`, `ViewportSetMeasureRenderTime` | Fine (plain read, setter). |
| `FrameTimeline.cs` | `frame_pre/post_draw` stopwatch cuts | **Broke it, fixed.** Post-draw arrives late, the ordering check dropped most frames. In separate mode a frame is closed by its own `frame_pre_draw`; see "Reading the numbers". |
| `ObjectInstanceGroups.cs:461` | `Shader.GetShaderUniformList()` once per `MaterialFingerprint.Of` (every instancing candidate) | **Would have hurt badly, fixed.** A synchronous server call that also builds a Godot `Array` of `Dictionary`s per call. Uniform names are now cached per `Shader`. Helps Safe mode too. |
| `UI/RadarCanvas.cs:469-477` | `_objectImage.SetData(...)` then `ImageTexture.Update` on the **same** `Image` | **Data race, fixed.** `Update` passes the `Image` by reference; the render thread may not have read it when the next rebuild rewrites it. Now a new `Image` per rebuild. |
| `ObjectRenderer.cs:5412` (`SplitSortedSurfaces`), `AvatarRenderer.cs:3727` (same name) | `mesh.SurfaceGetArrays(i)` on the main thread, once per mesh with >= 2 blended surfaces | Stall (a round trip plus the GPU buffer read it already cost). **Left**, rare and cached per mesh. Follow-up: keep the CPU arrays of such meshes. |
| `AvatarRenderer.cs:2929` (`AddRiggedPickBody`) | `SurfaceGetArrays` per surface, control-avatar (animesh) path only | Stall per animesh, once. Left. |
| `AvatarRenderer.cs:3301` | `mesh.CreateTrimeshShape()` fallback when the worker did not supply faces | Fallback only. Left. |
| `SelectionOutline.cs:96` (`BuildOutlineHull`) | `SurfaceGetArrays` per surface when something is selected | One stall per selection. Left. |
| `AvatarRenderer.cs:4238`, `ObjectRenderer.cs:4382`, `GpuCache.cs:1550` | `ImageTexture.GetImage()` fallbacks (alpha stats not cached; read-back shrink) | Already "meant never to happen", counted in `[WorkCost]`/`[FaceAlpha]`. Each is now one round trip. `GpuCache.ShrinkOne` is capped at 2 per frame. Left. |
| `ObjectRenderer.cs:1725` | `GetImage()` in the material dump | Diagnostic only. |
| `Boot.cs:3499`, `Input/CursorManager.cs:93` | `SubViewport` texture `GetImage()` for the cursors, after two process frames | Once at startup. Correct: the round trip waits for the queued draws, so the image is the rendered one. |
| `Boot.cs:5658`, `UI/SnapshotWindow.cs:719` | `GetViewport().GetTexture().GetImage()` after `ProcessFrame` / `FramePostDraw` | One round trip per user action. Correct: the getter is ordered after the draw that hid the UI. |
| `AvatarRenderer.cs:4541`, `MirrorReflection.cs:59`, `Boot.cs:3472`, `Input/CursorManager.cs:63` | `new SubViewport` (the `Viewport` constructor calls `viewport_get_texture`) | One round trip each, at creation. Fine unless a viewport is created per frame (none is). |
| `UI/AboutWindow.cs:140` | `RenderingServer.GetVideoAdapterName()` | Once when the window opens. |
| `EnvironmentDriver.cs` (28 sites), `AvatarController.cs:956`, `MaterialLab.cs:38` | `GlobalShaderParameterSet` every frame | Asynchronous setters. Fine. `GlobalShaderParameterGet` is not used anywhere. Note: a bad parameter name now errors on the render thread, with no C# stack (the "full C# backtrace" the comment at `AvatarController.cs:945` mentions is a Safe-mode property). |
| `DepthOfFieldController.cs:291-292`, `UI/GraphicsSettings.cs:510` | Setters | Fine. |
| `StatsOverlay.cs:429-431`, `RenderBaselineSampler.cs:85-88` | `Performance.GetMonitor(Render*)` | Plain reads, never round trips. |
| `Boot.cs:544`, `867` | `FramePostDraw` one-shot (window title) | Arrives on the main thread, later than Safe mode. Fine. |
| `GpuCache.cs:1134,574,1510,1563`, `MapTileTextures.cs:129`, `ObjectRenderer.cs:3834` | `ImageTexture.CreateFromImage` / `SetImage` | Main-thread half, each with a **fresh** `Image` that is not touched afterwards (`Dispose()` after the hand-over only drops our reference). Fine. The upload itself now runs on the render thread (cheaper on main, see "Risks"). |
| Worker code that builds engine objects: `ObjectRenderer.MeshPrepare.cs`, `AvatarRenderer.AttachWorker.cs`, `RigWorker.cs` (arrays only); `AvatarRenderer.cs:3562,3785,3936` (a `ShaderMaterial` built in a thread-pool continuation) | RenderingServer setters from non-main threads | Unchanged from Safe mode, which already queues them. No `Callable.From` is created off the main thread. |
| `RenderThread.cs` `Probe()` | One `CallOnRenderThread(Callable.From(...))` at startup | The only managed code the client runs on the render thread: a field write and an event, no Godot API. See the next section. |

## Choosing the mode (no `project.godot` editing)

1. **One run, no setting:** start the viewer with the engine's own flag. It wins over everything.

   ```
   "Puris Viewer.exe" --render-thread separate
   "Puris Viewer.exe" --render-thread safe        (force the default for one run)
   ```
   From the source tree: `godot --path app --render-thread separate`.
2. **Saved choice:** *Preferences > Graphics > Hardware > Render thread* ("Main thread (default)" /
   "Separate thread (experimental)"). It writes `user://engine_overrides.cfg`
   (`%APPDATA%\Godot\app_userdata\Puris Viewer\engine_overrides.cfg`), which
   `application/config/project_settings_override` in `project.godot` makes the engine merge in at
   startup. The page says in amber "Restart the viewer to apply this change." until the restart.
   Choosing "Main thread" **deletes** the file. It is not part of any graphics preset.

The client finds out what it is actually running with `RenderThread.Probe()`: one callable sent
through `RenderingServer.CallOnRenderThread`, comparing the thread it ran on with the main thread.
(The engine flag is consumed by the engine and is not in `OS.GetCmdlineArgs()`, and the project
setting does not see it.) `godot.log` gets `[Boot] render thread: separate (command line
(--render-thread))` / `(saved choice (...))` / `main (project default)`, and every `[Perf]` and
`[RenderBaseline]` line ends in `rt=separate|main`.

## How to A/B it in-world (for the person testing)

Same machine, same account, same settings, **nothing changed between the two runs**.

1. **Spot.** Sirens Beach on Agni, the 58-avatar spot of the baseline. Stand still at the same
   position and heading, camera not moving.
2. **Run A (default).** Start the viewer normally (no flag, Render thread = Main). Log in, go to the
   spot, wait until `queue=0` in the stats overlay (textures settled), then **stand 2 minutes**.
3. **Run B (separate).** Either start with `--render-thread separate`, or set Render thread =
   "Separate thread" on the Graphics page and restart. Check `godot.log` for
   `[Boot] render thread: separate ...`. Same spot, same wait, same 2 minutes.
4. Read `%APPDATA%\Godot\app_userdata\Puris Viewer\logs\slng-perf.log`. Take the `[Perf]` lines of the
   2 minutes (one every few seconds) and compare the **median of each field** between A and B:

| Field | Meaning in A (main) | Meaning in B (separate) | Good result |
|---|---|---|---|
| `fps`, `meanMs` | frame rate and mean frame period | same | fps clearly up, meanMs clearly down. This is the result. |
| `low1%`, `p99Ms`, `worstMs`, `hitches` | spikes | same | not worse (Godot warns of "a bit more jitter" from the extra synchronisation) |
| `processMs` | the main loop's work, including the draw | includes the **wait for the render thread** | falls |
| `scriptsMs` | our `_Process` code (~30 ms) | same code, now sharing memory bandwidth with the render thread | about the same (a small rise is normal) |
| `postFlushMs` | Godot's flush before the draw (~13 ms) | the flush **plus the wait for the render thread** (the engine's `sync()` is the last thing before the draw is queued) | **The tell:** if it stays near 13 the main side is the slower one; if it jumps, the render thread is slower, and the excess is the time the main thread sat waiting. |
| `drawMs` | the whole render submission on the main thread (~45 ms) | **0.0 by construction**, the draw is only queued | (not comparable; see `renderCpuMs`) |
| `renderCpuMs`, `setupMs`, `renderGpuMs` | Godot's measured render cost per viewport | the same measurement, read on a helper thread | `renderCpuMs + setupMs` is now the render side's length; `renderGpuMs` should not change |
| `vramMB`, `draws`, `tris` | | | unchanged (same scene) |
| `queue`, `queuePeak` | main-thread work backlog | same | drains to 0 at least as fast |

   Reading B: the frame is about `max(main side, render side)`. Main side is roughly
   `preFlushMs + scriptsMs + (postFlushMs minus its wait) + otherMs`; render side is
   `renderCpuMs + setupMs` plus the time to execute the queued commands. If `postFlushMs` grew, the
   next lever is **fewer draws** (FEAT-PERF-14, 15, 20, 21); if it did not, it is **less C#**
   (FEAT-PERF-16, 18).
5. **Stability, after the measurement** (these are the places the engine itself names): teleport away
   and back once; open and close the snapshot window and take a snapshot; stay 10 minutes in a busy
   place with particles; then resize the window, toggle fullscreen and change V-Sync **last**,
   because the engine lists window resizing as a known crash trigger in this mode. Note anything that
   crashes or looks different (text, particles, mirrors).

**Decision rule** for making it the default (a later one-line change of `thread_model` in
`project.godot`, keeping the Graphics page as the opt-out): fps gain of at least 25 % at the baseline
spot, `p99Ms` no more than 10 % worse, and no crash in a 30 minute session.

## Verification done on 2026-10-09

- `dotnet build SLNG.sln` and `dotnet build app/SLNG.App.csproj`: 0 warnings, 0 errors.
  `dotnet test`: 211 + 1568 + 1224 pass. `dotnet format SLNG.sln --verify-no-changes`: clean.
  `python tools/check_shader_globals.py`: consistent.
- Headless `--selftest`: **123/123 in the default mode and 123/123 with `--render-thread separate`**,
  including the new `render thread` check (mode probe, saved-choice round trip, a sync getter from the
  main thread and from the sampler thread, the sampler's drop limit, material fingerprints). The only
  difference in the engine output is the experimental warning. With `engine_overrides.cfg` holding
  `thread_model=2` the run reports `separate (saved choice ...)`, which proves the override file is
  read; the file was deleted afterwards.
- Windowed login screen on the RTX 4070, 500 frames each: **separate runs and exits 0**, same wall
  time as Safe. Separate logs two engine messages that Safe does not: one
  `ERROR ... _texture_2d_update ... p_image->is_empty()` at startup (not from client code: the client
  has no `ImageTexture.Update` at the login screen; most likely the engine's font atlas), and one
  `ERROR ... RenderingDevice::finalize can only be called from the render thread` at exit. Neither was
  visible as a problem; both are engine-side symptoms of the "experimental" label.

**Not verified:** a live session in-world (no login in this task). That is the A/B above.

## Risks and known unknowns

- **Experimental engine feature.** Crash risk is named for particles and window resizing; look at
  the login screen and the first minutes for missing text (the startup `_texture_2d_update` error).
- **Uploads move to the render thread.** `ImageTexture.CreateFromImage` and mesh commits cost the main
  thread less, so `MainThreadWorkQueue`'s time budget lets more of them through per frame, and a texture
  burst now lengthens the render side. Watch `queue` and `p99Ms` while a region loads, not only at the
  standing spot. If it stutters, lower the Visual/Refine budgets in separate mode (follow-up).
- **Diagnostics change.** RenderingServer setter errors appear on the render thread, so
  `[EngineWarnings]` shows "worker thread, no C# frame" for them.
- **Left as round trips**, listed in the audit (`SurfaceGetArrays` readers, `GetImage` fallbacks).
  Each is a one-off and shows up as a hitch, not as a per-frame cost.

## Acceptance Criteria

- [x] `thread_model` selectable without editing `project.godot` by hand: engine flag and a Graphics
  setting that says "restart"
- [x] Default unchanged (Safe)
- [x] Off-main-thread / main-thread RenderingServer use audited; what breaks is fixed
- [x] `[Perf]` fields stay meaningful in both modes, and say which mode they are from (`rt=`)
- [x] Build, tests, format, shader globals, headless `--selftest` in both modes
- [ ] In-world A/B at the baseline spot (above), result recorded here and in `docs/perf/`
- [ ] Default switched to Separate if the decision rule holds

## Technical Specs & Affected Files

- `app/scripts/RenderThread.cs` (new): mode probe, saved choice, engine override file
- `app/scripts/RenderTimeSampler.cs` (new): helper thread for the render-time round trips
- `app/scripts/RenderTimes.cs`, `FrameTimeline.cs`: separate-mode sampling and frame accounting
- `app/scripts/ObjectInstanceGroups.cs`: uniform-name cache; `app/scripts/UI/RadarCanvas.cs`: fresh `Image` per update
- `app/scripts/UI/GraphicsPreferencesPage.cs`, `app/i18n/{en-US,de-DE}.json`: the setting
- `app/project.godot`: `application/config/project_settings_override`
- `app/scripts/Boot.cs`, `UI/StatsOverlay.cs`, `RenderBaselineSampler.cs`: boot line, `rt=` tag, sampler shutdown
- `app/scripts/SelfTest.RenderThread.cs` (new), `SelfTest.cs`

## Sub-tasks / Progress

- [x] Research and audit
- [x] Fixes and the setting
- [x] Verification (headless, both modes; windowed login)
- [ ] In-world A/B (the person testing)
- [ ] Follow-up: keep CPU arrays for `SplitSortedSurfaces` so it stops reading meshes back
- [ ] Follow-up: separate-mode budgets for the texture/mesh upload lanes, from the A/B
