# ADR 0004: Performance architecture, built on what the reference viewer cannot do

- Status: proposed
- Date: 2026-10-10
- Constrains `MVP6-1` and every `FEAT-PERF` / `BUG-PERF` task from `FEAT-PERF-12` on.

## Context

The October 2026 performance work (FEAT-PERF-08, FEAT-PERF-13, BUG-AVATAR-11, the BUG-PERF-14..16
findings) kept arriving at the same kind of fix: find where Firestorm is cheaper, then copy its rule.
The rules copied so far were a mirrors default, an avatar cap and jelly dolls, and impostors are next
on that path. Each copy helped. None of them changes the fact that the scene is built the way the
reference viewer's scene is built: one object per prim face, work in the frame that needs it, and
data thrown away and rebuilt.

The maintainer's direction (2026-10-10): **we have the more modern architecture. Use it; do not
rebuild Firestorm.**

### What the measurements say about our architecture today

Sirens Beach on Agni, v0.27.30, render thread on, raised draw distance:

| Measure | Value |
|---|---|
| Scene nodes | 57,642 |
| Objects | 37,969 |
| Draw calls | 35,260 |
| Main-thread script time | 30–280 ms per frame |
| Godot flush | 18–97 ms |
| VRAM | 7.6 GB |

- **The frame is spent on bookkeeping, not on pixels.** The GPU needs 28 ms. Everything else is the
  CPU walking and mutating a scene graph that is ~10× larger than the number of things on screen.
- **Our expensive moments are rebuilds:**
  - An avatar crossing the cap refetches and re-rigs its whole outfit.
  - A return to a region drops worn items.
  - A shrink reads textures back from VRAM.
- **The same outfit costs per mesh, not per avatar.** Every rigged attachment is its own instance
  with its own Skin. One mesh body is dozens of skinned instances.

## Decision

Five structural pillars. Every performance task from now on is judged by which pillar it advances.
A parity default (for example mirrors off) is still fine when it is free, but it is not the plan.

### P1: The main thread only applies

All preparation happens on workers: decode, mesh build, tangents, surface splits, materials,
skinning data and texture resizes. The main thread commits finished results within a fixed per-frame
budget. Synchronous RenderingServer read-backs (`SurfaceGetArrays`, `GetImage`, RS getters) are
banned on hot paths, because with the separate render thread (FEAT-PERF-13) every one of them is a
full stall.

*Tasks:* BUG-PERF-15. Moving the remaining `avatar.rig.*` and `avatar.split_sorted` main-thread
work to workers.

### P2: The world is drawn from the ECS, not from a scene tree

`SLNG.Core.World` is already an entity-component store. Static prims become **RenderingServer
instances owned by the ECS** (RIDs, no `Node3D`): no scene-tree notifications, no per-node interop,
no 57k-node flush. Picking, selection and edit gizmos keep a node only while an object is
interacted with. On top of that come engine-side visibility ranges by object size (a C++ cull,
0 ms of C#), occluders generated from large opaque prims, and merged static geometry per spatial
cell and material. The reference viewer's draw-info batching is the comparison point for the
merge, not the template.

*Tasks:* FEAT-PERF-14 and FEAT-PERF-20 (redefined here), FEAT-PERF-21.

### P3: An avatar is one character, not N attachments

When an outfit changes, a worker merges all of the avatar's rigged meshes into **one skinned mesh
per avatar**:
- one Skin with the union of binds (bind-shape baked into vertices, overrides applied)
- opaque surfaces merged per material
- sorted alpha surfaces kept apart (BUG-RENDER-16)

One avatar is then a handful of draws and one skin update per frame, instead of dozens. Over-cap
avatars are **parked** (hidden, skin detached, data kept) and only unloaded under memory pressure, so
a camera swing costs nothing. The jelly doll stays the fallback for avatars never loaded. Impostors
become a cheap later option on top of a merged character, not a prerequisite.

*Tasks:* BUG-PERF-16 (park), outfit merge (new), FEAT-PERF-18, FEAT-PERF-22 (re-scoped).

### P4: One importance scheduler for every asset

There is one priority queue for meshes, textures, rigs, animations and uploads, ordered by
on-screen importance:
- your own avatar first
- then pixel area or distance
- then everything else

Each step has a budget: network slots, decode workers, upload bytes per frame, and VRAM. The disk
cache stores **GPU-ready** data, so a warm start is a copy, not a re-decode:
- block-compressed textures (BC1/BC3/BC7)
- prepared meshes with tangents
- merged outfits, keyed by outfit hash

The reference viewer caches raw assets and decodes them again every session.

*Tasks:* BUG-PERF-14 (first slice), FEAT-PERF-17, the decoded cache (FEAT-PERF-11) evolving into a
GPU-ready cache.

### P5: Per-frame animation runs on the GPU

Work that is a pure function of time moves into shaders:
- texture animation (FEAT-PERF-16)
- UV scrolling
- particles where Godot's GPU particles fit

Avatar animation evaluation moves to workers or engine-native playback (FEAT-PERF-18). The CPU then
only sees state changes.

## Consequences

- **Measuring is unchanged.** It stays at the fixed spot (`MVP6-1` baseline). The target is no
  longer "match Firestorm" but frame time against node count and draw count. Success means the
  per-frame cost scales with what is on screen, not with what is in the region.
- **P2 and P3 change long-lived structures** (ObjectRenderer's node-per-prim model, the worn-item
  pipeline). Each starts with a spec and a measurement probe, lands behind a setting, and replaces
  the old path only after an in-world A/B, the way FEAT-PERF-13 did.
- **The open cards fit as follows:**

| Task | Status under this ADR |
|---|---|
| BUG-PERF-15 | P1 prerequisite |
| BUG-PERF-16 | P3 first step |
| BUG-PERF-14 | P4 first slice |
| FEAT-PERF-15 (mirrors setting) | Stays only because it is free |

- **Order:** P1 (it unblocks the render thread), then P3 and P4 (crowded-sim arrival is the
  maintainer's main complaint), then P2 (the largest change), then P5 alongside.
