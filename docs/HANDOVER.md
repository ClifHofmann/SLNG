# HANDOVER

**One file.** Overwrite the section below when you hand over new work; do not create
`handover-<date>-<topic>.md` alongside it.

---

# 2026-09-14/15 — Inventory (offers + cache), the seat-pose rule, script permissions

`v0.22.142` → **`v0.22.162-alpha`**. **25 commits, all on `main`, all pushed** (`origin/main` ==
`HEAD` == `dd20200`). Working tree clean. One release tagged mid-session, **`v0.22.147-alpha`**,
built and published by CI with its installer. Every commit passes both builds + 745 tests +
`dotnet format` + `check_shader_globals` + `--selftest` 39/39.

**Next session: the only open item is the pose stand.** Everything else below is done and
in-world confirmed. Start at *OPEN: the pose stand* — it already carries the full evidence trail,
so do **not** re-derive it.

---

## Done and confirmed in-world

| ID | What |
|---|---|
| **BUG-AVATAR-08** ✅ | Walking into a building lifted the avatar onto the roof. The ground probe cast from 2 m above `transform.Position` — the collision cylinder *centre*, so ~1 m above the head — and accepted ceilings and roofs as floor. Now starts one step (0.5 m) above the **feet** and only counts a hit whose `normal.Y > 0.5`. |
| **FEAT-AVATAR-03** ✅ | Avatar menu + hover height. Was already implemented, only unverified. |
| **BUG-INV-04** ✅ | Receiving an offered item. Three separate defects — see below. |
| **FEAT-INV-07** ✅ | Versioned inventory cache, all three phases. |
| **FEAT-UI-27** ✅ | Seated movement keys drive the camera; standing up is the Stand button's job. |
| **FEAT-NET-01** 🧪 | Script permission requests (`llRequestPermissions`) — built, not yet seen firing in-world. |
| **FEAT-ANIM-03** ✅ (opt-in) | Furniture pose over a worn AO. **Ships OFF by default**; see the spec's *Why it is opt-in*. |

### BUG-INV-04 — three defects, not one

1. `InventoryOffered` (4) and `TaskInventoryOffered` (9) fell through `OnInstantMessage`'s
   `MessageFromAgent` guard and vanished. **Do not fix this by subscribing to
   `InventoryManager.InventoryObjectOffered`** — it fires synchronously and the next line sends
   accept-or-decline from `args.Accept`, which the constructor sets to `false`. Subscribing while
   asking the user would auto-decline every gift. Same trap as `GroupManager.GroupInvitation`.
2. Declining sent the message but left the item where it was. The grid files an agent's gift
   **before** the offer arrives (llviewermessage.cpp:1714-1717), so moving it out is the viewer's
   job — `LLDiscardAgentOffer` to `changeItemParent(item, Trash)`.
3. The discard never reached SL. `InventoryManager.MoveItem` prefers AIS and PATCHes a bare
   `parent_id`, which **SL answers 400**. The inventory's own *Delete* was broken on SL for the
   same reason. The reference viewer never reparents through AIS — a move is the legacy UDP
   `MoveInventoryItem` / `MoveInventoryFolder` on every grid (llviewerinventory.cpp:566-579,
   :647-660). `MoveToTrashAsync` now sends those packets itself.

### FEAT-INV-07 — the cache is LibreMetaverse's, not ours

The spec originally proposed writing our own cache. **LibreMetaverse already implements it:**
`Inventory.SaveToDisk` / `RestoreFromDisk`, and the restore is version-aware — it compares each
cached folder against the login skeleton's version and sets `InventoryNode.NeedsUpdate`
(`InventoryCache.cs:195-250`). That is `LLInventoryModel::loadSkeleton` in C#. Verified by
round-tripping a real store against the **pinned** 3.1.3, and pinned by `InventoryCacheTests`.

SLNG adds only: restore after login (before `InventoryPanel.Initialize`, or a fetch races it),
serve from the store when `NeedsUpdate == false`, save on quit **and** on logout.

Phase 3's real cost was ours, not the network: `Populate` re-filtered the whole tree **per
populated folder**, and the search crawl populates every folder — 810 on the reported inventory.
Coalesced to one pass per frame, plus a 180 ms debounce on the search box.

---

## OPEN: the pose stand

**Symptom.** Sitting on one particular pose stand (object `566c54f7`, localId `344277757`), SLNG
renders a T-pose. Firestorm and other viewers show the intended pose. A **second** pose stand
(`e598b886`) behaves identically in both viewers, so this is about particular objects, not about
pose stands as a category.

**Everything below is measured, not assumed. Do not redo it.**

