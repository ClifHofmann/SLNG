using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App;

public partial class ObjectRenderer
{
    /// <summary>FEAT-RENDER-27: feeds object updates through a real <see cref="WorldSimulation"/>
    /// (with a stand-in event source) into this renderer and checks the nodes: a turning root and its
    /// child are drawn where the spin has turned them, while the world model keeps the simulator's
    /// transforms; an update that changes nothing does not snap the prim back for a frame; a flipped
    /// omega swings it back; a prim that starts turning leaves its MultiMesh group and is not let
    /// back in; an uncompressed zero omega puts it back where the simulator has it. Also measures what
    /// the per-frame pass costs per prim.</summary>
    internal (bool Passed, string Detail) SelfTestTargetOmega(World world)
    {
        var failures = new List<string>();
        void Expect(bool ok, string what) { if (!ok) failures.Add(what); }
        void Settle() => MainThreadWorkQueue.Pump(double.MaxValue);

        var source = new SelfTestEventSource();
        using var simulation = new WorldSimulation(world, source);
        SpinDriven = simulation.SpinDrivenEntities;

        var at = new System.Numerics.Vector3(128f, 128f, 25f);
        var spinZ = new System.Numerics.Vector3(0f, 0f, MathF.PI / 2f); // a quarter turn a second
        const uint Root = 4001, Child = 4002, Other = 4003, Twin = 4004;
        const uint FirstExtra = 4100;
        const int Extras = 200;
        string costDetail = "";

        void Send(uint localId, System.Numerics.Vector3 position, uint parent = 0,
            System.Numerics.Vector3 omega = default, bool resetsSpin = false)
        {
            // A mesh object with no asset service: nothing is fetched, and the id stays the same across
            // updates so an update is not read as a new shape.
            var meshId = new Guid((int)localId, 0x7e57, 0, new byte[8]);
            source.Raise(new ObjectUpdateEvent(SelfTestRegion, localId, position, System.Numerics.Quaternion.Identity,
                System.Numerics.Vector3.One, 0, true, meshId, Guid.Empty, Guid.Empty, System.Numerics.Vector4.One,
                ParentLocalId: parent, AngularVelocity: omega, ResetsSpin: resetsSpin));
            simulation.Pump();
            Settle();
        }

        VisualState Shown(uint localId)
        {
            var state = _visuals[world.GetEntity(SelfTestRegion, localId)!.Id];
            state.ResourcesReleased = false;
            state.Shown = true;
            state.MeshInstance.Visible = true;
            return state;
        }

        void Frames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                simulation.AdvanceSpins(1f / 60f);
                TickTargetOmega();
            }
        }

        static Godot.Quaternion AboutSlZ(float radians)
        {
            var q = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, radians);
            return new Godot.Quaternion(q.X, q.Z, -q.Y, q.W);
        }

        static bool Near(Godot.Quaternion a, Godot.Quaternion b) => Mathf.Abs(a.Normalized().Dot(b.Normalized())) > 0.9995f;

        try
        {
            Send(Root, at, omega: spinZ);
            Send(Child, new System.Numerics.Vector3(2f, 0f, 0f), parent: Root);
            var root = Shown(Root);
            var child = Shown(Child);

            Frames(60);
            Expect(Near(root.MeshInstance.Quaternion, AboutSlZ(MathF.PI / 2f)),
                $"root not turned a quarter after 1 s: {root.MeshInstance.Quaternion}");
            Expect(root.MeshInstance.Position.DistanceTo(RenderConfig.ToGodot(SelfTestRegion, at)) < 1e-3f,
                "the root moved instead of turning in place");
            var childExpected = RenderConfig.ToGodot(SelfTestRegion, at + new System.Numerics.Vector3(0f, 2f, 0f));
            Expect(child.MeshInstance.Position.DistanceTo(childExpected) < 1e-3f,
                $"child not carried round the root: {child.MeshInstance.Position} vs {childExpected}");
            Expect(root.SpinDriven && child.SpinDriven, "turning prims not marked spin-driven");
            var childWorld = world.GetEntity(SelfTestRegion, Child)!.GetComponent<TransformComponent>()!.Position;
            Expect(System.Numerics.Vector3.Distance(childWorld, at + new System.Numerics.Vector3(2f, 0f, 0f)) < 1e-4f,
                "the world model was changed -- the turn must be visual only");

            // An update that leaves rotation and omega alone (a texture change, say), handled by
            // UpdateVisual: drawn where the spin has it, not snapped back to the server's rotation.
            var before = root.MeshInstance.Quaternion;
            Send(Root, at, omega: spinZ);
            Expect(Near(root.MeshInstance.Quaternion, before), "an unrelated update snapped the root back for a frame");

            // The swing: the omega flips, the turn is kept, and the prim goes back the way it came.
            Send(Root, at, omega: -spinZ);
            Expect(Near(root.MeshInstance.Quaternion, before), "a flipped omega snapped the root back");
            Frames(30);
            Expect(Near(root.MeshInstance.Quaternion, AboutSlZ(MathF.PI / 4f)),
                $"flipped omega did not swing back to an eighth: {root.MeshInstance.Quaternion}");

            // Instancing: a prim in a MultiMesh group that starts turning leaves it, and stays out.
            if (_instanceGroups != null)
            {
                Send(Other, at + new System.Numerics.Vector3(5f, 0f, 0f));
                Send(Twin, at + new System.Numerics.Vector3(8f, 0f, 0f));
                var other = Shown(Other);
                var twin = Shown(Twin);
                other.MeshInstance.Mesh = new BoxMesh();
                twin.MeshInstance.Mesh = other.MeshInstance.Mesh;
                var key = new InstanceGroupKey(Guid.NewGuid(), "target-omega", true);
                var material = new StandardMaterial3D();
                _instanceGroups.Join(other.EntityId, key, other.MeshInstance.Mesh, material, other.MeshInstance.Transform);
                _instanceGroups.Join(twin.EntityId, key, twin.MeshInstance.Mesh, material, twin.MeshInstance.Transform);
                _instanceGroups.FlushRealise();
                Expect(_instanceGroups.IsInstanced(other.EntityId), "setup: the pair was not instanced");

                Send(Other, at + new System.Numerics.Vector3(5f, 0f, 0f), omega: spinZ);
                Frames(1);
                Expect(!_instanceGroups.IsInstanced(other.EntityId), "a turning prim stayed in its MultiMesh group");
                Expect(other.MeshInstance.Mesh != null, "a turning prim left its group without its mesh back");
                EvaluateInstancing(other, NowSeconds());
                Expect(!_instanceGroups.IsTracked(other.EntityId), "the cull sweep put a turning prim back in a group");
                _instanceGroups.Leave(twin.EntityId);
            }

            // Stopping with an uncompressed zero omega: back where the simulator has it, off the list.
            Send(Root, at, resetsSpin: true);
            Frames(1);
            Expect(Near(root.MeshInstance.Quaternion, Godot.Quaternion.Identity),
                $"an uncompressed zero omega did not put the root back: {root.MeshInstance.Quaternion}");
            Expect(child.MeshInstance.Position.DistanceTo(RenderConfig.ToGodot(SelfTestRegion, at + new System.Numerics.Vector3(2f, 0f, 0f))) < 1e-3f,
                "the child was not put back with its root");
            Expect(!root.SpinDriven, "a stopped root still marked spin-driven");

            // Cost: one turning root carrying Extras children, all shown.
            Send(Root, at, omega: spinZ);
            for (uint i = 0; i < Extras; i++)
            {
                Send(FirstExtra + i, new System.Numerics.Vector3(1f + i * 0.01f, 0f, 0f), parent: Root);
                Shown(FirstExtra + i);
            }
            Frames(5);
            const int Measured = 120;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int f = 0; f < Measured; f++) TickTargetOmega();
            double us = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            int driven = simulation.SpinDrivenEntities.Count;
            double perPrim = us / Measured / Math.Max(1, driven);
            costDetail = $"{driven} spin-driven prims: {us / Measured:0} us/frame, {perPrim:0.00} us/prim";
            Expect(driven >= Extras + 2, $"only {driven} prims on the per-frame list");
            // A generous ceiling: this only has to catch something pathological (an allocation per
            // prim, a scan of every visual), not measure the machine.
            Expect(perPrim < 25.0, $"the per-frame pass is too expensive: {costDetail}");
        }
        catch (Exception ex)
        {
            failures.Add($"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SpinDriven = null;
            foreach (var entity in new List<Entity>(world.GetAllEntities()))
            {
                if (_visuals.ContainsKey(entity.Id)) RemoveVisual(entity.Id);
            }
            Settle();
        }

        return failures.Count == 0
            ? (true, "root and child drawn turned, world model untouched, no snap on update, swing reverses, " +
                     $"evicted from instancing, zero omega restores; {costDetail}")
            : (false, string.Join("; ", failures));
    }

    /// <summary>The world events a self-test feeds in by hand; nothing else is raised.</summary>
    private sealed class SelfTestEventSource : IWorldEventSource
    {
#pragma warning disable CS0067 // the interface's other events are never raised here
        public event EventHandler<ObjectUpdateEvent>? ObjectUpdateReceived;
        public event EventHandler<AvatarUpdateEvent>? AvatarUpdateReceived;
        public event EventHandler<ObjectRemovedEvent>? ObjectRemovedReceived;
        public event EventHandler<ObjectPropertiesEvent>? ObjectPropertiesReceived;
        public event EventHandler<PhysicsPropertiesEvent>? PhysicsPropertiesReceived;
        public event EventHandler<ObjectMediaEvent>? ObjectMediaReceived;
        public event EventHandler<ObjectAnimationEvent>? ObjectAnimationReceived;
        public event EventHandler<TerrainPatchEvent>? TerrainPatchReceived;
        public event EventHandler<TerrainSettingsEvent>? TerrainSettingsReceived;
        public event EventHandler<RegionDisconnectedEvent>? RegionDisconnectedReceived;
        public event EventHandler<AvatarAppearanceEvent>? AvatarAppearanceReceived;
        public event EventHandler<AvatarAnimationEvent>? AvatarAnimationReceived;
        public event EventHandler<NameResolvedEvent>? NameResolved;
        public event EventHandler<NameResolvedEvent>? DisplayNameResolved;
        public event EventHandler<TeleportProgressEvent>? TeleportProgressReceived;
        public event EventHandler<GltfMaterialOverrideEvent>? GltfMaterialOverrideReceived;
#pragma warning restore CS0067

        public void Raise(ObjectUpdateEvent e) => ObjectUpdateReceived?.Invoke(this, e);
    }
}
