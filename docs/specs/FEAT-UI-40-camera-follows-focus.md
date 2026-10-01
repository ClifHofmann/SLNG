# [FEAT-UI-40] The camera follows what it is focused on

- **Feature ID:** `FEAT-UI-40`
- **Track:** `ui`
- **Status:** `✅ Done`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Asked for in-world 2026-10-01: when the camera is zoomed onto an avatar (radar double-click) or aimed
at something with Alt+Click, it stayed on the spot it had at that moment. If the avatar walked on or the
object moved or was animated, it left the shot. The camera should follow it.

## What the reference viewer does (read, not assumed)
`LLAgentCamera::calcFocusPositionTargetGlobal` (llagentcamera.cpp:1629-1664): with a focus object set,
the focus target is `mFocusObject->getRenderPosition() + mFocusObjectOffset`, recomputed every frame.
`mFocusObjectOffset` is `focusTarget - objectPosition` at the moment the focus was set
(`updateFocusOffset`, :1563-1570) -- a world-space vector, **not** turned with the object. A dead focus
object clears the offset and the focus object (`validateFocusObject`, :1572-1581); the focus point stays
where it last was. Walking does not cancel the focus (already so in SLNG).

## Acceptance Criteria
- [x] Alt+Click on an avatar or object: the focus follows the body that was hit (an avatar, a moving or
      animated object, a worn attachment).
- [x] Radar double-click on an avatar: the focus follows that avatar by agent id.
- [x] The pan to a new focus runs toward the subject's CURRENT position.
- [x] Terrain is a fixed spot; a subject that is gone leaves the camera where it last was.
- [x] `Esc` (reset camera), the camera HUD's preset views and a new focus end the following.
- [x] Confirmed in-world (v0.24.134, 2026-10-01).

## Technical Specs & Affected Files
- `app/scripts/FocusFollow.cs` -- the focus point as "subject position now + the offset it had when it
  was set". Built from a physics body (`OfBody`; freed or out of the tree = gone) or from any
  `Func<Vector3?>` (`Of`).
- `app/scripts/AvatarController.cs` -- `_focusFollow`; `FollowFocusSubject()` runs once a frame before
  `UpdateTransition` and moves either the pan's destination (while panning) or `_orbitTarget`; set by
  the Alt+Click ray (`FollowForHit`, not for the terrain layer) and by `FocusOnAvatarFrontal(agentId, ...)`;
  cleared by `SetPresetView` (Esc and the preset views). `AvatarSubject` finds the avatar's entity by
  agent id and looks again for up to 3 s if it vanishes (region crossing: the next simulator sends it as a
  new object before the old one is removed).
- `app/scripts/UI/MinimapOverlay.cs`, `app/scripts/Boot.cs` -- the radar's focus request now carries the
  agent id.

## Not done / known limits
- An avatar known only from a coarse location has no body in the world, so it is aimed at as a fixed spot.
- Touching the camera pad or the zoom pad during the 0.5 s pan cancels the pan, and the focus then jumps
  to the subject's live position instead of finishing the glide.
- A second double-click on the same radar row releases the MAP's focus only; the camera keeps following
  until `Esc`.
