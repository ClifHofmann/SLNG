# [FEAT-NET-06] Keep own avatar + attachments + HUDs across teleport

- **Feature ID:** `FEAT-NET-06`
- **Track:** `net/core`
- **Status:** `⏸️ Pending`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md)

## Overview & Goal

During a teleport or region change, the own avatar, all attached items (mesh bodies, hair, accessories) and all HUDs are currently torn down and rebuilt from scratch.
Logs on Agni (e.g. teleport Millenium → Secret Love at ~10:02 on 2026-10-08, v0.26.107) reveal:
- Over ~30 s at 19–34 fps, `avatar.hud_mesh` rebuilt 353×, hundreds of `avatar.rig` items queued (including self avatar), and work queue peaked around ~15,000 items.

**Root cause in SLNG:**
- `World.RemoveRegion` (`src/SLNG.Core/ECS/World.cs:123`) keeps only the local-agent entity and deletes the agent's attachments, HUDs and child prims along with the old region.
- `WorldSimulation.ApplyAvatarUpdate` (`src/SLNG.Core/WorldSimulation.cs` ~767-791) removes the old self entity and instantiates a new one (carrying over only `AvatarComponent`), forcing renderers to rebuild the self avatar from scratch.
- The `World` is keyed strictly by `(regionHandle, localId)` with internal `Entity.Id` (Guid); the SL object UUID is not leveraged for entity continuity across region changes.

**Reference Viewer Implementation (`scratch/slviewer`):**
- `LLViewerObjectList::killObject` never kills `gAgentAvatarp`. It calls `setRegion(gAgent.getRegion())` (`llviewerobjectlist.cpp:1336-1343`).
- `LLViewerObject::setRegion` recurses into every child, i.e. all attachments and HUDs (`llviewerobject.cpp:7143-7176`).
- When the new region sends the same objects with new local IDs, `processObjectUpdate` / `processCachedObject` locate them by full UUID and only update their local ID and region (`llviewerobjectlist.cpp:325-346` and `563-594`). Nothing is destroyed or rebuilt.

Goal: Re-key the local agent and all attached entities across teleport/region changes by UUID instead of destroying and recreating them.

## Technical Specification

### 1. SLNG.Core: Entity Re-Keying & Attachment Tree Retention
- Add a `World` operation to move an existing entity to a new `(regionHandle, localId)` key, preserving `Entity.Id`, its components, and attachment hierarchy.
- Emit a re-key event (e.g. `EntityRekeyed`) distinct from `EntityRemoved` + `EntityAdded` so renderers treat it as "same object, new key".
- Modify `World.RemoveRegion`:
  - Retain the local agent AND every entity in its attachment tree (attachment roots parented to agent, child prims, HUDs).
  - Mark these preserved entities as "awaiting re-confirmation".

### 2. SLNG.Net / WorldSimulation: UUID-Based Identity Matching
- Ensure SL UUID is carried in `ObjectUpdate`, compressed updates, and cached probe DTOs into Core (`WorldSimulation`).
- When an update arrives matching an SL UUID already held in the self-attachment tree (or for the local agent itself), re-key that entity to the new `(regionHandle, localId)` instead of creating a new entity.
- In `ApplyAvatarUpdate`, re-key the self entity instead of `RemoveEntity` + create.

### 3. Reconciliation for Detached Items
- For retained self-attachment entities that the new region does NOT re-send (detached during teleport), remove them after a bounded window once updates subside ("answers have gone quiet" heuristic, similar to `FEAT-NET-04`).
- Log summary:
  `[TeleportKeep] kept N self entities, M re-confirmed, K removed after Xs`

### 4. Renderers (`app/`): Handle Re-Key Without Rebuild
- Ensure `AvatarRenderer` and `ObjectRenderer` handle `EntityRekeyed`:
  - Do not trigger mesh or rig rebuilds.
  - Update internal mappings keyed by `localId` / `regionHandle` (HUD placement maps, attachment parent lookups, selection, physics pick-body metadata `"LocalId"`).
  - Verify HUD clicks after teleport continue resolving to the updated `LocalId` via `TryClickHud`.

## Acceptance Criteria
- [ ] Unit tests for `World` entity re-keying, `RemoveRegion` attachment tree retention, and quiet-window reconciliation pass.
- [ ] In-world teleport between two Agni regions shows ~0 `avatar.hud_mesh` and 0 self-avatar rig rebuilds in `[WorkCost]` over the subsequent 30 s.
- [ ] HUDs remain visible, in-place, and responsive/clickable across teleports.
- [ ] Items detached in transit are removed upon reconciliation window expiry.
- [ ] Solution and `app/` build clean, tests pass, `dotnet format` clean.
