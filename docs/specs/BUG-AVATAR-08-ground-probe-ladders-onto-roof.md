# [BUG-AVATAR-08] Walking into a building teleports the self avatar onto the roof

- **Feature ID:** `BUG-AVATAR-08`
- **Track:** `render`
- **Status:** `✅ Done` — verified in-world 2026-09-14: walking into a house no longer lifts the avatar onto the roof.
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Reported live 2026-09-14 with a side-by-side screenshot: walking into a house in Puris lifts the
own avatar (and with it the camera) onto the roof, while Firestorm — logged in as the same agent,
at the same moment — keeps showing that avatar correctly inside the building.

The disagreement localises the bug immediately. The self position is **never** sent to the
simulator: `AvatarController` sends control flags through `GridSession.SetMovement` and the sim
echoes back the resulting position (the long comment at `AvatarController.cs:813` explains why the
horizontal prediction was removed). So the simulator, and every other viewer, had the avatar in the
right place the whole time. Only one piece of code writes a *local* Z: the ground clamp.

## Root cause

`client-output.log` contains the climb frame by frame — five consecutive `[GroundClamp]` lines,
each one finding a higher collider than the last:

```
sim-collision-plane                groundZ=23,18  agentZ=23,81
collider:Obj_0c42cb2b…/StaticBody  groundZ=25,82  agentZ=24,10
collider:Obj_9956c7cc…/StaticBody  groundZ=27,46  agentZ=26,62
collider:Obj_adffcc03…/StaticBody  groundZ=27,48  agentZ=28,29
collider:Obj_19ec87c6…/StaticBody  groundZ=27,85  agentZ=28,55
… settles at                       groundZ=28,09  agentZ=28,89
```

Two defects compose into that ladder:

1. **The probe started above the head, not at the feet.** The downward ray began at
   `godotPos + 2.0`, and `godotPos` is the collision **cylinder centre** — the clamp forty lines
   below adds the same half body height back (`clampTargetZ = groundHeight + halfBodyZ`). The ray
   therefore started ≈ 2.95 m above the feet, about a metre above the head, and any horizontal
   surface in that window — a ceiling slab, a door lintel, a roof — came back as "the floor".
   The comment on the line even said *"from 2 meters above the avatar's feet"*; the `- halfBodyZ`
   was simply missing.

2. **`44e45a0` (BUG-AVATAR-05) made that hit authoritative.** It changed the raycast from
   *"only when the sim support plane has no answer"* (`if (!hasGround && result.Count > 0)`) to
   *"highest hit wins"* (`if (!hasGround || rayZ > groundHeight)`), so a ceiling now outvoted the
   simulator's own collision plane.

Together they close a positive feedback loop: the clamp lifts the avatar onto the surface it just
found → the ray origin rises with the avatar → next frame it finds the surface above that one.
Entering the house climbed 5.7 m in five frames.

### What the reference viewer does

- `LLVOAvatar::resolveHeightGlobal` (`llvoavatar.cpp:5981`) probes `inPos + 0.5` → `inPos - 0.5`;
  `LLVOAvatar::getGround` (`llvoavatar.cpp:7068`) uses ±1.0. Both are tight windows around the
  **foot** position — the viewer never probes from head height.
- `LLWorld::resolveStepHeightGlobal` (`llworld.cpp:532-593`) does not raycast object geometry at
  **all**: it takes the land heightmap and, when the avatar has one, the server's `mFootPlane`
  (Havok's collision plane). The server decides what an avatar stands on; the viewer only reads it.
  That is exactly the `sim-collision-plane` source already implemented here.

## Fix (v0.22.139-alpha)

`app/scripts/AvatarController.cs`:

- `halfBodyZ` is computed **before** the probe (it was computed inside the clamp) so the feet
  position is available, and the ray runs from `feet + GroundProbeStepHeight` (0.5 m — the
  viewer's own window, and the tallest step that can be walked up without jumping) down to
  `feet - 100`. This bounds any raycast-sourced clamp to one step **by construction**: the ray
  cannot see a surface it does not reach.
- A hit only counts as ground when its normal points up (`normal.Y > GroundProbeMinNormalY`,
  0.5 ⇒ slopes up to 60°). A straight-down ray also hits the underside of a ceiling and the inside
  face of a wall; those return a normal pointing down or sideways and are now ignored, falling
  back to the simulator's plane.

BUG-AVATAR-05's intent — a child prim, board or step on top of a linkset base wins over the coarse
support plane — is preserved, because such a surface is within one step of the feet.

## Acceptance Criteria

- [x] `dotnet build SLNG.sln`, `dotnet build app/SLNG.App.csproj`, `dotnet test` (711 tests),
      `dotnet format`, `check_shader_globals.py`, `--selftest` all pass
- [ ] Walking into a building keeps the avatar on the floor, in agreement with a second viewer
      (live confirmation pending)
- [ ] Stepping onto a low prim (≤ 0.5 m) still works; a higher ledge is not climbed

## Technical Specs & Affected Files

- `app/scripts/AvatarController.cs` — ground probe origin, walkable-normal test, constants
- `app/scripts/Boot.cs` — `AppVersion` → `v0.22.139-alpha`

## Related

- `BUG-AVATAR-05` — introduced the "highest hit wins" rule this bug rides on
- `BUG-AVATAR-06` — falling is a constant-speed clamp, not gravity (still open, same code block)
