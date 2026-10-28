# [BUG-PERF-22] A freeze with the separate render thread switches the viewer back to the main thread

- **Feature ID:** `BUG-PERF-22`
- **Track:** `render` / `ui`
- **Status:** ⏸️ Pending
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md)
- **Branch:** `fix/BUG-PERF-22-render-thread-fallback`

## Overview & Goal
The experimental separate render thread (FEAT-PERF-13, Graphics › Performance › Advanced › Render thread =
"Separate thread") has frozen the client twice on an RTX 4070 (2026-10-26 v0.28.4, 2026-10-28 v0.28.45, both in a busy
Second Life region). Both times, read with a non-invasive debugger attach:
- the main thread waits in `SleepConditionVariableSRW` under Godot's main loop (RenderingServer sync);
- `WorkerThread 0` (the render thread) waits in `vulkan_1` -> `nvoglv64` -> `WaitForMultipleObjects`, inside the NVIDIA
  driver;
- 0 % CPU, the window "not responding", no driver reset (no nvlddmkm/Display 4101 event in the System log).
- V-Sync was OFF (the maintainer always runs without it), so "present waits for V-Sync" does not explain it.

The process never recovers and has to be killed, and because the choice is saved, the next start uses the separate
thread again. The cause is unknown (no Godot symbols); this task does not fix it. It makes the freeze **harmless**:
after one freeze the viewer starts on the main thread and says why.

## Behaviour
1. **Detect (watchdog thread).** `MainThreadWatchdog` already notices a stale heartbeat from a background thread. When
   **all** of these hold, it applies the fallback once per run:
   - the render thread is "separate" in this run **and** that is the saved choice (`RenderThread.SavedPreferenceIsSeparate()`,
     read on the main thread at startup and cached; a one-off `--render-thread separate` is not saved, so it is only logged);
   - the main thread has been stalled for at least `FallbackStallSeconds` = 10 s (a named constant; the normal stall
     report stays at 2 s);
   - not `--selftest` / headless.
2. **Fallback (still on the watchdog thread, no Godot API).** The main thread is frozen, so nothing may be marshalled
   to it. Resolve both absolute paths at startup on the main thread (`ProjectSettings.GlobalizePath`) and write with
   `System.IO` only:
   - `engine_overrides.cfg` (`RenderThread.OverridePath`): remove the `driver/threads/thread_model` line, exactly what
     `RenderThread.SavePreference(false)` does; if no other key is left, delete the file. Write to a temp file and
     move it over, so a kill mid-write cannot leave a broken file.
   - a marker `user://render_thread_fallback.json` with the time and the stall length.
   - one plain console line (it is a real failure, not `--diag` material), e.g.
     `[RenderThread] main thread stalled 10 s with the separate render thread -- switched the saved choice back to the main thread for the next start`.
   The viewer is not killed; the person ends it, and the next start is on the main thread.
3. **Next start (main thread, Boot).** If the marker exists: show a notice (toast or the notifications window, both
   languages), then delete the marker. Suggested text:
   - de: „Der Viewer ist mit dem eigenen Render-Thread hängen geblieben und nutzt jetzt wieder den Haupt-Thread. Sie
     können ihn unter Einstellungen › Grafik › Leistung › Erweitert wieder einschalten.“
   - en: "The viewer froze with the separate render thread and is back on the main thread. You can switch it on again
     under Preferences › Graphics › Performance › Advanced."
   The Render thread row then simply shows "Main thread (default)"; nothing else changes.
4. If the stall ends by itself after the fallback was written, leave the fallback in place (the person can switch back)
   and log that the main thread recovered.

## Rules to follow
- `AGENTS.md`, `.claude/skills/app-rules/SKILL.md` (incl. "Preferences follow the plan": no new tab or section; a
  text change on the Render thread row only through its existing keys), `.claude/skills/slng-verify/SKILL.md`.
- Background thread: no Godot calls, no `Callable.From(...).CallDeferred()` (crashes off the main thread). File I/O only.
- New texts in both `app/i18n/en-US.json` and `de-DE.json`.
- Bump `AppVersion` (patch), commit `fix(render): [BUG-PERF-22] … (vX.Y.Z-alpha)`.

## Acceptance Criteria
- [ ] With "Separate thread" saved, a main-thread stall of 10 s rewrites `engine_overrides.cfg` to the main thread and
      writes the marker, once; with "Main thread" saved, or with only `--render-thread separate`, nothing is written.
- [ ] The next start runs on the main thread (`[Boot] render thread: main …`), shows the notice once, deletes the marker.
- [ ] A kill during the write never leaves a broken `engine_overrides.cfg` (temp file + move).
- [ ] Other keys in `engine_overrides.cfg`, if any, survive.
- [ ] `/slng-verify` clean.

## Tests
- Selftest: run the fallback against temp paths (inject the stall instead of freezing the real main thread, e.g. a
  watchdog instance with a fake clock/heartbeat): file rewritten, marker written, second trigger writes nothing; a
  start-up check on that marker shows the notice and deletes it. Restore everything; "user data untouched" must pass.
- The file rewrite (remove one key, delete when empty) is pure string work: a unit test if it lives in `src/`.

## Technical Specs & Affected Files
- `app/scripts/MainThreadWatchdog.cs`: the 10 s fallback trigger.
- `app/scripts/RenderThread.cs`: an off-main-thread `System.IO` variant of `SavePreference(false)` taking absolute paths.
- `app/scripts/Boot.cs`: resolve paths at startup; marker check + notice after the HUD is up.
- `app/i18n/en-US.json`, `app/i18n/de-DE.json`; `app/scripts/SelfTest*.cs`.
- `docs/BENUTZERHANDBUCH.md`: one sentence in the Render-Thread paragraph.

## Out of scope / later
- The cause (research): build Godot 4.7 with symbols to name the Vulkan call; report upstream with the stacks.
- Hiding the option on NVIDIA.
