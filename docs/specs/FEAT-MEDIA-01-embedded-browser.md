# [FEAT-MEDIA-01] Embedded web browser on a MOAP face

- **Feature ID:** `FEAT-MEDIA-01`
- **Track:** `render` / `infra`
- **Status:** `⏸️ Pending` — split out of `MVP3-3` (Phase 4) when that milestone closed,
  2026-09-17. **First acceptance criterion is "ADR accepted" — nothing else here starts
  before that.**
- **Owner:** —
- **Agent:** `architect` (ADR) then `graphics-engineer` (implementation)
- **Dep:** `MVP3-3` (Phase 1/3 — the data model and face-texture-swap seam this would extend)
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md) · [MVP3-3](file:///E:/Git/SLNG/docs/specs/MVP3-3-shared-media-moap.md)

## Overview & Goal

The full reference-viewer MOAP experience: a live, interactive web page (or YouTube/Vimeo
embed, or MP4/HLS stream) rendered directly on a prim face, with an in-world navigation
toolbar (back/forward/reload/URL/volume) the way Firestorm shows one on click. `MVP3-3` Phases
1 and 3 cover the data model and static-image case with no new dependency; this is the
remaining, architecturally different half.

**Why this needs its own id and can't just be "more of MVP3-3":** Godot 4 core has no built-in
web view and no video codec that covers what SL residents actually put on a MOAP face
(`VideoStreamTheora` needs a fully-downloaded file, not a stream, and there's no VP8/9/H.264
decoder at all). The only way to show a real web page is a native dependency — most likely a
CEF-based GDExtension (e.g. `gdcef`) — which is a per-platform build and licensing commitment
that reshapes the installer/CI pipeline (`.github/workflows/build_installer.yml`) and
forecloses the mobile goal outright if adopted casually. That is squarely inside AGENTS.md's
"do not change tech stack without an ADR" gate.

## What the ADR needs to weigh (from the MVP3-3 architecture review)

- **In-process embedding vs. a separate process.** The real reference viewer does **not**
  embed CEF in its own process — it runs it as a separate process (`SLPlugin`), specifically
  to isolate a browser crash from taking down the viewer and to keep the extension surface
  small. Consider the same split rather than assuming in-process is the only option.
- **Per-platform build cost.** A native GDExtension needs its own binary per target platform;
  Windows-first is fine short-term, but the ADR should say explicitly what happens to
  Linux/macOS (kept buildable per AGENTS.md) and the mobile goal (a later goal, but one this
  decision can quietly foreclose if not named).
- **Licensing.** CEF's own license plus whatever wrapper is chosen; needs a clear statement,
  not an assumption.
- **Revisit condition.** If Godot ever ships a first-class web view, this decision — and
  possibly the whole feature — should be revisited rather than carried forward by inertia.
- **Engine-agnostic boundary.** Whatever is chosen lives entirely in `app/`; `src/` stays
  untouched (AGENTS.md's layering rule already covers this, but the ADR should say so
  explicitly given how much surface a browser engine has).

## Acceptance Criteria

- [ ] ADR written and accepted (`docs/adr/000X-<name>.md`) covering the questions above.
- [ ] (Everything below is gated on the ADR and deliberately not scoped further here.)
- [ ] A MOAP face with a real webpage `CurrentUrl` (not just an `image/*` one) renders the live
      page on the face.
- [ ] Basic interactivity: at minimum, clicking a link/scrolling on the face reaches the
      embedded browser, not just a static snapshot.
- [ ] Respects `MediaFace.ControlPermissions`/`InteractPermissions`/whitelist — the same
      TPV-driven checks `FEAT-MEDIA-02` already has to implement for the simpler fallback.
- [ ] A crash in the embedded browser does not take down SLNG.
- [ ] Confirmed on the primary target platform (Windows) at minimum; Linux/macOS status stated
      explicitly even if not implemented in the first cut.

## Technical Specs & Affected Files

- New: whatever GDExtension/native binary the ADR selects, `app/` only.
- `src/SLNG.Core/MediaFace.cs`, `PrimitiveComponent.MediaFaces` — the existing data model this
  would consume (already built, already tested by `MVP3-3`).
- `app/scripts/ObjectRenderer.cs` — the face-texture-swap seam `ApplyMediaImageAsync` already
  established for Phase 3; a live browser surface would replace/extend that per-face texture
  with a `SubViewport`-rendered one instead of a static `TextureData`.
- `.github/workflows/build_installer.yml` — will need updating for whatever native binaries
  the chosen approach ships.

## Sub-tasks / Progress

- [ ] Write the ADR (architect).
- [ ] Get it reviewed/accepted.
- [ ] Everything else is out of scope until then.
