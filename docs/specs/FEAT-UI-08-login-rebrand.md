# [FEAT-UI-08] Login / Boot Screen Rebrand (Puris Glassmorphism Theme)

- **Feature ID:** `FEAT-UI-08`
- **Track:** `ui`
- **Status:** `⏸️ Pending`
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
- [ ] Login panel (`LoginScreen/Panel`) uses the glassmorphism dark-navy/teal theme
      instead of the current default Godot panel style.
- [ ] "PURIS" title + version label match the mockup's typography treatment.
- [ ] Inputs (`GridInput`, `FirstInput`, `LastInput`, `PassInput`, dropdowns) themed to
      match (dark fill, teal focus ring).
- [ ] `SaveLoginCheck` uses a themed checkbox matching the teal checkmark style.
- [ ] `LoginButton` uses the teal→blue gradient + hover glow.
- [ ] Loading screen (`LoadingScreen`) replaces the current spinner with a circular
      progress indicator + step checklist, driven by real load-progress signals (not
      the mockup's `setInterval` fake-progress simulation — needs mapping to actual
      boot/connect stages already logged via `Boot.cs`'s `LogMessage` calls).
- [ ] No regression to login functionality, saved-profile dropdown, or the
      `user://logins.cfg` persistence path.
- [ ] `dotnet build` + `dotnet test` clean; manual test of login + loading flow in the
      dev client.

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
- [ ] Build a `Theme`/`StyleBoxFlat` resource for the dark-navy/teal palette.
- [ ] Reskin `LoginScreen/Panel` and its child controls.
- [ ] Replace the loading-screen spinner with a circular progress ring + step
      checklist, wired to real connection/load stages.
- [ ] Verify in the dev client (`tools/run-client.ps1`), both login and loading states.

## Open Questions
- Does "Second Life Next Generation" stay as the loading-screen tagline, or is that
  mockup placeholder text?
- Background image: mockup uses a stock Unsplash tech photo — final choice needed, or
  keep the current `Background` `TextureRect` and only recolor the gradient overlay?
  (Default: keep the current background, only apply the teal/blue gradient overlay,
  to avoid a licensing question over the stock photo.)

## Status
v0.3.2 release build confirmed working — implementation unblocked, in progress on
this branch.
