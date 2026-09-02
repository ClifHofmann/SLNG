# [BUG-RENDER-04] 474 "Vector3 cannot be normalized" warnings at every avatar (re)build

- **Feature ID:** `BUG-RENDER-04`
- **Track:** `render`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/FEAT-SL-01-second-life-readiness`
- **Version:** `v0.20.4-alpha`

## Overview & Goal

User: *"Kannst du die warnings noch beseitigen (beim bauen und starten)."* At startup, right after
the six system body-part meshes load (`[AvatarBodyMeshService] Loaded avatar_head: ...` etc.),
`godot.log` printed 474 copies of `WARNING: Vector3 cannot be normalized, the elements must be
finite. Making (0, 0, 0) as a fallback.` (`core/math/vector3.h:551`) — pure console noise, no
reported visual defect, but real enough to be worth tracing to its actual source rather than
suppressed.

## Root cause

`AvatarRenderer.BuildPartMesh` builds each system body-part mesh (head, upper/lower body, eyes,
eyelashes, hair) with Godot's `SurfaceTool`, calling `GenerateTangents()` before `Commit()`.
`GenerateTangents()` computes each triangle's tangent from its UV gradient scaled by its position
edges — and LL's own `avatar_head`/`avatar_eye`/`avatar_upper_body`/`avatar_hair` `.llm` meshes
contain a handful of genuinely degenerate triangles, confirmed by a throwaway probe against the
real, unmodified output of `SLNG.Assets.AvatarBodyMeshService.Load` (every normal is well-formed —
this is a triangle-*shape* issue, not a decode bug):

| Part | Position-degenerate (zero 3D area) | UV-degenerate only |
|---|---|---|
| head | 70 | 4 |
| upper_body | 2 | 2 |
| eye | 80 | 0 |
| hair | 0 | 1 |
| lower_body, eyelashes | 0 | 0 |

152 position-degenerate + 7 UV-degenerate = 159 triangles, each touching 3 vertices ≈ 477 — matching
the observed 474 closely enough to confirm this is the source (the small gap is triangles sharing a
vertex with another degenerate triangle, not double-counted by Godot's per-vertex tangent
accumulation).

Both kinds are apparently intentional, decades-old topology from LL's own hand-authored mesh: a
zero-area triangle used to close a UV chart without needing a visible sliver, or a UV-only seam
where positions are fine but the three UV coordinates collapse. The real reference viewer evidently
tolerates both silently; Godot's `SurfaceTool::generate_tangents()` does not.

## Acceptance Criteria

- [x] Position-degenerate triangles (near-zero cross product of their position edges) are dropped
      from `BuildPartMesh`'s index buffer entirely — zero screen-space area, so omitting them
      changes nothing rendered.
- [x] UV-degenerate-only triangles (position fine, UV collinear/coincident) get their own 3
      duplicated vertices with one UV nudged by a sub-texel `1/8192` offset, so `GenerateTangents()`
      has a non-degenerate UV gradient to compute from. Every other, well-formed triangle keeps
      sharing the original indexed vertices unchanged.
- [x] The degeneracy check runs against the mesh's *actual* current `positions`/`normals` (the
      morphed values `BuildPartMesh` is called with), not a cached list from the unmorphed base
      mesh — a triangle's degeneracy can shift after a shape change, and this must keep working
      correctly on every `RebuildBodyMorphs` call, not just at first boot.
- [x] `dotnet build SLNG.sln` / `app/SLNG.App.csproj`: 0 warnings. `dotnet test`: 563/563.
      `dotnet format` clean, shader-globals clean, `--selftest` 26/26 (uniform counts unchanged —
      no shader touched).

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `app/scripts/AvatarRenderer.cs` | `BuildPartMesh` restructured around a local `SubmitVertex` helper (shared by the base N vertices and the handful of degenerate-triangle duplicates); the triangle-index loop now checks position-space and UV-space degeneracy per triangle before deciding whether to reuse the shared indices, skip, or duplicate-and-nudge. |

### Design notes

- **Measured, not guessed.** The exact degenerate-triangle counts per body part came from a
  throwaway console probe (`scratch/DecodeTest`, gitignored) run against the real
  `AvatarBodyMeshService.Load` output before writing any fix — this is what caught that the
  dominant cause is *position*-degeneracy (152 triangles), not UV-degeneracy (7) as a first,
  UV-only fix attempt would have addressed alone; that first attempt was checked against the same
  probe and found to leave ~450 of the 474 warnings unexplained before this was added.
- **Neither fix branch changes what renders.** A position-degenerate triangle has genuinely zero
  screen-space area — Godot would rasterize nothing for it whether present or absent. A
  UV-degenerate-only triangle keeps its real position-space geometry; only its tangent input
  changes, by an amount (1/8192 of a UV unit) far below any texture resolution this project bakes
  avatar skins to.
- **Why not just skip `GenerateTangents()` entirely for avatar meshes?** The shader family's own
  `prim_common.gdshaderinc` and every `prim_*_avatar.gdshader` variant do consume `TANGENT` for
  normal-mapped materials — dropping tangent generation outright would silently break any avatar
  material that uses a normal map, not just quiet the console.

## What the tests guarantee

Nothing new is meaningfully unit-testable — this is Godot-engine mesh-building code
(`app/scripts`, outside `tests-rules`' scope, which covers `src/`). The full 563-test suite passing
unmodified confirms no `SLNG.Assets`/`SLNG.Core` behaviour changed; `--selftest`'s unchanged
shader/uniform counts confirm no shader was touched. Confidence for the actual fix comes from the
probe's measured before/after (every detected degenerate triangle, in every affected body part,
confirmed no longer degenerate after the nudge; see the spec's own root-cause table for the raw
counts) rather than a test in the suite.

## Still open

- **Not yet re-verified in-world** — needs an actual boot + login to confirm `godot.log` no longer
  shows the 474 `Vector3 cannot be normalized` lines (or shows meaningfully fewer, if some other,
  not-yet-found source also contributes a handful).
- **The same `GenerateTangents()` pattern exists at two other call sites in `AvatarRenderer.cs`**
  (rigged/worn-attachment mesh building, ~line 1297 and ~line 2580) — not touched here, since
  creator-authored attachment meshes are far less likely to share this specific decades-old LL
  system-mesh quirk, and the log evidence pins the 474 warnings specifically to the system
  body-part build phase (right after the `[AvatarBodyMeshService] Loaded avatar_*` lines, before
  the first `[BomFace]` attachment message). Worth a similar probe if attachment-related tangent
  warnings are ever reported.
