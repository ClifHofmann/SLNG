# [BUG-RENDER-34] The mirror plane was measured in the wrong space

- **Feature ID:** `BUG-RENDER-34`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `claude` (graphics-engineer)
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

*„Jetzt sehe ich nur irgendeine Wand als Spiegelung, aber nicht mich selbst oder Inventar."*

The diagnostic added in `BUG-RENDER-33` answered it without another guess:

```
[PlanarMirror] ON normal=(-1,0,0) radius=1,21 near=28,82 cos=0,05
[PlanarMirror] ON normal=(1,-0,-0) radius=1,21 near=3,06  cos=0,05
```

**`cos=0,05` on every line** — that is the floor the code clamps to, meaning the view direction
stands almost exactly *perpendicular* to the normal being computed. And the sign flips between
frames. Both say the same thing: by that maths the camera sits **inside** the mirror's own plane.
Everything downstream divides by that cosine, which is where a 28 m near plane came from, and with
the near plane 28 m out the reflection contains nothing but whatever lies beyond it.

So neither the near plane (`BUG-RENDER-33`) nor the clipping was the remaining fault. The **plane
itself** was wrong.

## Why

`FrontFaceNormal` took the thinnest axis of the mesh's **local** bounding box and then used the
matching **column of the global basis** as the normal. Those are the same thing only when the
mesh's local axes line up with its own bounding box — and for this content they do not.

## The fix

Transform the bounding box into **world space** and take its thinnest axis there:

```csharp
var world   = state.MeshInstance.GlobalTransform * state.MeshInstance.GetAabb();
var extents = world.Size;
Vector3 axis = extents.X <= extents.Y && extents.X <= extents.Z ? Vector3.Right
             : extents.Y <= extents.Z ? Vector3.Up : Vector3.Back;
```

This asks the question directly — *which way is this panel flat?* — and answers it in the frame the
reflection maths actually uses. Exact for anything built against a wall, which is where mirrors
hang; a panel rotated off the world axes gets the nearest axis, still far closer than a basis
column chosen by a local extent.

Two guards came with it:

- A viewer standing **in** the panel's plane has no front face to reflect, and every number derived
  from it degenerates. Below 5 cm the normal is returned as zero, which `MirrorReflection` already
  treats as "no mirror" — better than a coin flip between two signs.
- `MirrorExtents` is published and printed on the state line. "Which way is this panel flat" is the
  one input the whole reflection rests on; reading it back is what turns the next wrong normal from
  a theory into a measurement.

## Round 2: the axis was right, the SIGN was the camera's

With the world-space measurement in, the state line proved the axis correct and exposed the
remaining half:

```
normal=(1,0,0)     near=0,95  cos=0,6   extents=(0, 2,29, 0,77)   <- correct
normal=(-1,-0,-0)  near=30,03 cos=0,05  extents=(0, 2,29, 0,77)   <- flipped
```

`extents=(0, 2.29, 0.77)` says the panel is flat along world X, so the axis was never in doubt
after round 1. But the **sign alternated frame by frame**, and on every flipped frame `cos`
collapsed to its floor and the near plane blew out to 30 m — a reflection of nothing but the far
distance, which is what the user saw as *„er ignoriert die Wand und zeigt das Außen"*.

The sign came from **which side of the plane the camera was on**. A mirror's front face is a
property of the object; the camera is not a witness to it, and in third person it routinely swings
back *through* the wall the mirror hangs on. The **agent** stays in the room, so it decides the
side now — with hysteresis, because a stable witness is not an infallible one: the flip is only
accepted once the avatar is clearly (0.35 m) past the plane, so walking along a mirror cannot
oscillate its reflection.

When the camera does end up behind the glass, `MirrorReflection.UpdateFor` returns false on its own
(`distance <= 0`) and the mirror simply stops rendering — which is correct, since from back there it
cannot be seen anyway.

## Acceptance Criteria

- [ ] `cos=` on the state line tracks the viewing angle (near 1 head-on) instead of sitting at the
      0.05 floor.
- [ ] `normal=` is stable between frames rather than flipping sign.
- [ ] The reflection shows the room and the avatar again, from every angle the mirror is visible.

## Technical Specs & Affected Files

- `app/scripts/ObjectRenderer.cs` — `FrontFaceNormal` measured in world space, the in-plane guard,
  and `MirrorExtents`.
- `app/scripts/Boot.cs` — `extents=` on the `[PlanarMirror]` line; `AppVersion` `v0.22.142-alpha`
  (`141` was round 1, the world-space axis).

## Sub-tasks / Progress

- [x] Found from the state line, not from another hypothesis — the diagnostic paid for itself one
      round after it was added.
- [x] Build (both), 711 tests, format, shader globals 28/28, selftest 39/39, `project.godot`
      untouched.
- [x] **Confirmed in-world 2026-09-14** (Agni): *„OK das funktioniert“*.
