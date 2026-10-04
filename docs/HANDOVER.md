# HANDOVER

**One file.** Overwrite the section below when you hand over new work; do not create
`handover-<date>-<topic>.md` alongside it.

---

# 2026-10-03/04 — camera zoom and focus pick, seed-capability crash

`v0.26.28-alpha` (released) → **`v0.26.31-alpha`** (on `main`, not released, not yet pushed at the time
of writing). Both builds, all tests (Core 1347, Assets 172, Net 1104), `dotnet format`, shader globals and
`--selftest` 95/95 pass. **None of the three items below has been seen in-world yet.**

## Merged to `main`, awaiting the maintainer's in-world test

| ID | What | Test |
|---|---|---|
| **FEAT-UI-55** 🚧 | Camera zoom and focus pick, closer to Firestorm. Wheel zoom is multiplicative (2^¼ per notch) and eases (0.07 s half-life). With no focus the wheel makes **the point under the cursor the focus point without turning the camera** (`AnchorFocusAt`), so the focus crosshair and the depth of field land on it; moving the cursor more than 8 px away re-picks. A focus from Alt+Click / roster stays a plain dolly. Alt+Click on an avatar tests its **skeleton** (`AvatarRenderer.TryPickAvatar`, capsules between joints) and focuses the **surface** point; phantom foliage no longer shields a person. Walking releases a wheel-set focus back to avatar follow. | zoom on an avatar's face / arm / shoulder; Alt+Click behind grass; walk after a wheel zoom |
| **BUG-NET-31** 🧪 | A login whose seed capability request failed killed the client with a **stack overflow**: LibreMetaverse 3.1.6 `Caps.SeedRequestCompleteHandler` cancels its own token and retries synchronously, with no limit. `SeedCapabilityGuard` aborts the loop from the log filter, re-seeds on a timer (2/4/8/15/15 s) and ends the session cleanly with `SessionEndReason.CapabilitiesUnavailable`. Memory: `lmv-seed-cap-retry-recursion`. | log in with an account that is already logged in elsewhere; look for one `[Caps] the seed capability request failed (...)` line and no "Stack overflow." |

## Open

- **Why the seed request failed instantly** on the maintainer's login is **not known** (grid not in the log).
  The guard's first log line names the likely cause; ask for it on the next occurrence.
- **FEAT-UI-56** ⏸️ alpha-aware foliage in the focus pick (see-through texels let the ray through).
- Hover cursor and depth of field still use the old 0.22 m avatar capsule (`AvatarRenderer`, `AvatarPhysics`).
- Left out of FEAT-UI-55 on purpose: per-focus-type minimum distance, FOV zoom, Alt-drag as 1 % per pixel,
  `CameraHUD` per-frame `ZoomStep`, the 0.4 s focus glide, camera collision. Study:
  `docs/specs/camera-zoom-parity-firestorm-vs-slng.md` (both sections, with a FEAT-UI-55 status note).
- Open question to the maintainer: should the wheel become fully Firestorm-like (never zoom to the cursor)? Today
  it zooms to the cursor when there is no Alt+Click focus.
- Still unconfirmed from earlier releases: friend's MFA login (v0.26.28), BUG-GRID-01, FEAT-UI-43 keyboard,
  FEAT-INV-14, FEAT-UI-54 Group Info live behaviour, BUG-NET-30, BUG-RENDER-40 (needs a log from a crowded hub),
  About Land on OpenSim, HiDPI HUD items. Test cases: `docs/TESTFAELLE.md`.
- Backlog: FEAT-LAND-04 (Access tab), FEAT-LAND-06+ (Region/Estate), BUG-UI-20 (friends list shows login names),
  gap-list tickets FEAT-UI-44..53.

## Release

Latest tag is `v0.26.28-alpha`. To cut `v0.26.31-alpha`: CI green → annotated tag → installer workflow → English
release notes (`gh release edit --notes-file`). The maintainer's tester can only get builds through GitHub releases.

## Traps worth not re-learning

- `godot --headless --selftest` can rewrite `app/project.godot`; `git checkout app/project.godot` before staging.
- `dotnet build SLNG.sln` does not compile `app/`; build `app/SLNG.App.csproj` separately.
- Never merge before the maintainer confirms the in-world test.
- ROADMAP rows have 8 cells; edit by exact-prefix replace, never split on ` | ` (empty cells).
- `client-output.log` is UTF-16 and can be hundreds of thousands of lines; read it with offsets, never whole.
