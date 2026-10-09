using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

    private const ulong SelfTestRegion = 11437119954698752UL;

    private Entity SelfTestAddPrim(World world, uint localId, uint parentLocalId, bool animated)
    {
        var e = world.GetOrCreateEntity(SelfTestRegion, localId);
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
        MainThreadWorkQueue.Pump(double.MaxValue);   // CreateVisual, then UpdateVisual
        var state = _visuals[e.Id];
        state.ResourcesReleased = false;             // in range -- the cull sweep does this in the client
        state.MeshInstance.Visible = true;           // and shows it: a hidden prim's skeleton is not advanced
        return e;
    }

    private void SelfTestArrive(Entity e, MeshData data)
    {
        var state = _visuals[e.Id];
        var meshId = e.GetComponent<PrimitiveComponent>()!.MeshId;
        state.LoadedMeshId = meshId;
        state.LoadedMeshDetailLevel = MeshDetailLevel.Highest;
        ApplyArrivedMesh(state, meshId, MeshDetailLevel.Highest, data);
        MainThreadWorkQueue.Pump(double.MaxValue);
    }

    /// <summary>
    /// FEAT-ANIMESH-02: the animations a linkset's prims signal reach its control avatar through the
    /// real event flow -- ObjectAnimation's component update, UpdateVisual, the avatar's per-frame
    /// pass -- and the avatar plays their UNION. The assets come from a synthetic loader.
    ///
    /// <para>What it pins down is the part that is invisible when it is wrong: a list that was
    /// waiting before the avatar existed, a plain child prim whose script starts the animation, the
    /// larger sequence id across prims, a prim that is unlinked, re-linked or derezzed, and the
    /// avatar going away. An object whose animation does not play looks exactly like an object
    /// nobody signalled.</para>
    /// </summary>
    internal (bool Passed, string Detail) SelfTestAnimeshAnimations(World world, AvatarRenderer avatars)
    {
        var failures = new List<string>();
        void Expect(bool ok, string what) { if (!ok) failures.Add(what); }
        void Settle() => MainThreadWorkQueue.Pump(double.MaxValue);
        // One frame to notice the change and ask for the assets, the queue to apply them, one more
        // frame to play: the client does the same over a few frames.
        void Pulse() { avatars.SelfTestTick(0.05f); Settle(); avatars.SelfTestTick(0.05f); }

        const string Shoulder = "mShoulderLeft";
        const string Elbow = "mElbowLeft";
        var keyA = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, MathF.PI / 3f);
        var keyB = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, -MathF.PI / 4f);
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var animA = AvatarRenderer.SelfTestAnimation(Shoulder, keyA, loop: true, length: 2f);
        var animB = AvatarRenderer.SelfTestAnimation(Elbow, keyB, loop: true, length: 2f);

        avatars.AnimationLoaderOverride = id =>
            Task.FromResult<AnimationData?>(id == idA ? animA : id == idB ? animB : null);
        // The world simulation's parent-to-children index, for a world that has no simulation.
        avatars.LinksetChildren = (region, localId) => world.GetAllEntities()
            .Where(e => e.RegionHandle == region && e.GetComponent<TransformComponent>()?.ParentLocalId == localId)
            .Select(e => e.Id).ToList();

        void Signal(Entity e, params (Guid id, int seq)[] list)
        {
            var prim = e.GetComponent<PrimitiveComponent>()!;
            prim.SignaledAnimations = list.Select(i => new SignaledAnimation(i.id, i.seq)).ToArray();
            world.NotifyComponentUpdated(e, prim);
            Settle();   // UpdateVisual
        }

        bool Set(Guid rootId, params (Guid id, int seq)[] expected) =>
            ControlAvatarAnimations.SameSet(avatars.SelfTestSignaled(rootId),
                expected.Select(i => new SignaledAnimation(i.id, i.seq)).ToArray());

        var rigged = avatars.SelfTestRiggedBar(0.5f, out _);
        var plain = new MeshData(new[]
        {
            new MeshSubmesh(
                new[] { System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY },
                new[] { System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitZ },
                new[] { System.Numerics.Vector2.Zero, System.Numerics.Vector2.UnitX, System.Numerics.Vector2.UnitY },
                new[] { 0, 1, 2 }),
        });

        var made = new List<Entity>();
        Entity Add(uint localId, uint parent, bool animated)
        {
            var e = SelfTestAddPrim(world, localId, parent, animated);
            made.Add(e);
            return e;
        }

        try
        {
            // A list that arrived before the avatar existed is read when it is built.
            var root = Add(1, 0, animated: true);
            Signal(root, (idA, 1));
            SelfTestArrive(root, rigged);
            Pulse();
            Expect(Set(root.Id, (idA, 1)) && avatars.SelfTestAnimationTime(root.Id, idA) != null
                   && avatars.SelfTestBoneAtKey(root.Id, Shoulder, keyA),
                "a list that was waiting before the avatar existed must play once it is built " +
                $"(signalled={avatars.SelfTestSignaled(root.Id).Length}, time={avatars.SelfTestAnimationTime(root.Id, idA)})");

            // A PLAIN child's script starts a second animation: the union plays both.
            var child = Add(2, 1, animated: false);
            SelfTestArrive(child, plain);
            Signal(child, (idB, 2));
            Pulse();
            Expect(Set(root.Id, (idA, 1), (idB, 2)) && avatars.SelfTestAnimationTime(root.Id, idB) != null
                   && avatars.SelfTestBoneAtKey(root.Id, Elbow, keyB),
                "an animation signalled by a plain child prim must play on the root's skeleton");

            // The same animation in two prims: the larger sequence id, and B stops with its prim's list.
            float timeA = avatars.SelfTestAnimationTime(root.Id, idA) ?? -1f;
            Signal(child, (idA, 4));
            Pulse();
            Expect(Set(root.Id, (idA, 4)) && avatars.SelfTestAnimationTime(root.Id, idB) == null,
                "the union must take the larger sequence id and drop what no prim signals any more");
            Expect((avatars.SelfTestAnimationTime(root.Id, idA) ?? -1f) > timeA,
                "an animation already playing must carry on, not restart, when its sequence id moves");

            // Gone from the root but still in the child.
            Signal(root);
            Pulse();
            Expect(Set(root.Id, (idA, 4)) && avatars.SelfTestAnimationTime(root.Id, idA) != null,
                "an animation one prim stopped signalling but another still does must keep playing");

            // Nobody signals anything: nothing plays, and the body stays where the last animation left
            // it (the viewer resets no joint; a joint nothing ever drove is still at rest).
            Signal(child);
            Pulse();
            Expect(Set(root.Id) && avatars.SelfTestAnimationTime(root.Id, idA) == null
                   && avatars.SelfTestBoneAtKey(root.Id, Shoulder, keyA)
                   && avatars.SelfTestBoneAtRest(root.Id, "mHipLeft"),
                "an object with nothing signalled must stop playing but hold its last pose");

            // One animation at a time, as the real robot's script cycles them: the shoulder animation
            // goes, an elbow-only one comes, and the shoulder STAYS where the first one put it.
            Signal(root, (idA, 1));
            Pulse();
            Signal(root, (idB, 1));
            Pulse();
            Expect(Set(root.Id, (idB, 1)) && avatars.SelfTestAnimationTime(root.Id, idA) == null
                   && avatars.SelfTestBoneAtKey(root.Id, Elbow, keyB) && avatars.SelfTestBoneAtKey(root.Id, Shoulder, keyA),
                "replacing one animation with another that drives fewer joints must leave the others where they were");
            Signal(root);
            Pulse();

            // Unlinking a child takes its animations with it; linking it back brings them back.
            Signal(child, (idB, 1));
            Pulse();
            Expect(Set(root.Id, (idB, 1)), "the child's animation must play before it is unlinked");
            var childTransform = child.GetComponent<TransformComponent>()!;
            childTransform.ParentLocalId = 0;
            world.NotifyComponentUpdated(child, childTransform);
            Settle();
            Pulse();
            Expect(Set(root.Id) && avatars.SelfTestAnimationTime(root.Id, idB) == null,
                "a child unlinked from the object must stop contributing to it");
            childTransform.ParentLocalId = 1;
            world.NotifyComponentUpdated(child, childTransform);
            Settle();
            Pulse();
            Expect(Set(root.Id, (idB, 1)) && avatars.SelfTestAnimationTime(root.Id, idB) != null,
                "a child linked back must contribute again");

            // Derezzing the child.
            world.RemoveEntity(SelfTestRegion, 2);
            RemoveVisual(child.Id.ToString());
            Pulse();
            Expect(Set(root.Id) && avatars.SelfTestAnimationTime(root.Id, idB) == null,
                "a removed child's animations must stop");

            // Derezzing the root takes the avatar, and a late tick finds nothing to do.
            Signal(root, (idA, 1));
            Pulse();
            Expect(avatars.SelfTestAnimationTime(root.Id, idA) != null, "the root's own animation must play again");
            world.RemoveEntity(SelfTestRegion, 1);
            RemoveVisual(root.Id.ToString());
            Pulse();
            Expect(avatars.SelfTestControlAvatarCount == 0, "removing the root must free the control avatar");
        }
        finally
        {
            avatars.AnimationLoaderOverride = null;
            avatars.LinksetChildren = null;
            foreach (var e in made)
                if (_visuals.ContainsKey(e.Id)) RemoveVisual(e.Id.ToString());
        }

        return failures.Count == 0
            ? (true, "a list waiting before the build, a plain child's script, the larger sequence id across prims, " +
                     "an animation held by another prim, a held pose when nothing is signalled and when a smaller animation replaces a bigger one, unlink and re-link, " +
                     "derezzing a child and the root")
            : (false, string.Join("; ", failures));
    }

    /// <summary>
    /// The click dump's live-material reader against a material built the way the terrace floor's is
    /// (legacy gloss 30, environment 5, a white specular map with a beige tint, an RGB normal map):
    /// it must read the real pixels back and predict a matte, not a mirror, surface.
    /// </summary>
    private static float PredictedRoughness(string line)
    {
        int at = line.LastIndexOf("roughness=", StringComparison.Ordinal);
        if (at < 0) return float.NaN;
        var rest = line[(at + "roughness=".Length)..].Split(' ')[0].Replace(',', '.');
        return float.TryParse(rest, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : float.NaN;
    }

    internal static (bool Passed, string Detail) SelfTestLiveMaterialDump()
    {
        var whiteImage = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        whiteImage.Fill(new Color(1, 1, 1, 1));
        var white = ImageTexture.CreateFromImage(whiteImage);
        var normalImage = Image.CreateEmpty(8, 8, false, Image.Format.Rgb8);
        normalImage.Fill(new Color(0.5f, 0.5f, 1f));
        var normal = ImageTexture.CreateFromImage(normalImage);

        var mat = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
        mat.SetShaderParameter(PrimShaderFamily.HasSpecularTexture, true);
        mat.SetShaderParameter(PrimShaderFamily.SpecularTexture, white);
        mat.SetShaderParameter(PrimShaderFamily.SpecularTint, new Godot.Vector3(0.87f, 0.80f, 0.72f));
        mat.SetShaderParameter(PrimShaderFamily.SpecularGlossiness, 30f / 255f);
        mat.SetShaderParameter(PrimShaderFamily.SpecularEnvironment, 5f / 255f);
        mat.SetShaderParameter(PrimShaderFamily.HasNormalTexture, true);
        mat.SetShaderParameter(PrimShaderFamily.NormalTexture, normal);

        string line = DescribeLiveMaterial(mat);
        bool ok = line.Contains("has_specular_texture=True") && line.Contains("specularTex=ImageTexture")
                  && line.Contains("1x1") && line.Contains("meanRGB=(1,1,1)")
                  && line.Contains("normalTex=ImageTexture") && line.Contains("alpha mean=1")
                  && PredictedRoughness(line) is > 0.4f and < 0.531f;
        return (ok, ok ? "reads the bound textures back and predicts a matte floor"
                       : "dump line was: " + line);
    }

    /// <summary>
    /// BUG-PERF-06: a static object's mesh is now usually prepared on a worker (surface arrays and
    /// trimesh faces) and only committed by AssignSharedMesh. Two objects with the same face records
    /// get the same mesh asset under different ids: one through a worker preparation, one built on
    /// the main thread the old way. Both must equal the build as it was before (kept below verbatim)
    /// -- surface by surface, read back out of the engine -- with the same face list, the same merged
    /// grouping (faces 0 and 1 share a record), and a collision shape of exactly the trimesh faces.
    /// </summary>
    internal (bool Passed, string Detail) SelfTestPreparedStaticMesh(World world)
    {
        var failures = new List<string>();
        void Expect(bool ok, string what) { if (!ok) failures.Add(what); }
        void Settle() => MainThreadWorkQueue.Pump(double.MaxValue);

        var stone = default(FaceTexture) with { TextureId = Guid.Parse("5e1f7e57-0000-0000-0000-000000000001") };
        var moss = default(FaceTexture) with { TextureId = Guid.Parse("5e1f7e57-0000-0000-0000-000000000002") };
        var faceRecords = new[] { stone, stone, moss };
        var data = SelfTestStaticMesh();

        var made = new List<Entity>();
        Entity Add(uint localId)
        {
            var e = SelfTestAddPrim(world, localId, 0, animated: false);
            made.Add(e);
            var prim = e.GetComponent<PrimitiveComponent>()!;
            prim.Faces = faceRecords;
            world.NotifyComponentUpdated(e, prim);
            Settle();
            return e;
        }

        try
        {
            var a = Add(901);
            var b = Add(902);
            var stateA = _visuals[a.Id];
            var stateB = _visuals[b.Id];
            var meshIdA = a.GetComponent<PrimitiveComponent>()!.MeshId;

            var prepared = PrepareArrivedMeshAsync(data, flipV: true, CapturePlanInputs(stateA),
                KeyForMesh(meshIdA, MeshDetailLevel.Highest), a.Id).GetAwaiter().GetResult();
            Expect(prepared != null, "nothing was prepared");
            stateA.LoadedMeshId = meshIdA;
            stateA.LoadedMeshDetailLevel = MeshDetailLevel.Highest;
            ApplyArrivedMesh(stateA, meshIdA, MeshDetailLevel.Highest, data, prepared);
            Settle();
            SelfTestArrive(b, data);   // no preparation: the main-thread build
            // Without a preparation the faces for a distant object are made by a background task;
            // its shape lands with a later queue item.
            var waitClock = System.Diagnostics.Stopwatch.StartNew();
            while (_collisionWaiters.Count > 0 && waitClock.ElapsedMilliseconds < 5000)
            {
                System.Threading.Thread.Sleep(5);
                Settle();
            }

            var runStart = new[] { true, false, true };
            Expect(prepared == null || prepared.RunStart.AsSpan().SequenceEqual(runStart),
                $"the worker planned [{(prepared == null ? "" : string.Join(",", prepared.RunStart))}], expected [{string.Join(",", runStart)}]");
            var legacy = LegacyBuildArrayMesh(data, flipV: true, runStart, out var legacyFaces);

            foreach (var (name, state) in new[] { ("prepared", stateA), ("main-thread", stateB) })
            {
                if (state.MeshInstance.Mesh is not ArrayMesh mesh)
                {
                    failures.Add($"{name}: no mesh");
                    continue;
                }
                if (mesh.GetSurfaceCount() != legacy.GetSurfaceCount())
                    failures.Add($"{name}: {mesh.GetSurfaceCount()} surfaces, the old build made {legacy.GetSurfaceCount()}");
                for (int s = 0; s < Math.Min(mesh.GetSurfaceCount(), legacy.GetSurfaceCount()); s++)
                {
                    var x = mesh.SurfaceGetArrays(s);
                    var y = legacy.SurfaceGetArrays(s);
                    foreach (var slot in new[] { Mesh.ArrayType.Vertex, Mesh.ArrayType.Normal, Mesh.ArrayType.Tangent,
                                                 Mesh.ArrayType.TexUV, Mesh.ArrayType.Index })
                    {
                        bool same = slot switch
                        {
                            Mesh.ArrayType.Vertex or Mesh.ArrayType.Normal => x[(int)slot].AsVector3Array().AsSpan().SequenceEqual(y[(int)slot].AsVector3Array()),
                            Mesh.ArrayType.Tangent => x[(int)slot].AsFloat32Array().AsSpan().SequenceEqual(y[(int)slot].AsFloat32Array()),
                            Mesh.ArrayType.TexUV => x[(int)slot].AsVector2Array().AsSpan().SequenceEqual(y[(int)slot].AsVector2Array()),
                            _ => x[(int)slot].AsInt32Array().AsSpan().SequenceEqual(y[(int)slot].AsInt32Array()),
                        };
                        if (!same) failures.Add($"{name}: surface {s} {slot} differs from the old build");
                    }
                    if (mesh.SurfaceGetFormat(s) != legacy.SurfaceGetFormat(s))
                        failures.Add($"{name}: surface {s} format {mesh.SurfaceGetFormat(s)} vs {legacy.SurfaceGetFormat(s)}");
                }
                if (!_meshFaceIndices.TryGetValue(state.LoadedMeshKey, out var faceList) || !faceList.AsSpan().SequenceEqual(legacyFaces))
                    failures.Add($"{name}: face list differs from the old build");
                if (state.CollisionShape.Shape is not ConcavePolygonShape3D shape
                    || !shape.Data.AsSpan().SequenceEqual(BuildTrimeshFaces(data)))
                    failures.Add($"{name}: collision shape missing or not the trimesh faces");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            foreach (var e in made)
                if (_visuals.ContainsKey(e.Id)) RemoveVisual(e.Id.ToString());
            Settle();
        }

        return failures.Count == 0
            ? (true, "worker-prepared and main-thread builds both equal the old build (2 merged surfaces, faces, collision)")
            : (false, string.Join("; ", failures));
    }

    /// <summary>
    /// BUG-PERF-11: the collision shapes of objects within reach of the avatar. One touching it is built
    /// on the spot; the rest are queued and built nearest first, one per frame when the budget is
    /// nothing, and an object that goes away while its shape is queued costs no shape at all. The node
    /// copies the cull sweep reads (<c>Pos</c>, <c>Scl</c>) match the node they stand for.
    /// </summary>
    internal (bool Passed, string Detail) SelfTestUrgentCollisionQueue(World world)
    {
        var failures = new List<string>();
        void Expect(bool ok, string what) { if (!ok) failures.Add(what); }
        void Settle() => MainThreadWorkQueue.Pump(double.MaxValue);

        double savedBudget = RenderConfig.CollisionUrgentFrameBudgetMs;
        var data = SelfTestStaticMesh();
        var made = new List<Entity>();
        try
        {
            // A at 1 m touches the avatar; B..E are queued and, set up out of order, must come out by distance.
            float[] distances = { 1f, 16f, 5f, 20f, 9f, 12f };
            foreach (var _ in distances) made.Add(SelfTestAddPrim(world, 950 + (uint)made.Count, 0, animated: false));
            var states = made.Select(e => _visuals[e.Id]).ToList();

            var origin = states[0].Pos;
            _agentPos = origin;
            _agentPosKnown = true;

            for (int i = 0; i < states.Count; i++)
            {
                var st = states[i];
                Expect(st.Pos.IsEqualApprox(st.MeshInstance.Position), $"prim {i}: Pos is not the node's Position");
                Expect(st.Scl.IsEqualApprox(st.MeshInstance.Scale), $"prim {i}: Scl is not the node's Scale");
                st.Pos = origin + new Godot.Vector3(distances[i], 0f, 0f);
                st.MeshInstance.Position = st.Pos;
            }

            RenderConfig.CollisionUrgentFrameBudgetMs = 0; // nothing: one shape per tick, the nearest
            foreach (var e in made) SelfTestArrive(e, data);

            Expect(states[0].CollisionShape.Shape is ConcavePolygonShape3D, "the object touching the avatar has no shape yet");
            for (int i = 1; i < states.Count; i++)
                Expect(states[i].CollisionShape.Shape == null && states[i].CollisionDeferred,
                       $"prim {i} at {distances[i]} m was built on the spot or lost its place in the queue");

            // 5 m (index 2), 9 m (4), 12 m (5), 16 m (1), 20 m (3).
            int[] expectedOrder = { 2, 4, 5, 1, 3 };
            for (int step = 0; step < expectedOrder.Length; step++)
            {
                TickUrgentShapes();
                for (int i = 1; i < states.Count; i++)
                {
                    bool shouldHave = Array.IndexOf(expectedOrder, i) <= step;
                    bool has = states[i].CollisionShape.Shape != null;
                    if (has != shouldHave)
                        failures.Add($"after tick {step + 1}: prim {i} ({distances[i]} m) {(has ? "has" : "lacks")} a shape, expected the opposite");
                }
            }
            Expect(_urgentShapes.Count == 0, "the queue is not empty after every shape was built");
            var built = states[2].CollisionShape.Shape as ConcavePolygonShape3D;
            Expect(built != null && built.Data.AsSpan().SequenceEqual(BuildTrimeshFaces(data)),
                   "a queued shape is not the trimesh faces of its mesh");
            Expect(states.All(s => !s.CollisionDeferred), "an object is still marked as waiting for its shape");

            // One that goes away while queued: no shape for it.
            var later = SelfTestAddPrim(world, 990, 0, animated: false);
            made.Add(later);
            var laterState = _visuals[later.Id];
            laterState.Pos = origin + new Godot.Vector3(7f, 0f, 0f);
            laterState.MeshInstance.Position = laterState.Pos;
            SelfTestArrive(later, data);
            var laterKey = laterState.LoadedGeometryKey;
            Expect(_urgentShapes.ContainsKey(laterKey), "the object near the avatar was not queued");
            RemoveVisual(later.Id.ToString());
            Settle();
            TickUrgentShapes();
            Expect(!_urgentShapes.ContainsKey(laterKey), "the queue still holds a shape nobody is waiting for");
            Expect(!_meshCollisionShapes.ContainsKey(laterKey), "a shape was built for an object that was gone");
        }
        catch (Exception ex)
        {
            failures.Add($"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            RenderConfig.CollisionUrgentFrameBudgetMs = savedBudget;
            _agentPosKnown = false;
            foreach (var e in made)
                if (_visuals.ContainsKey(e.Id)) RemoveVisual(e.Id.ToString());
            Settle();
        }

        return failures.Count == 0
            ? (true, "touching object solid at once, five more built nearest first one per tick, a vanished object costs no shape")
            : (false, string.Join("; ", failures));
    }

    /// <summary>
    /// BUG-PERF-11: the cull sweep's slice. One frame after a stall must not be answered with a slice that is
    /// most of the pass (the frame time it is sized from is capped), and a slice that has run out of time stops
    /// and leaves the rest to the next frames - which still get through the whole pass.
    /// </summary>
    internal (bool Passed, string Detail) SelfTestCullSlice(World world)
    {
        var failures = new List<string>();
        void Expect(bool ok, string what) { if (!ok) failures.Add(what); }
        void Settle() => MainThreadWorkQueue.Pump(double.MaxValue);

        double savedBudget = RenderConfig.CullSliceBudgetMs;
        var made = new List<Entity>();
        try
        {
            var agent = world.GetOrCreateEntity(SelfTestRegion, 1);
            agent.SetComponent(new AvatarComponent(Guid.NewGuid(), "Self", "Resident", isLocalAgent: true));
            agent.SetComponent(new TransformComponent(new System.Numerics.Vector3(128f, 128f, 25f), System.Numerics.Quaternion.Identity));
            const int Count = 400;
            for (int i = 0; i < Count; i++) made.Add(SelfTestAddPrim(world, 2000 + (uint)i, 0, animated: false));
            Settle();
            int total = _visuals.Count;

            // A 10 s frame: a slice of 0.1 s worth (40% of the pass), not 40 passes.
            _cullOrder.Clear();
            _cullCursor = 0;
            _cullCarry = 0;
            _cullOrder.AddRange(_visuals.Keys);
            _cullCursor = 0;
            _Process(10.0);
            Expect(_cullCursor > 0 && _cullCursor <= (int)(total * CullMaxDeltaSeconds / CullSweepSeconds) + 1,
                   $"a 10 s frame visited {_cullCursor} of {total} (expected about {total * CullMaxDeltaSeconds / CullSweepSeconds:0})");

            // No time at all: a slice stops at the first clock check (every CullClockEvery visits) but always moves.
            RenderConfig.CullSliceBudgetMs = 0;
            _cullCursor = 0;
            _cullCarry = 0;
            _Process(10.0);
            Expect(_cullCursor >= 1 && _cullCursor <= 2 * CullClockEvery,
                   $"a slice with no budget visited {_cullCursor} (expected 1 to {2 * CullClockEvery})");

            int frames = 0;
            while (_cullCursor < _cullOrder.Count && frames++ < 500) _Process(10.0);
            Expect(_cullCursor >= _cullOrder.Count, $"the pass did not finish in {frames} frames of an empty budget (at {_cullCursor} of {_cullOrder.Count})");
        }
        catch (Exception ex)
        {
            failures.Add($"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            RenderConfig.CullSliceBudgetMs = savedBudget;
            _agentPosKnown = false;
            foreach (var e in made)
                if (_visuals.ContainsKey(e.Id)) RemoveVisual(e.Id.ToString());
            Settle();
        }

        return failures.Count == 0
            ? (true, "a long frame no longer makes a long slice, a slice without time stops early and the pass still completes")
            : (false, string.Join("; ", failures));
    }

    /// <summary>
    /// BUG-PERF-11: verifies that:
    /// 1. LOD changes retain the first collision shape built for a mesh asset via _meshAssetCollisionShapes.
    /// 2. Entity removals are queued in _pendingRemovals and drained under a frame budget rather than freezing inline.
    /// 3. ObjectInstanceGroups batches group realisation via TickRealise outside the cull sweep.
    /// </summary>
    internal (bool Passed, string Detail) SelfTestMeshAssetCollisionAndRemovals(World world)
    {
        var failures = new List<string>();
        void Expect(bool ok, string what) { if (!ok) failures.Add(what); }
        void Settle() => MainThreadWorkQueue.Pump(double.MaxValue);

        var made = new List<Entity>();
        try
        {
            var agent = world.GetOrCreateEntity(SelfTestRegion, 1);
            agent.SetComponent(new AvatarComponent(Guid.NewGuid(), "Self", "Resident", isLocalAgent: true));
            agent.SetComponent(new TransformComponent(new System.Numerics.Vector3(128f, 128f, 25f), System.Numerics.Quaternion.Identity));
            _agentPos = new Godot.Vector3(128f, 128f, 25f);
            _agentPosKnown = true;

            var dataLow = SelfTestStaticMesh();
            var dataHigh = SelfTestStaticMesh();
            var meshId = Guid.NewGuid();

            // 1. First collision shape retained across LOD changes
            var e1 = SelfTestAddPrim(world, 3100, 0, animated: false);
            made.Add(e1);
            var st1 = _visuals[e1.Id];
            st1.Pos = _agentPos + new Godot.Vector3(1f, 0f, 0f);
            st1.MeshInstance.Position = st1.Pos;
            st1.LoadedMeshId = meshId;
            st1.LoadedMeshDetailLevel = MeshDetailLevel.Low;
            ApplyArrivedMesh(st1, meshId, MeshDetailLevel.Low, dataLow);
            Settle();

            var shape1 = st1.CollisionShape.Shape as ConcavePolygonShape3D;
            Expect(shape1 != null, "prim 1 has no collision shape after initial arrival");
            Expect(_meshAssetCollisionShapes.ContainsKey(meshId) && _meshAssetCollisionShapes[meshId] == shape1,
                   "initial shape was not recorded in _meshAssetCollisionShapes");

            // Switch LOD to Highest with different data: shape must be retained
            st1.LoadedMeshDetailLevel = MeshDetailLevel.Highest;
            ApplyArrivedMesh(st1, meshId, MeshDetailLevel.Highest, dataHigh);
            Settle();
            Expect(st1.CollisionShape.Shape == shape1,
                   "prim 1 replaced its collision shape on LOD change instead of retaining the first one");

            // A second prim with the same meshId also reuses the asset shape
            var e2 = SelfTestAddPrim(world, 3101, 0, animated: false);
            made.Add(e2);
            var st2 = _visuals[e2.Id];
            st2.Pos = _agentPos + new Godot.Vector3(2f, 0f, 0f);
            st2.MeshInstance.Position = st2.Pos;
            st2.LoadedMeshId = meshId;
            st2.LoadedMeshDetailLevel = MeshDetailLevel.Highest;
            ApplyArrivedMesh(st2, meshId, MeshDetailLevel.Highest, dataHigh);
            Settle();
            Expect(st2.CollisionShape.Shape == shape1,
                   "prim 2 did not reuse the existing asset collision shape");

            // 2. Pending removals are queued and drained under budget
            var e3 = SelfTestAddPrim(world, 3102, 0, animated: false);
            made.Add(e3);
            Settle();
            Expect(_visuals.ContainsKey(e3.Id), "prim 3 was not created");

            OnEntityRemoved(this, new EntityEventArgs(e3));
            Expect(_pendingRemovalSet.Contains(e3.Id), "prim 3 was not queued in _pendingRemovalSet");
            Expect(_visuals.ContainsKey(e3.Id), "prim 3 was removed synchronously instead of being deferred");

            ProcessPendingRemovals(maxBudgetMs: 5.0);
            Expect(!_visuals.ContainsKey(e3.Id), "prim 3 visual was not removed by ProcessPendingRemovals");
            Expect(!_pendingRemovalSet.Contains(e3.Id), "prim 3 remains in _pendingRemovalSet after processing");

            // 3. ObjectInstanceGroups batches group realisation via TickRealise
            if (_instanceGroups != null)
            {
                var sharedMesh = new BoxMesh();
                var sharedMat = new StandardMaterial3D();
                var key = new InstanceGroupKey(Guid.NewGuid(), "test_mat", true);

                var e4 = SelfTestAddPrim(world, 3103, 0, animated: false);
                var e5 = SelfTestAddPrim(world, 3104, 0, animated: false);
                made.Add(e4);
                made.Add(e5);
                Settle();

                _instanceGroups.Join(e4.Id, key, sharedMesh, sharedMat, Transform3D.Identity);
                _instanceGroups.Join(e5.Id, key, sharedMesh, sharedMat, Transform3D.Identity);

                Expect(!_instanceGroups.IsInstanced(e4.Id),
                       "group was realised inline in Join instead of waiting for TickRealise");

                _instanceGroups.TickRealise(maxGroups: 2, maxMs: 5.0);
                Expect(_instanceGroups.IsInstanced(e4.Id) && _instanceGroups.IsInstanced(e5.Id),
                       "group was not realised by TickRealise");

                _instanceGroups.Leave(e4.Id);
                _instanceGroups.Leave(e5.Id);
            }
        }
        catch (Exception ex)
        {
            failures.Add($"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _agentPosKnown = false;
            foreach (var e in made)
            {
                if (_pendingRemovalSet.Contains(e.Id)) _pendingRemovalSet.Remove(e.Id);
                if (_visuals.ContainsKey(e.Id)) RemoveVisual(e.Id);
            }
            Settle();
        }

        return failures.Count == 0
            ? (true, "mesh asset collision shapes retained over LODs, entity removals deferred, instance groups realised under budget")
            : (false, string.Join("; ", failures));
    }

    /// <summary>
    /// BUG-PERF-11: two things the budgeted sweep and the shared collision shapes must not break.
    /// (1) A released, hidden object far from the viewer is skipped by the sweep - and comes back when the
    /// viewer walks up to it, or jumps next to it, however far away it was when it was last looked at.
    /// (2) A shape shared across the LODs of one mesh asset is not shared across sculpts that use the same
    /// sculpt map with different stitching or mirror flags: those are different geometry.
    /// </summary>
    internal (bool Passed, string Detail) SelfTestDormantAndSculptShapes(World world)
    {
        var failures = new List<string>();
        void Expect(bool ok, string what) { if (!ok) failures.Add(what); }
        void Settle() => MainThreadWorkQueue.Pump(double.MaxValue);

        var made = new List<Entity>();
        // The draw distance is the user's preference, loaded at boot: the distances below assume the default.
        float savedDraw = RenderConfig.DrawDistance;
        RenderConfig.DrawDistance = 96f;
        try
        {
            var agent = world.GetOrCreateEntity(SelfTestRegion, 1);
            agent.SetComponent(new AvatarComponent(Guid.NewGuid(), "Self", "Resident", isLocalAgent: true));
            var agentAt = new TransformComponent(new System.Numerics.Vector3(128f, 128f, 25f), System.Numerics.Quaternion.Identity);
            agent.SetComponent(agentAt);

            // ---- (1) a far, released object comes back ------------------------------------------------
            var far = SelfTestAddPrim(world, 4000, 0, animated: false);
            made.Add(far);
            var farTransform = far.GetComponent<TransformComponent>()!;
            farTransform.Position = new System.Numerics.Vector3(128f + 230f, 128f, 25f);   // 230 m east
            world.NotifyComponentUpdated(far, farTransform);
            Settle();
            var farState = _visuals[far.Id];

            void Frames(int n) { for (int f = 0; f < n; f++) _Process(0.1); }
            Frames(60);
            Expect(farState.ResourcesReleased && !farState.Shown, $"an object 230 m away was not released and hidden by the sweep (released={farState.ResourcesReleased} shown={farState.Shown} pos={farState.Pos} agent={_agentPos} cursor={_cullCursor}/{_cullOrder.Count} visuals={_visuals.Count})");

            // Walking: 8 m a step, a few frames at each, until the object is 10 m away.
            for (float x = 128f; x <= 128f + 220f; x += 8f)
            {
                agentAt.Position = new System.Numerics.Vector3(x, 128f, 25f);
                Frames(4);
            }
            Expect(!farState.ResourcesReleased && farState.Shown,
                   "walking up to a released object 230 m away did not bring it back (released=" +
                   farState.ResourcesReleased + ", shown=" + farState.Shown + ")");

            // Teleporting: gone far away again, released and hidden, then one jump next to it.
            agentAt.Position = new System.Numerics.Vector3(128f - 800f, 128f, 25f);
            Frames(60);
            Expect(farState.ResourcesReleased && !farState.Shown, "the object was not released after the agent left");
            agentAt.Position = new System.Numerics.Vector3(128f + 225f, 128f, 25f);
            Frames(30);
            Expect(!farState.ResourcesReleased && farState.Shown, "a jump next to a released object did not bring it back");

            // A longer draw distance reaches an object that was out of range.
            agentAt.Position = new System.Numerics.Vector3(128f, 128f, 25f);
            Frames(60);
            Expect(farState.ResourcesReleased && !farState.Shown, "the object was not released again at 230 m");
            RenderConfig.DrawDistance = 300f;
            Frames(30);
            Expect(!farState.ResourcesReleased && farState.Shown, "raising the draw distance past a released object did not bring it back");
            RenderConfig.DrawDistance = 96f;

            // ---- (2) sculpts of one map with different flags keep their own shape -------------------------
            agentAt.Position = new System.Numerics.Vector3(128f, 128f, 25f);
            _agentPos = RenderConfig.ToGodot(SelfTestRegion, agentAt.Position);
            _agentPosKnown = true;
            var sculptId = Guid.NewGuid();
            var dataA = SelfTestStaticMesh();
            var dataB = new MeshData(dataA.Submeshes.Select(sm => sm with
            {
                Positions = sm.Positions.Select(p => new System.Numerics.Vector3(-p.X, p.Y, p.Z)).ToArray(),
            }).ToList());

            var a = SelfTestAddPrim(world, 4001, 0, animated: false);
            var b = SelfTestAddPrim(world, 4002, 0, animated: false);
            made.Add(a);
            made.Add(b);
            Settle();
            var stateA = _visuals[a.Id];
            var stateB = _visuals[b.Id];
            foreach (var (st, type) in new[] { (stateA, (byte)1), (stateB, (byte)0x41) })
            {
                st.Pos = _agentPos + new Godot.Vector3(1f, 0f, 0f);
                st.MeshInstance.Position = st.Pos;
                st.LoadedMeshId = sculptId;
                st.LoadedSculptType = type;
                st.LoadedMeshDetailLevel = null;   // a sculpt has no LOD ladder
                st.CollisionShape.Shape = null;
            }
            EnsureCollisionShape(stateA, KeyForSculpt(sculptId, 1), dataA);
            EnsureCollisionShape(stateB, KeyForSculpt(sculptId, 0x41), dataB);
            Expect(stateA.CollisionShape.Shape is ConcavePolygonShape3D sa
                   && sa.Data.AsSpan().SequenceEqual(BuildTrimeshFaces(dataA)), "the first sculpt has the wrong collision shape");
            Expect(stateB.CollisionShape.Shape is ConcavePolygonShape3D sb
                   && sb.Data.AsSpan().SequenceEqual(BuildTrimeshFaces(dataB)),
                   "a sculpt of the same map with other flags was given the first sculpt's collision shape");
        }
        catch (Exception ex)
        {
            failures.Add($"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            RenderConfig.DrawDistance = savedDraw;
            _agentPosKnown = false;
            foreach (var e in made)
                if (_visuals.ContainsKey(e.Id)) RemoveVisual(e.Id);
            Settle();
        }

        return failures.Count == 0
            ? (true, "a released object comes back by walking, by a jump and by a longer draw distance; sculpts with other flags keep their own shape")
            : (false, string.Join("; ", failures));
    }

    /// <summary>Four submeshes: faces 0 and 1 (same record, so merged), an empty one, face 2.</summary>
    private static MeshData SelfTestStaticMesh()
    {
        MeshSubmesh Quad(int face, float z, int[] indices) => new(
            new[]
            {
                new System.Numerics.Vector3(-0.5f, -0.5f, z), new System.Numerics.Vector3(0.5f, -0.5f, z + 0.1f),
                new System.Numerics.Vector3(0.5f, 0.5f, z), new System.Numerics.Vector3(-0.5f, 0.5f, z + 0.05f),
            },
            new[]
            {
                System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0.1f, 0f, 1f)),
                System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0f, 0.2f, 1f)),
            },
            new[]
            {
                new System.Numerics.Vector2(0f, 0f), new System.Numerics.Vector2(1f, 0.1f),
                new System.Numerics.Vector2(0.9f, 1f), new System.Numerics.Vector2(0.1f, 0.8f),
            },
            indices,
            face);

        return new MeshData(new[]
        {
            Quad(0, 0f, new[] { 0, 1, 2, 0, 2, 3 }),
            Quad(1, 0.3f, new[] { 0, 1, 2, 0, 2, 3 }),
            Quad(1, 0.4f, Array.Empty<int>()),
            Quad(2, 0.6f, new[] { 3, 2, 1, 3, 1, 0 }),
        });
    }

    /// <summary>ObjectRenderer.BuildArrayMesh as it was before BUG-PERF-06, kept unchanged as the
    /// oracle for the prepared and the main-thread build.</summary>
    private static ArrayMesh LegacyBuildArrayMesh(MeshData mesh, bool flipV, bool[] runStart, out int[] faceIndices,
        Func<string>? label = null)
    {
        var arrayMesh = new ArrayMesh();
        var indices = new List<int>(mesh.Submeshes.Count);
        // BUG-RENDER-40: a non-finite vertex value is repaired on its way into the SurfaceTool and the
        // mesh is named once, instead of Godot printing anonymous normalize warnings later.
        var guard = new MeshArrayGuard.VertexGuard();

        SurfaceTool? st = null;
        int runVertexBase = 0;
        int committable = -1;

        void FlushRun()
        {
            if (st == null) return;
            st.Commit(arrayMesh);
            st = null;
        }

        foreach (var sub in mesh.Submeshes)
        {
            if (sub.Indices.Length == 0) continue;

            committable++;
            // Defensive: a plan shorter than the committable submeshes would silently merge the
            // tail into whatever run preceded it, so treat a missing entry as "starts a surface".
            // Written as one condition rather than via a bool so the compiler's null analysis can
            // still see that st is non-null below.
            if (st == null || committable >= runStart.Length || runStart[committable])
            {
                FlushRun();
                st = new SurfaceTool();
                st.Begin(Mesh.PrimitiveType.Triangles);
                runVertexBase = 0;
                indices.Add(sub.FaceIndex);
            }

            // Tangents, computed in GODOT space and from the FINAL UVs -- both matter. The
            // positions below are swizzled from SL's Z-up and the V is conditionally flipped, and
            // a tangent basis derived from the pre-swizzle values would be rotated relative to the
            // vertices it is attached to.
            //
            // Godot cannot apply a normal map without these. SurfaceTool.GenerateTangents() used
            // to do the job and had to be turned off: it produced NaNs on the degenerate triangles
            // SL content is full of, and those reached the Vulkan driver. SLNG.Assets.MeshTangents
            // guarantees finite output instead of dividing by a zero-area UV triangle -- see its
            // tests for the exact family of inputs that crashed.
            var tangentPositions = new System.Numerics.Vector3[sub.Positions.Length];
            var tangentNormals = new System.Numerics.Vector3[sub.Positions.Length];
            var tangentUvs = new System.Numerics.Vector2[sub.Positions.Length];
            for (int i = 0; i < sub.Positions.Length; i++)
            {
                var sp = sub.Positions[i];
                var sn = sub.Normals[i];
                var suv = sub.UVs[i];
                tangentPositions[i] = new System.Numerics.Vector3(sp.X, sp.Z, -sp.Y);
                tangentNormals[i] = new System.Numerics.Vector3(sn.X, sn.Z, -sn.Y);
                tangentUvs[i] = new System.Numerics.Vector2(suv.X, flipV ? 1.0f - suv.Y : suv.Y);
            }
            // The winding is reversed below, so the tangent maths gets the same order the GPU
            // will see rather than the source order.
            var tangentIndices = new int[sub.Indices.Length];
            for (int t = 0; t + 2 < sub.Indices.Length; t += 3)
            {
                tangentIndices[t] = sub.Indices[t];
                tangentIndices[t + 1] = sub.Indices[t + 2];
                tangentIndices[t + 2] = sub.Indices[t + 1];
            }
            var tangents = SLNG.Assets.MeshTangents.Compute(
                tangentPositions, tangentNormals, tangentUvs, tangentIndices);

            // SL/OpenGL authors triangles CCW-front; Godot/Vulkan expects CW-front.
            // Reverse each triangle's winding by swapping its last two indices.
            // We supply the vertices once, then supply the reversed indices.
            for (int i = 0; i < sub.Positions.Length; i++)
            {
                var p = sub.Positions[i];
                var n = sub.Normals[i];
                var uv = sub.UVs[i];

                st.SetNormal(guard.Normal(new Godot.Vector3(n.X, n.Z, -n.Y), i));
                var tg = tangents[i];
                st.SetTangent(new Godot.Plane(tg.X, tg.Y, tg.Z, tg.W));
                st.SetUV(guard.Uv(new Godot.Vector2(uv.X, flipV ? 1.0f - uv.Y : uv.Y), i));
                st.AddVertex(guard.Position(new Godot.Vector3(p.X, p.Z, -p.Y), i));
            }

            // Shifted past whatever this run already holds. Zero unless a previous submesh was
            // merged into this same surface, so the un-merged path is unchanged arithmetic.
            int indexBase = runVertexBase;
            for (int t = 0; t + 2 < sub.Indices.Length; t += 3)
            {
                st.AddIndex(indexBase + sub.Indices[t]);
                st.AddIndex(indexBase + sub.Indices[t + 2]);
                st.AddIndex(indexBase + sub.Indices[t + 1]);
            }

            runVertexBase += sub.Positions.Length;
        }
        FlushRun();

        guard.Report(label ?? (() => "prim mesh (unlabelled)"));
        faceIndices = indices.ToArray();
        return arrayMesh;
    }
}