| Checked | Result |
|---|---|
| Do we receive every animation the sim signals? | **Yes** — `AvatarManager` passes the complete `signaledAnimations` list through. |
| Do we decode them correctly? | **Yes** — an independent raw BinBVH parser confirms LibreMetaverse byte for byte. |
| Does our skeleton rest pose match SL's? | **Yes** — `avatar_skeleton.xml` is `rot="0 0 0"` throughout; both bind poses are a T-pose. |
| Does our priority blend match the viewer's? | **Yes** — newest wins a tie, as `LLJointStateBlender::addJointState` (strict `>`) plus `LLMotionController`'s `push_front` (llpose.cpp:196-231, llmotioncontroller.cpp:966). |
| Does the seat rule drop anything? | **No** — `dropped=[none]` in the log. |
| Is a permission request being missed? | **No** — the stand never sends one (zero `[ScriptPerm]` lines). |
| Does the viewer stop a zero-length looping animation? | **No** — `onUpdate` returns `mLastLoopedTime <= mDuration`, true here. |

**What the sim actually sends on that stand:** exactly one body animation,
`944447f1-0769-13a8-bf4b-d8649a001362`, sourced by the seat itself. Decoded from the cache:

```
version=1.0  basePrio=3  dur=0.1  in=0.1  out=0.1  loop=1  hand=1  joints=19
mShoulderLeft  prio=3  rotKeys=2  t=0.1 (0, +0.0087, 0)   t=0.1 (0, 0, 0)
```

Every joint identity. It is a **reset animation** — it zeroes 19 body joints at priority 3 to
override whatever else is playing. Firestorm labels it `StandNormal*`; that name is **not** a
viewer built-in (it appears nowhere in the Linden source) — FS reads it from the object's
inventory, and the `*` marks exactly that.

### The one difference the data shows

Firestorm's Animation Explorer on that stand lists our seven animations **plus**:

```
body_noise - 2    pelvis_fix - 0   hand_motion - 1   physics_motion - 1
breathe_rot - 1   eye - 1          head_rot - 1
```

No `*`, no `(att.)` — these are the viewer's built-in procedural motions, started by
`LLVOAvatar::startDefaultMotions()` (llvoavatar.cpp:2107-2124) on **every** avatar, always. The
simulator never signals them, which is why they appear in no SLNG log. **SLNG plays none of
them.**

**Honest caveat:** these are small procedural motions — breathing, idle noise, pelvis
stabilisation. It is *not* established that they alone explain an arms-out T-pose. They are the
only difference left after everything above was ruled out, and they are missing regardless of
this bug.

### Suggested next step

Implement the seven built-in motions as a scoped feature. `hand_motion` is independently
interesting: it is what applies an animation's `hand_pose` field, and this stand sets
`hand_pose = 1`.

If that does not resolve it, the next move is a **measurement, not another guess** — a
side-by-side of SLNG's and Firestorm's rendered skeleton on the same stand, joint by joint.

---

## Traps worth not re-learning

- **LibreMetaverse's offer/invitation events answer for you.** `InventoryObjectOffered` and
  `GroupInvitation` fire synchronously and send accept-or-decline from an `Accept` that defaults
  to `false`. `ScriptQuestion` does **not** — it only notifies, so subscribing to it is safe.
- **`Entity.Id` is not an object id.** It is `Guid.NewGuid()` per entity; the simulator's object
  UUID lives in `MetadataComponent.Id`. Using the wrong one made the seat rule silently never fire
  — for anyone, on any seat. `ApplyObjectProperties` documents this exact trap and it still caught
  us out.
- **A 2-joint, priority-6, zero-length animation is a deformer, not a pose.** Mesh bodies ship
  ankle locks and pelvis fixes that key real body joints. Treating them as poses T-posed the
  avatar.
- **SL's AIS refuses a reparent.** Moves go over UDP on every grid.
- **The console log is now behind `--diag`.** 299 lines became roughly 40. Errors and one-time
  state still print unconditionally; per-mesh and per-frame diagnostics do not.
  `SLNG.Core.Diag.Verbose` mirrors the flag for `src/`, which cannot see `app/`'s `Diagnostics`.
- **Decode assets from the cache instead of guessing.**
  `%APPDATA%/Godot/app_userdata/Puris Viewer/cache/assets/<id>.anim`. Two rounds of speculation
  were settled in minutes this way, after three rounds of guessing were not.

---

## Also in this session

- **Console log cleanup.** Diagnostics moved behind `--diag`. `CompareSelfShapeSourcesAsync` now
  returns early without it — it decoded every worn wearable purely to print a comparison nothing
  acts on, so it was work as well as noise.
- **Texture RIDs at shutdown.** "14 RIDs of type Texture were leaked" — the GPU cache was already
  disposed, the **static** texture fields were not (`TerrainRenderer`'s noise LUT,
  `ObjectParticles`' generated default particle texture). Freed explicitly now. **Unverified
  against the count:** the headless selftest never creates them, so only a real shutdown can say
  whether 14 moved.
- **Sitting behaviour (FEAT-UI-27).** A movement key used to fire a `Stand()` request and then let
  the local prediction run *as if it had worked*, so a seat the simulator would not release left
  the avatar walking away from a body still bound to it. Movement keys now drive the camera
  instead — A/D swing, W/S zoom, arrow keys equivalent. A deliberate departure from the reference
  viewer, which only sets a control flag and lets the simulator decide whether to stand you up.
