# [BUG-NET-34] After a teleport the old region stays visible at its old place for up to a minute

- **Feature ID:** `BUG-NET-34`
- **Track:** `render`
- **Status:** `⏸️ Pending`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

**Reported in-world 2026-10-15:** *„Ich sehe wieder die alte Sim, von der ich weggesprungen bin.“* The user
logged in at region Ungren and teleported to Millenium / Azure Haven. A club from Ungren then floated about
45 m above Azure Haven. Ungren's ground is at 65–76 m; Azure Haven's is at 2–29 m.

**What the log shows (`godot.log` of that session):**

```
[Origin] 0,0 -> region 741070837455616; world holds 33436 entities [9 regions]
[RegionLoad] +60s region 1995613604717568: world holds 0 entities for it, 143140 scene nodes
[RegionLoad] +30s region 741070837455616: ... 156182 scene nodes
[RegionLoad] +60s region 741070837455616: ... 24045 scene nodes
```

**Cause (read in the code):**

- **The nodes never move.** A node is placed once, relative to the region origin at the moment it is built
  (comment above `Boot.LogOriginChange`, BUG-NET-21). Nothing moves it when the origin changes. So the old
  region's nodes stay at their Godot coordinates, and those now lie on top of the new region.
- **The entities go at once:** `World.RemoveRegion` (`src/SLNG.Core/ECS/World.cs:182`) removes them and raises
  `EntityRemoved` for each one.
- **The nodes go slowly:**
  - `ObjectRenderer.OnEntityRemoved` (`app/scripts/ObjectRenderer.cs`, search `private void OnEntityRemoved`)
    only puts the id into `_pendingRemovals`.
  - `ProcessPendingRemovals(maxBudgetMs = 2.0)` then frees about 2 ms of nodes per frame (BUG-PERF-11).
  - A burst of 30,000 removals therefore takes 30–60 s. During that time the old region stays on screen. The
    per-frame cull sweep can even show those nodes again, because they are still in `_visuals`.
- **The terrain lags too:** `TerrainRenderer.OnTerrainUpdated` only marks the region dirty, and the next
  rebuild (0.75–10 s later) frees it.

**Goal:** within the frame in which a region's entities are removed, none of its objects or its terrain can
be seen, clicked or stood on. Freeing the nodes keeps its frame budget, unchanged.

## Rules for this task

- Work in `E:/Git/SLNG`. Create the branch `fix/BUG-NET-34-hide-left-region` from `main`. Do not use
  `git worktree`.
- In `docs/ROADMAP.md`, set the BUG-NET-34 row to Owner `gemini` and status 🚧, and to 🧪 when done.
- Set `AppVersion` in `app/scripts/Boot.cs` to `v0.27.56-alpha`, or one patch above whatever is there.
- Do not touch:
  - the local agent, its attachments and HUDs. `World.RemoveRegion` already keeps them (FEAT-NET-06).
  - `AvatarRenderer`. It already drains all its removals in the same frame (`_pendingRemovals` loop in its
    `_Process`), so it does not lag.
  - re-keying across a teleport (`OnEntityRekeyed`).
- Commit: `fix(render): [BUG-NET-34] hide a removed region's objects and terrain at once (v0.27.56-alpha)`. Do
  not merge, push or release.

## Technical Specs & Affected Files

### Step 1: a flag on `VisualState`

In `app/scripts/ObjectRenderer.cs`, class `VisualState` (search `public MeshInstance3D MeshInstance = null!;`),
add:

```csharp
// BUG-NET-34: the entity is gone from the world and this visual is waiting in _pendingRemovals to be
// freed. Hidden at once; nothing may show, re-instance or re-collide it before it is freed.
public bool PendingRemoval;
```

### Step 2: hide immediately in `OnEntityRemoved`

Replace the body of `OnEntityRemoved` with:

```csharp
private void OnEntityRemoved(object? sender, EntityEventArgs e)
{
    var id = e.Entity.Id;
    if (!_visuals.TryGetValue(id, out var state)) return;
    HideForRemoval(id, state);
    if (_pendingRemovalSet.Add(id))
    {
        _pendingRemovals.Enqueue(id);
    }
}
```

Add this method next to it:

```csharp
/// <summary>BUG-NET-34: takes a removed entity off the screen and out of picking and physics in the
/// frame its removal arrives. Freeing the node stays in ProcessPendingRemovals under its budget: a
/// teleport removes tens of thousands of visuals at once, and before this they stood where they were
/// built -- on top of the new region -- for up to a minute.</summary>
private void HideForRemoval(Guid id, VisualState state)
{
    if (state.PendingRemoval) return;
    state.PendingRemoval = true;

    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

    // Out of its MultiMesh group FIRST: Leave() hands the shared mesh back to the member's own node,
    // so it must run before that node is hidden, not after.
    _instanceGroups?.Leave(id);

    if (IsInstanceValid(state.MeshInstance)) state.MeshInstance.Visible = false;
    state.Shown = false;
    if (IsInstanceValid(state.StaticBody)) state.StaticBody.CollisionLayer = 0;
    if (state.LightNode != null && IsInstanceValid(state.LightNode)) state.LightNode.Visible = false;

    _removalHideTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
    _removalHideCount++;
}

// BUG-NET-34: the cost of the hides above, summed and printed once per burst (see Step 4).
private long _removalHideTicks;
private int _removalHideCount;
```

**Check before you continue:**

- **The thread.** `World.EntityRemoved` must be raised on the main thread. `TerrainRenderer.OnTerrainUpdated`
  has a comment saying world events are applied on the main thread (`WorldSimulation.Pump`). Find the call
  chain to `World.RemoveRegion` and confirm it. If it is not the main thread, stop and report; do not add
  locking.
