using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App;

public partial class ObjectRenderer
{
    internal (bool Passed, string Detail) SelfTestRemovalHidesAtOnce(World world)
    {
        var failures = new List<string>();
        void Expect(bool ok, string what) { if (!ok) failures.Add(what); }
        void Settle() => MainThreadWorkQueue.Pump(double.MaxValue);

        const int Count = 300;
        const ulong TestRegion = 11437119954698752UL;
        var made = new List<Entity>(Count);

        try
        {
            var agent = world.GetOrCreateEntity(TestRegion, 1);
            agent.SetComponent(new AvatarComponent(Guid.NewGuid(), "Self", "Resident", isLocalAgent: true));
            agent.SetComponent(new TransformComponent(new System.Numerics.Vector3(128f, 128f, 25f), System.Numerics.Quaternion.Identity));
            _agentPos = new Vector3(128f, 128f, 25f);
            _agentPosKnown = true;

            // 1. Create 300 visuals for one test region handle, shown, near the agent
            for (uint i = 0; i < Count; i++)
            {
                var e = world.GetOrCreateEntity(TestRegion, 20000 + i);
                e.SetComponent(new TransformComponent(
                    new System.Numerics.Vector3(128f + (i % 10) * 0.5f, 128f + (i / 10) * 0.5f, 25f),
                    System.Numerics.Quaternion.Identity));
                var prim = new PrimitiveComponent(System.Numerics.Vector3.One, 0, isMesh: false);
                e.SetComponent(prim);
                world.NotifyComponentUpdated(e, prim);
                made.Add(e);
            }
            Settle();

            Expect(_visuals.Count >= Count, $"expected at least {Count} visuals created, got {_visuals.Count}");

            foreach (var e in made)
            {
                if (_visuals.TryGetValue(e.Id, out var st))
                {
                    st.ResourcesReleased = false;
                    st.MeshInstance.Visible = true;
                    st.Shown = true;
                    st.StaticBody.CollisionLayer = PhysicsLayers.Objects;
                }
                else
                {
                    failures.Add($"visual for entity {e.Id} missing before test");
                }
            }

            // 2. Call OnEntityRemoved for all 300
            foreach (var e in made)
            {
                OnEntityRemoved(this, new EntityEventArgs(e));
            }

            // 3. Right away, before any _Process:
            //    - every one of the 300 has MeshInstance.Visible == false, PendingRemoval == true and StaticBody.CollisionLayer == 0
            //    - all 300 are still in _visuals (not freed yet)
            int hiddenCount = 0;
            foreach (var e in made)
            {
                if (!_visuals.TryGetValue(e.Id, out var st))
                {
                    Expect(false, $"entity {e.Id} missing from _visuals right after removal");
                    continue;
                }
                Expect(!st.MeshInstance.Visible, $"entity {e.Id} MeshInstance.Visible was not set to false immediately");
                Expect(!st.Shown, $"entity {e.Id} Shown was not set to false immediately");
                Expect(st.PendingRemoval, $"entity {e.Id} PendingRemoval was not set to true immediately");
                Expect(st.StaticBody.CollisionLayer == 0, $"entity {e.Id} StaticBody.CollisionLayer was not 0 immediately ({st.StaticBody.CollisionLayer})");
                if (!st.MeshInstance.Visible && !st.Shown && st.PendingRemoval && st.StaticBody.CollisionLayer == 0)
                    hiddenCount++;
            }
            Expect(hiddenCount == Count, $"{hiddenCount} of {Count} hidden immediately");

            // 4. Runs one cull sweep. None of them became visible.
            _cullOrder.Clear();
            _cullOrder.AddRange(_visuals.Keys);
            _cullCursor = 0;
            _cullCarry = 0;
            int sweepFrames = 0;
            while (_cullCursor < _cullOrder.Count && sweepFrames++ < 50)
            {
                _Process(0.1);
            }

            int reShown = 0;
            foreach (var e in made)
            {
                if (_visuals.TryGetValue(e.Id, out var st))
                {
                    if (st.MeshInstance.Visible || st.Shown) reShown++;
                }
            }
            Expect(reShown == 0, $"{reShown} visuals were re-shown by the cull sweep");

            // 5. Calls ProcessPendingRemovals until _pendingRemovals.Count == 0. Then none of the 300 is in _visuals.
            int drains = sweepFrames;
            while (_pendingRemovals.Count > 0)
            {
                ProcessPendingRemovals(maxBudgetMs: 2.0);
                drains++;
                if (drains > 500) break;
            }
            Expect(_pendingRemovals.Count == 0, $"_pendingRemovals not empty after {drains} drains: {_pendingRemovals.Count} left");

            int remaining = 0;
            foreach (var e in made)
            {
                if (_visuals.ContainsKey(e.Id)) remaining++;
            }
            Expect(remaining == 0, $"{remaining} of {Count} visuals still remain in _visuals after all drains");

            return failures.Count == 0
                ? (true, $"{Count} hidden in the removal frame, 0 re-shown by the sweep, all freed after {drains} drains")
                : (false, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            return (false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _agentPosKnown = false;
            foreach (var e in made)
            {
                if (_visuals.ContainsKey(e.Id)) RemoveVisual(e.Id);
            }
            Settle();
        }
    }
}

public partial class SelfTest
{
    private static Check CheckRemovalHidesAtOnce(SceneTree tree)
    {
        const string Name = "removal hides at once";
        var objects = new ObjectRenderer();
        try
        {
            tree.Root.AddChild(objects);
            var world = new SLNG.Core.ECS.World();
            objects.Initialize(world, null!, null!);
            var (passed, detail) = objects.SelfTestRemovalHidesAtOnce(world);
            return new Check(Name, passed, detail);
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(objects)) objects.QueueFree();
        }
    }
}
