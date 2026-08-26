# [FEAT-UI-08] Login / Boot Screen Rebrand (Puris Glassmorphism Theme)

- **Feature ID:** `FEAT-UI-08`
- **Track:** `ui`
- **Status:** `✅ Done`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Reskin the login/boot screen (`app/scenes/Boot.tscn` + `app/scripts/Boot.cs`) to match
the "Puris Viewer" branding the installer already ships under (`installer/PurisViewer.iss`
→ `MyAppName = "Puris Viewer"`). This is a visual/theme change only — the existing
login form fields and loading-screen flow already match the mockup's layout 1:1, so no
structural rework of the node tree or login logic is expected.

Design reference (HTML/CSS mockup provided by the user, Tailwind-based):
[FEAT-UI-08-login-rebrand-mockup.html](file:///E:/Git/SLNG/docs/specs/assets/FEAT-UI-08-login-rebrand-mockup.html)

### Design tokens (extracted from the mockup)
- **Background:** dark navy `#0f172a`, with a full-bleed background image, teal→blue
  diagonal gradient overlay (`rgba(15,118,110,.6)` → `rgba(30,58,138,.8)`, 135deg),
  `background-blend-mode: overlay`.
- **Login panel:** glassmorphism card — `rgba(15,23,42,.55)` fill, `backdrop-filter:
  blur(20px)`, 1px `rgba(255,255,255,.08)` border, large soft drop shadow, rounded
  `2xl` (~16px) corners, fade-in-from-below on appear (`opacity 0→1`,
  `translateY(15px→0)`, 0.8s ease-out).
- **Accent color:** teal `#0d9488` / `#2dd4bf` (focus rings, checkmarks, progress
  ring, glow text). Buttons use a teal→blue gradient (`#0d9488` → `#0369a1`) with a
  glow shadow that intensifies on hover.
- **Inputs:** near-black `rgba(0,0,0,.5)` fill, subtle `rgba(148,163,184,.15)` border,
  teal focus ring (`0 0 0 2px rgba(13,148,136,.3)`) + darker fill on focus.
- **Checkbox:** custom-drawn (native checkbox has no equivalent styling in Godot;
  needs a themed `CheckBox` icon override), teal checkmark, teal border when checked.
- **Typography:** title "PURIS" — wide letter-spacing (`0.25em`), semibold, white.
  Version string directly under the title, small/uppercase/muted gray, wide tracking.
- **Logo:** app icon, ~96×96, rounded (`2rem`), drop shadow. **Resolved:** `app/icon.svg`
  IS the Puris logo (teal/blue gradient abstract mark, matches the mockup palette
  already) — not the Godot default robot icon. No new asset needed; import it as a
  `Texture2D` (Godot auto-imports `.svg`, already has `app/icon.svg.import`) and
  render it in a `TextureRect` sized/rounded to match.
- **Loading view:** replaces the current spinner (`LoadingScreen/VBox/HBox/SpinnerBox`)
  with a circular SVG progress ring (percentage centered inside) next to a 5-line
  step checklist that turns teal + `[✓]` as progress crosses 20/40/60/80/100%
  thresholds. Small uppercase tagline ("Second Life Next Generation") above it.
- **Status bar:** bottom-left, small monospace gray text, teal for the active/success
  line — already structurally present via `Boot.cs`'s `LogMessage`/status text, just
  needs the color treatment.

## Acceptance Criteria
- [x] Login panel (`LoginScreen/Panel`) uses the glassmorphism dark-navy/teal theme
      instead of the current default Godot panel style.
- [x] "PURIS" title + version label match the mockup's typography treatment.
- [x] Inputs (`GridInput`, `FirstInput`, `LastInput`, `PassInput`, dropdowns) themed to
      match (dark fill, teal focus ring).
- [x] `SaveLoginCheck` uses a themed checkbox matching the teal checkmark style (icon
      rasterized from the app's existing Material Symbols icon font, not a new asset).
- [x] `LoginButton` uses a teal→blue accent + hover glow (see Deviations: flat blended
      color, not a true CSS-style gradient fill — see below).
- [x] Loading screen (`LoadingScreen`) replaces the current spinner with a circular
      progress indicator (`TextureProgressBar`, radial fill) + step checklist, driven by
      real load-progress signals: LibreMetaverse's own login handshake stages (relayed
      through a new `GridSession.LoginProgress` event / `SLNG.Core.LoginProgressEvent`)
      plus two of Boot.cs's own genuine milestones (local session init before
      `LoginAsync`, post-login world/avatar setup after it). The old
      `SimulateLoadingAnimation` timer-based fake progress is gone.
