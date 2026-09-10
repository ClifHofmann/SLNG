# Feature: FEAT-RENDER-07 (Depth of Field / DoF)

## Context
As a later addition (specifically for the Photography and Machinima tools), a Depth of Field (DoF) effect should be implemented. This allows the camera to focus on a specific subject (e.g., the avatar or a selected object) while applying a cinematic blur to the out-of-focus background and foreground.

## Requirements
1. **Engine Integration:**
   - Utilize Godot's built-in Depth of Field post-processing capabilities (accessible via `Camera3D` attributes or `WorldEnvironment`).
2. **Focus Mechanics:**
   - **Auto-Focus:** Raycast from the center of the viewport (or mouse position) to dynamically adjust the focal distance to the hit object.
   - **Manual Focus:** Allow the user to manually set the focal distance and aperture/blur intensity (F-Stop equivalent).
3. **UI Integration:**
   - Add toggles and sliders for DoF (Enabled, Focus Distance, Blur Amount) to the upcoming Photography/Snapshot UI (`MVP3-4`).

## Acceptance Criteria
- [x] Depth of Field can be cleanly toggled on and off without artifacts.
- [x] Auto-focus reliably finds the distance to the subject being looked at.
- [x] UI sliders provide smooth manual control over the blur intensity and focal plane.
- [x] The feature is cleanly integrated into the `MVP 3` Snapshot Studio toolset.

## Implementation (v0.22.4-alpha, refined v0.22.5-alpha)

`feature/FEAT-RENDER-07-depth-of-field`. Three new files plus wiring in `Boot.cs` and
`SnapshotWindow.cs`.

### `app/scripts/DepthOfFieldController.cs` — a plain `Node`

Owns a `CameraAttributesPractical` and attaches it to the play camera. Godot exposes DoF as
two independent blur zones (everything nearer than X, everything further than Y); the
user-facing model is a **focal plane plus a sharp band in metres**. That translation lives
in `Apply()` and nowhere else:

- far zone: blur begins at `focus + range/2`, then ramps in over a **`falloff`** distance of
  `max(focus * 1.5, 2)` m — deliberately long. A real lens's circle of confusion grows
  continuously with distance and then flattens; Godot only offers a single linear ramp, so a
  short transition (the first cut used `range/2`) put full blur a couple of metres past the
  subject and read as the blur *snapping on*. Scaling `falloff` to the focal distance keeps a
  near focus tighter than a far one. (v0.22.5)
- near zone: blur begins at `focus - range/2` (clamped > 0 — a near distance of 0 puts the
  ramp behind the camera and Godot blurs the whole frame), transition = `min(falloff, near)`
  because the foreground only has that much room before the camera, only when "blur
  foreground" is on

**Toggling off detaches the resource** (`camera.Attributes = null`), it does not merely zero
`DofBlurAmount` — a `CameraAttributes` left assigned keeps its exposure model in the
pipeline, and "cleanly toggled on and off" is an acceptance criterion.

**Bokeh kernel (v0.22.6).** The first time DoF switches on, the controller sets the global
DoF kernel to `RenderingServer.DofBokehShape.Circle` + `DofBlurQuality.High` + jitter.
Godot's defaults are Box at Very Low with no jitter, which on a round high-contrast object
(a cartwheel against bright grass — reported in-world) renders as a stair-stepped double
edge / halo rather than a soft blur. It is global render state, but it only takes effect
while a `CameraAttributes` with DoF is on the active camera — only ever this controller — so
nothing is restored when DoF turns off. If High proves too costly on a busy sim it becomes
a quality dropdown; DoF is off by default so ordinary play never pays for it.

The camera is `AvatarController` (itself a `Camera3D`). It is the **only** live camera —
`FreeCamera` is never instantiated and the View-menu first/third/free switch is a
`LogMessage` stub — so one attachment covers the whole live view, and the effect shows in
both the preview and the saved PNG for free.

### Auto-focus

Casts `ProjectRayOrigin/Normal` through viewport centre against
`Objects | Terrain | Avatars` (same mask as `AvatarController`'s Alt-click focus ray).

- **20 Hz, not per-frame.** Not for the ray's cost — one `IntersectRay` is cheap — but for
  the terrain heightfield: `TerrainRenderer` marks unstreamed patches NaN so a ray *misses*
  rather than reporting floor-at-0, and Godot's heightfield raycast runs `normalize()` over
  the NaN cell en route to the miss, printing `Vector3 cannot be normalized` every time.
  That is the exact flood `PhysicsLayers.Terrain` was split out to spare `CursorManager`
  from. The ray also only runs while DoF **and** auto-focus are both on.
- Focal depth is the hit distance **along the view axis** (`(hit - origin)·normal`), not the
  euclidean distance — the two diverge toward the edges of a wide FOV, where euclidean would
  put the plane slightly too far and soften the subject it just focused on.
- Eased toward with a frame-rate-independent exponential (half-life 0.12 s) so a subject
  crossing frame doesn't snap.
- A centre-ray miss eases the target back to the manual `FocusDistance`, not to infinity —
  pointing at empty sky should not throw the frame out of focus.

### `app/scripts/UI/DofSettings.cs`

Settings holder, `preferences.cfg [dof]` section, same ConfigFile pattern as
`CameraSettings`. `Enabled` defaults **off**: DoF costs a full-screen blur pass and
auto-focus costs a raycast, and ordinary play should pay neither. The three slider setters
take a `persist` flag — an `HSlider` fires `ValueChanged` every step of a drag, and a
`ConfigFile.Save()` per step is a synchronous main-thread file write up to once a frame; the
UI applies live with `persist:false` during the drag and writes once on `DragEnded`.

### UI — in `SnapshotWindow`, not Preferences

DoF is framing, not a quality setting — you set the focal plane while looking at the shot.
Enable toggle, auto-focus checkbox with a live `→ N.N m` / "nothing in the centre of frame"
readout, focus / sharp-band / blur sliders, "blur foreground" toggle, reset button. The
focus slider stays editable while auto-focus is on — `_Process` tracks the live focal plane
onto its handle, and grabbing it is a **manual override** that switches auto-focus off, so
the value the user set holds and the handoff has no jump. (v0.22.5 — it was read-only under
auto-focus at first, which just read as a broken slider.)

### Not done here

Not yet A/B'd in-world — the auto-focus feel and the near/far zone mapping want a real
scene. No FOV control (that is `FEAT-UI-22`). No aperture/f-stop model — Godot's DoF is a
0..1 blur amount, and a physically-derived CoC would need `CameraAttributesPhysical`'s
exposure pipeline, out of scope for a photography blur.
