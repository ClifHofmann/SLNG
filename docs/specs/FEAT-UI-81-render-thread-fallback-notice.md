# [FEAT-UI-81] After a render-thread freeze, keep saying so and offer the way back

- **Feature ID:** `FEAT-UI-81`
- **Track:** `ui` / `render`
- **Status:** 🧪 Review
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md), follows BUG-PERF-22, plan table in [FEAT-UI-79](FEAT-UI-79-preferences-rework.md)
- **Branch:** `feature/FEAT-UI-81-render-thread-fallback-notice`

## Overview & Goal

BUG-PERF-22 makes a freeze with the separate render thread harmless: the next start runs on the main thread and says so
once, in an 8 s toast. On 2026-10-29 that cost a day. The toast went by during a long load, the render-thread row sits
under Advanced, and nothing said afterwards why the client ran at 14 fps instead of 27 at Sirens (same scene, same
camera). Cache, a leak, VRAM and a mirror were all chased first.

## Design

- `RenderThreadFallback.TryConsumeMarker(path, out marker, keepCopyPath)` writes the marker to a second file before it
  deletes it; `ReadKept` and `ClearKept` read and remove that copy (`user://render_thread_fallback_last.json`).
  xUnit: `RenderThreadFallbackKeptTests` (5 cases).
- The start keeps the copy and its toast lasts 20 s.
- Graphics preferences, Performance section, **outside Advanced**: while a kept copy exists and the saved choice is not
  the separate thread, a notice reads "The separate render thread was switched off: the viewer froze on {when} for {s} s."
  with a button "Switch on again". The button saves the separate thread (as the dropdown does, so the existing
  "restart to apply" notice follows), removes the copy and refreshes the page. Picking "separate" in the dropdown removes
  the copy too. The row is in the plan table (FEAT-UI-79) and the selftest inventory.

## Acceptance Criteria

- [x] xUnit for the kept copy; headless `--selftest` ok incl. `preferences complete` with the new row.
- [ ] In-world: after a real fallback (or a hand-written marker in `user://`), the next start shows the notice in
      Preferences > Graphics > Performance without Advanced; the button turns it off, the restart notice shows, and a
      restart brings the separate thread back (`[Boot] render thread: separate`).

## Not covered

No selftest builds the page against a kept copy: the copy lives in the real `user://`, and a test would touch the user's
own record.