- [x] No regression to login functionality, saved-profile dropdown, or the
      `user://logins.cfg` persistence path (login logic itself untouched).
- [x] `dotnet build` + `dotnet test` clean; manual visual check of the login screen in
      the dev client (see Status for what was and wasn't verified live).

### Deviations from the mockup (see Status for why)
- Button "gradient" is a single flat blended teal-blue color (`#087E94`-ish), not a real
  linear gradient fill -- Godot's `StyleBoxFlat` has no gradient-fill property, and adding
  a shader/gradient-texture button skin was judged not worth the complexity for a reskin
  task (same reasoning already applied to the blur decision).
- Background "overlay" blend is a plain alpha-composited `GradientTexture2D`, not a true
  Photoshop-style "overlay" blend mode (Godot's CanvasItem has no such blend mode without
  a custom shader).
- Login logo (`app/icon.svg`) is not pixel-masked to rounded corners -- `Control.clip_contents`
  only clips to a rectangle in Godot, not to a `StyleBoxFlat`'s rounded corner radius.

## Technical Specs & Affected Files
- `app/scenes/Boot.tscn` — theming (Theme resource / StyleBoxFlat overrides per
  control), node additions for the progress ring + step list in `LoadingScreen`.
- `app/scripts/Boot.cs` — wire real progress into the new step-checklist UI (currently
  drives `LogPanel`/`ProgressLabel`/`SpinnerLabel` text directly — see existing
  `LogMessage` call sites for the stages to map to the 5 mockup steps).
- New theme resource(s) under `app/` (e.g. `app/theme/puris_login_theme.tres`) for the
  `StyleBoxFlat` glass-panel look. **Resolved:** no real backdrop blur — a
  `SubViewport`/`BackBufferCopy`/shader blur isn't worth the complexity for a screen
  shown briefly at boot; a flat semi-transparent `StyleBoxFlat` (dark navy fill,
  ~55% alpha, 1px pale border, soft shadow) approximates the look closely enough.
- `app/icon.svg` reused directly as the login logo — no new asset needed.

## Sub-tasks / Progress
- [x] Logo asset resolved — reuse `app/icon.svg`.
- [x] Blur approach resolved — flat translucent `StyleBoxFlat`, no real backdrop blur.
- [x] Build `StyleBoxFlat` resources for the dark-navy/teal palette (`Boot.tscn`
      sub-resources: glass panel, input normal/focus, button normal/hover).
- [x] Reskin `LoginScreen/Panel` and its child controls (logo, title, inputs, checkbox,
      button, status bar).
- [x] Replace the loading-screen spinner with a circular progress ring + step
      checklist, wired to real connection/load stages (see Acceptance Criteria).
- [x] Verify in the dev client — login screen only; see Status for the loading-screen
      caveat.

## Open Questions (resolved)
- "Second Life Next Generation" tagline: kept (already real, existing copy in
  `Boot.tscn`), just re-styled (small/uppercase/muted-teal) to match the mockup instead
  of being replaced or removed.
- Background image: kept the existing `boot_bg.jpg` per the stated default, with only
  the teal→blue diagonal `GradientTexture2D` overlay added on top (no shader, no true
  Photoshop-"overlay" blend mode — see Deviations). The existing background reads well
  under the new gradient; no need to reconsider it.

## Status
Implementation complete on `main` (merged in `bf102e2`).
`dotnet build`/`dotnet test` clean. Visually verified live in the dev client for the
**login screen** only (screenshot-confirmed: glass panel, gradient background, PURIS
title, themed inputs, teal button/glow, checkbox icon). The **loading-screen**
progress-ring/step-checklist could not be exercised live in this pass — see the task
report for why (a working-tree collision with concurrent, unrelated in-flight edits in
this same checkout forced stopping interactive testing early) — logic was verified by
code review + clean build/test only. A follow-up pass should click Login in the dev
client and visually confirm the ring/checklist animate through their 5 real stages.
