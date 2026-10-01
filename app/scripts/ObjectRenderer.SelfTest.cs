using System;
using System.Collections.Generic;
using Godot;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App;

// FEAT-ANIMESH-01 -- the headless proof that the object renderer hands the right prims to the
// control avatar in every order the world can deliver them. Kept out of ObjectRenderer.cs: it is
// only ever called by SelfTest, and that file is already 285 KB.

public partial class ObjectRenderer
{
    /// <summary>
    /// Drives real entities through the real event flow (EntityAdded, ComponentUpdated, the work
    /// queue) with no asset service, so no mesh is ever fetched: a decoded mesh is delivered by hand,
    /// through the same method the asset callback uses. What it pins down is the part a unit test
    /// cannot reach and an in-world run only shows as "the robot is lying down" -- WHO is skinned by
    /// a control avatar and who is not, in each arrival order, and that nothing is left behind.
    /// </summary>
    internal (bool Passed, string Detail) SelfTestAnimeshOrders(World world, AvatarRenderer avatars)
    {
        const ulong Region = 11437119954698752UL;
        var failures = new List<string>();
        void Expect(bool ok, string what) { if (!ok) failures.Add(what); }
        void Settle() => MainThreadWorkQueue.Pump(double.MaxValue);

        var rigged = avatars.SelfTestRiggedBar(0.5f, out _);
        // A different instance of the same thing -- how another LOD of one asset arrives.
        var riggedLod = avatars.SelfTestRiggedBar(0.5f, out _);
        var plain = new MeshData(new[]
        {
            new MeshSubmesh(
                new[] { System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY },
                new[] { System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitZ },
                new[] { System.Numerics.Vector2.Zero, System.Numerics.Vector2.UnitX, System.Numerics.Vector2.UnitY },
                new[] { 0, 1, 2 }),
        });

        Entity AddPrim(uint localId, uint parentLocalId, bool animated)
        {
            var e = world.GetOrCreateEntity(Region, localId);
            e.SetComponent(new TransformComponent(new System.Numerics.Vector3(128f, 128f, 25f), System.Numerics.Quaternion.Identity)
            {
                ParentLocalId = parentLocalId,
            });
            var prim = new PrimitiveComponent(System.Numerics.Vector3.One, 0, isMesh: true, meshId: Guid.NewGuid())
            {
                IsAnimatedMesh = animated,
            };
            e.SetComponent(prim);
            world.NotifyComponentUpdated(e, prim);
            Settle();                                   // CreateVisual, then UpdateVisual
            _visuals[e.Id].ResourcesReleased = false;   // in range -- the cull sweep does this in the client
            return e;
        }

        void Arrive(Entity e, MeshData data)
        {
            var state = _visuals[e.Id];
            var meshId = e.GetComponent<PrimitiveComponent>()!.MeshId;
            state.LoadedMeshId = meshId;
            state.LoadedMeshDetailLevel = MeshDetailLevel.Highest;
            ApplyArrivedMesh(state, meshId, MeshDetailLevel.Highest, data);
            Settle();
        }

        void SetFlag(Entity e, bool on)
        {
            var prim = e.GetComponent<PrimitiveComponent>()!;
            prim.IsAnimatedMesh = on;
            world.NotifyComponentUpdated(e, prim);
            Settle();
        }

        bool Drawn(Entity e) => _visuals[e.Id].MeshInstance.Mesh != null;
        bool Owned(Entity e) => _visuals[e.Id].ControlAvatarOwned;
        bool NoCollision(Entity e) => _visuals[e.Id].CollisionShape.Shape == null;

        var made = new List<Entity>();
        Entity Add(uint localId, uint parent, bool animated)
        {
            var e = AddPrim(localId, parent, animated);
            made.Add(e);
            return e;
        }

        try
        {
            // An ordinary rigged mesh on the ground is a static mesh -- in the viewer too.
            var r1 = Add(1, 0, animated: false);
            Arrive(r1, rigged);
            Expect(Drawn(r1) && !Owned(r1) && avatars.SelfTestControlAvatarCount == 0,
                "an unflagged rigged mesh must stay a plain static mesh");

            // (b) the mesh is already assigned statically, THEN the flag turns up.
            SetFlag(r1, true);
            Expect(!Drawn(r1) && Owned(r1) && NoCollision(r1) && avatars.SelfTestPartCount(r1.Id) == 1,
                $"(b) a flag arriving after the mesh must tear the static mesh down and skin it (drawn={Drawn(r1)} owned={Owned(r1)} parts={avatars.SelfTestPartCount(r1.Id)})");

            // (c) and the flag goes away again.
            SetFlag(r1, false);
            Expect(Drawn(r1) && !Owned(r1) && avatars.SelfTestPartCount(r1.Id) == -1,
                $"(c) a flag going away must restore the static mesh and free the skeleton (drawn={Drawn(r1)} owned={Owned(r1)} parts={avatars.SelfTestPartCount(r1.Id)})");

            // (a) the flag is known before the mesh arrives.
            var r2 = Add(2, 0, animated: true);
            Arrive(r2, rigged);
            Expect(!Drawn(r2) && Owned(r2) && NoCollision(r2) && avatars.SelfTestPartCount(r2.Id) == 1,
                $"(a) a flagged root must never get a static mesh (drawn={Drawn(r2)} owned={Owned(r2)} parts={avatars.SelfTestPartCount(r2.Id)})");

            // A re-assignment (another LOD) replaces the part; it never adds one.
            Arrive(r2, riggedLod);
            Expect(avatars.SelfTestPartCount(r2.Id) == 1 && avatars.SelfTestControlAvatarCount == 1,
                $"a second mesh for the same prim must replace the first (parts={avatars.SelfTestPartCount(r2.Id)})");

            // Only the ROOT's flag counts, and a child's mesh moves when the root's flag flips even
            // though nothing about the child changed. The (non-rigged) root itself stays static.
            var r3 = Add(3, 0, animated: false);
            Arrive(r3, plain);
            var c4 = Add(4, 3, animated: false);
            Arrive(c4, rigged);
            Expect(Drawn(c4) && !Owned(c4), "a rigged child of an unflagged root must stay static");
            SetFlag(r3, true);
            Expect(!Drawn(c4) && Owned(c4) && Drawn(r3) && !Owned(r3) && avatars.SelfTestPartCount(r3.Id) == 1,
                $"flagging the root must skin its rigged child and leave a non-rigged root alone (child drawn={Drawn(c4)} owned={Owned(c4)}, root drawn={Drawn(r3)} owned={Owned(r3)}, parts={avatars.SelfTestPartCount(r3.Id)})");
            SetFlag(r3, false);
            Expect(Drawn(c4) && !Owned(c4) && avatars.SelfTestPartCount(r3.Id) == -1,
                $"unflagging the root must give the child its static mesh back (child drawn={Drawn(c4)} owned={Owned(c4)})");

            // A flagged child is not animesh: the viewer ignores the block on a child.
            var r8 = Add(8, 0, animated: false);
            Arrive(r8, plain);
            var c9 = Add(9, 8, animated: true);
            Arrive(c9, rigged);
            Expect(Drawn(c9) && !Owned(c9) && avatars.SelfTestPartCount(r8.Id) == -1,
                "a child's own animated-mesh flag must count for nothing");

            // A flagged root whose mesh is not rigged is drawn static -- and says so, once.
            var r5 = Add(5, 0, animated: true);
            Arrive(r5, plain);
            Expect(Drawn(r5) && !Owned(r5) && _animeshNotRiggedWarned.Contains(r5.Id),
                "a flagged root with an unrigged mesh must stay static and be reported");

            // One skeleton per ROOT: the root's mesh and a rigged child share it.
            var r6 = Add(6, 0, animated: true);
            Arrive(r6, rigged);
            var c7 = Add(7, 6, animated: false);
            Arrive(c7, rigged);
            Expect(avatars.SelfTestPartCount(r6.Id) == 2 && Owned(r6) && Owned(c7),
                $"a root and its rigged child must share one control avatar (parts={avatars.SelfTestPartCount(r6.Id)})");

            // Out of range: the prim lets go of the mesh, so the skeleton must go too ...
            ReleaseResources(_visuals[r2.Id]);
            Expect(avatars.SelfTestPartCount(r2.Id) == -1 && !Owned(r2),
                $"a distant animesh must not keep its skeleton (parts={avatars.SelfTestPartCount(r2.Id)} owned={Owned(r2)})");
            // ... and it comes back when the mesh is reloaded on the way in.
            _visuals[r2.Id].ResourcesReleased = false;
            Arrive(r2, rigged);
            Expect(Owned(r2) && avatars.SelfTestPartCount(r2.Id) == 1, "an animesh coming back into range must be skinned again");

            // Derez: the last prim takes the skeleton with it, a middle one only its own share.
            RemoveVisual(c7.Id.ToString());
            Expect(avatars.SelfTestPartCount(r6.Id) == 1, $"removing one prim must leave the object's skeleton to the others (parts={avatars.SelfTestPartCount(r6.Id)})");
            RemoveVisual(r6.Id.ToString());
            Expect(avatars.SelfTestPartCount(r6.Id) == -1, "removing the last prim must free the skeleton");

            foreach (var e in made)
                if (_visuals.ContainsKey(e.Id)) RemoveVisual(e.Id.ToString());
            Settle();
            Expect(avatars.SelfTestControlAvatarCount == 0,
                $"nothing may outlive its objects ({avatars.SelfTestControlAvatarCount} control avatar(s) left)");
        }
        finally
        {
            foreach (var e in made)
                if (_visuals.ContainsKey(e.Id)) RemoveVisual(e.Id.ToString());
        }

        return failures.Count == 0
            ? (true, "flag known first, flag after the mesh, flag removed, child follows its root's flag, a child's own flag ignored, " +
                     "unrigged flagged root reported, one skeleton per root, LOD swap replaces, out-of-range and derez free it")
            : (false, string.Join("; ", failures));
    }
}