- **The light.** Look at where `state.LightNode` is created (search `new OmniLight3D`). If it is a child of
  `state.MeshInstance`, hiding the mesh hides it already. Keep the line anyway; it is harmless.

### Step 3: nothing may bring a pending visual back

In the cull sweep in `ObjectRenderer.cs` (search `if (!_visuals.TryGetValue(id, out var state)) continue; //
removed since the snapshot`), add directly after the next line (`if (!IsInstanceValid(state.MeshInstance))
continue;`):

```csharp
if (state.PendingRemoval) continue;   // BUG-NET-34: hidden, waiting to be freed
```

Then search `ObjectRenderer*.cs` for every other place that sets `MeshInstance.Visible = true`, sets `Shown =
true`, or joins a visual into `_instanceGroups` (`Join`, `Offer`, or similar; read `ObjectInstanceGroups.cs`).
Give each of those a `PendingRemoval` guard too. List every guard you added in your report.

### Step 4: one log line per burst

In `ProcessPendingRemovals`, before its first line (`if (_pendingRemovals.Count == 0) return;`), add:

```csharp
// BUG-NET-34: report the immediate hides once, after the frame they happened in.
if (_removalHideCount >= 100)
{
    double ms = _removalHideTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    GD.Print($"[RegionHide] hid {_removalHideCount} visuals in {ms:0.0} ms, {_pendingRemovals.Count} wait to be freed");
    _removalHideTicks = 0;
    _removalHideCount = 0;
}
else if (_pendingRemovals.Count == 0)
{
    _removalHideTicks = 0;
    _removalHideCount = 0;
}
```

(Fewer than 100 hides is an ordinary derez. It is counted, but it does not print.)

### Step 5: terrain of a removed region disappears at once

In `app/scripts/TerrainRenderer.cs`, replace the body of `OnTerrainUpdated`:

```csharp
private void OnTerrainUpdated(object? sender, ulong regionHandle)
{
    // World events are applied on the main thread (WorldSimulation.Pump), so this is
    // safe. Just mark the region dirty; the rebuild is coalesced in _Process.
    _dirtyRegions.Add(regionHandle);

    // BUG-NET-34: a REMOVED region's terrain is freed by the next rebuild, which can be 10 s away;
    // until then it would lie at its old place, inside the new region. Hide it and stop it
    // colliding now.
    if (_world != null && !_world.Terrains.ContainsKey(regionHandle)
        && _regions.TryGetValue(regionHandle, out var node))
    {
        node.Root.Visible = false;
        node.StaticBody.CollisionLayer = 0;
    }
}
```

In `RebuildTerrain`, find the branch where the region is present and `_regions.TryGetValue(regionHandle, out
var regionNode)` succeeds. A region can come back before its old node was freed (quick teleport back). So
make the node visible and colliding again there:

```csharp
regionNode.Root.Visible = true;
regionNode.StaticBody.CollisionLayer = PhysicsLayers.Terrain;
```

Check the exact names (`Root`, `StaticBody`, `PhysicsLayers.Terrain`) in the `RegionTerrainNode` class at the
top of the file.

### Step 6: selftest

Add `CheckRemovalHidesAtOnce(SceneTree tree)` in a new file `app/scripts/ObjectRenderer.RemovalHide.SelfTest.cs`
(`partial class ObjectRenderer`). Copy the setup style of an existing ObjectRenderer selftest; search
`ObjectRenderer.VisibilityRange.SelfTest.cs` or `ObjectRenderer.BatchPotential.SelfTest.cs` for how they
create visuals headless. Register it in `app/scripts/SelfTest.cs` next to `CheckBatchPotentialCost`. The check:

1. Creates 300 visuals for one test region handle, shown (`Visible = true`, `Shown = true`), at positions
   near the agent.
2. Calls `OnEntityRemoved` for all 300 (or removes them through a `World` the test owns, if the existing tests
   do it that way).
3. **Right away**, before any `_Process`:
   - every one of the 300 has `MeshInstance.Visible == false`, `PendingRemoval == true` and
     `StaticBody.CollisionLayer == 0`;
   - all 300 are still in `_visuals` (not freed yet).
4. Runs one cull sweep. None of them became visible.
5. Calls `ProcessPendingRemovals` until `_pendingRemovals.Count == 0`. Then none of the 300 is in `_visuals`.
6. Returns ok with a text like `300 hidden in the removal frame, 0 re-shown by the sweep, all freed after N
   drains`.

### Step 7: roadmap row

The BUG-NET-34 row already exists in `docs/ROADMAP.md`, after the BUG-NET-33 row. Set its status to 🚧 when you
start. When you are done, set it to 🧪 and add one sentence to it: what was built, and the `[RegionHide]` line
from the selftest.

## Acceptance Criteria

- [ ] `dotnet build SLNG.sln` and `dotnet build app/SLNG.App.csproj` both build with 0 errors.
- [ ] `dotnet test` passes.
- [ ] `dotnet format SLNG.sln --verify-no-changes` is clean.
- [ ] `godot --headless --path app --log-file user://logs/selftest/godot.log -- --selftest` prints `PASS`, and the
      new check is `ok`. Afterwards run `git status` and undo any change the headless run made to
      `app/project.godot`.
- [ ] Report: the selftest line, every `PendingRemoval` guard you added (file:line), and the answer to the
      thread question in Step 2.
- [ ] In-world (the user does this): log in somewhere crowded and teleport far away. In the first frame
      nothing of the old region is visible. The log shows `[RegionHide] hid N visuals in X ms`, and X stays
      a single hitch (target < 100 ms for 30k).

## Sub-tasks / Progress

- [ ] Steps 1–4 ObjectRenderer
- [ ] Step 5 TerrainRenderer
- [ ] Step 6 selftest
- [ ] Step 7 roadmap, version, commit
