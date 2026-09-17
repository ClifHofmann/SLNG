# [FEAT-MEDIA-02] MOAP click-to-open in the system browser

- **Feature ID:** `FEAT-MEDIA-02`
- **Track:** `render` / `ui`
- **Status:** `⏸️ Pending` — split out of `MVP3-3` (Phase 2) when that milestone closed, 2026-09-17
- **Owner:** —
- **Agent:** `graphics-engineer` (raycast → face resolution) + `ux-designer` (confirm dialog)
- **Dep:** `MVP3-3` (Phase 1 — the `MediaFace`/whitelist/permission model this reads)
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md) · [MVP3-3](file:///E:/Git/SLNG/docs/specs/MVP3-3-shared-media-moap.md)

## Overview & Goal

The interaction fallback for a MOAP face that isn't (or can't be) auto-rendered by
`MVP3-3` Phase 3: clicking the face shows a confirm dialog naming the host and full URL, then
opens it in the system's default browser — real user value with zero new engine dependency,
while `FEAT-MEDIA-01`'s embedded browser is pending its own ADR.

**The blocker this task actually has to solve is not the dialog.** `ObjectSelectionController`'s
left-click handler resolves a raycast hit to an **object**
(`_session.ClickObjectAsync(rawLocalId, position: hitPosSl)`,
`ObjectSelectionController.cs:228`) but never to a **face** — every call site passes
`faceIndex: 0` even though `ClickObjectAsync` already accepts a real one. Prim collision is one
`ConcavePolygonShape3D` per whole object (`ObjectRenderer.cs:4480,4521`), not per face, so
Godot's raycast result gives only a world-space hit point, no face/triangle id. Resolving
"which SL face was clicked" needs a small CPU-side picking step against the object's own
`MeshData` (which submeshes already carry `FaceIndex`, see `SLNG.Core.FaceSurfaceMerge`) — this
does not exist anywhere in the renderer today and is this task's real scope.

## Guardrails (from the MVP3-3 architecture review, carried forward — not yet implemented)

- Validate with `Uri.TryCreate(url, UriKind.Absolute, ...)` and allow only `http`/`https`
  before ever calling `OS.ShellOpen` — `CurrentUrl` comes from an in-world object and an
  unvalidated scheme (`file://`, `ms-msdt:`, ...) is arbitrary URI-handler invocation.
- Check `SLNG.Core.MediaPermissionEvaluator`/`MediaWhitelist` client-side before offering the
  dialog at all, not just server-side — TPV Non-negotiable #1 (honor creator permissions)
  applies regardless of what the sim also enforces.
- No automatic OS-browser open, ever, not even of just a preview: opening an external program
  is a materially bigger action than an in-scene texture, and MOAP is a known
  IP-disclosure/griefing vector (a rezzed prim can point media at a server the griefer controls
  and log every visitor's IP) — this action must stay user-click-gated, full stop. (This does
  not extend to `MVP3-3` Phase 3's auto-rendered image, which is a lower-risk case, only ever
  fires for a face the creator explicitly flagged `AUTO_PLAY`, and is exactly what a reference
  viewer already shows with zero clicks — confirmed live: Firestorm auto-renders an `AUTO_PLAY`
  image face and shows nothing for a non-`AUTO_PLAY` one until clicked.)

## Acceptance Criteria

- [ ] A raycast hit on a world object resolves to the specific SL face number that was clicked
      (not just the object), reusing the submesh `FaceIndex` data `FaceSurfaceMerge` already
      carries.
- [ ] Clicking a face with `PrimitiveComponent.MediaFaces[i] != null` shows a confirm dialog
      naming the host and full `CurrentUrl` before doing anything else.
- [ ] The URL is validated (`http`/`https` only) and the permission/whitelist check runs
      client-side before the dialog is even offered.
- [ ] Confirming opens the URL in the OS default browser (`OS.ShellOpen`); declining or
      dismissing does nothing.
- [ ] A face with no media, or one the permission/whitelist check rejects, produces no dialog
      and no click-through to the object's normal touch/sit behaviour is broken.
- [ ] Unit tests for the new face-resolution logic (pure geometry, testable without a live grid).
- [ ] `dotnet build` (solution + `app/`), `dotnet test`, `dotnet format`, shader-globals,
      `--selftest` all clean.
- [ ] Confirmed in-world against `tools/testassets/moap_probe.lsl`'s non-`AUTO_PLAY` face.

## Technical Specs & Affected Files

- `app/scripts/ObjectSelectionController.cs` — left-click dispatch, currently passes
  `faceIndex: 0` unconditionally.
- `app/scripts/ObjectRenderer.cs` — owns the `MeshData`/`FaceIndex` data the resolution step
  needs; likely home for the new picking helper.
- `src/SLNG.Core/FaceSurfaceMerge.cs` — existing per-submesh `FaceIndex` bookkeeping to reuse.
- `src/SLNG.Core/MediaFace.cs` — `MediaPermissionEvaluator`, `MediaWhitelist` (already built,
  already tested — this task consumes them, doesn't build them).

## Sub-tasks / Progress

- [ ] Design the raycast-hit → SL-face-number resolution (likely a CPU-side point-in-triangle
      test against the object's cached `MeshData`, restricted to a small radius around the hit).
- [ ] Wire face resolution into `ObjectSelectionController`'s left-click handler.
- [ ] Confirm dialog UI (host + URL, Accept/Decline).
- [ ] Client-side permission/whitelist gate before offering the dialog.
- [ ] `OS.ShellOpen` on confirm, scheme-validated.
- [ ] Tests + in-world confirmation.
