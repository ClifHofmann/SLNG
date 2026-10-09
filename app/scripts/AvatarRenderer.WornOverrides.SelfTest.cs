using Godot;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SLNG.App;

public partial class AvatarRenderer
{
    /// <summary>
    /// BUG-AVATAR-10: an animated object worn on an avatar never changes that avatar's skeleton, and
    /// an ordinary rigged mesh gives its joint overrides back when it is taken off.
    ///
    /// <para>Drives the real worn path on a real <see cref="World"/> with a real avatar skeleton: the
    /// same rigged "creature" mesh (a hip joint overridden 0.3 m lower, its scale locked, a pelvis
    /// fixup -- the kind of skeleton a pet carries) is committed once as the root of an animated
    /// object and once as an ordinary attachment. The animated one must leave the wearer exactly as
    /// it was; the ordinary one must move the hip, and taking it off (<c>RemoveVisual</c>, what a
    /// detach runs) must put the hip back. Without the animated-root skip the first case fails;
    /// without the release on detach the last one does.</para>
    ///
    /// <para>Needs the renderer in a scene tree (node paths are resolved); nothing is fetched.</para>
    /// </summary>
    internal (bool Passed, string Detail) SelfTestWornJointOverrides()
    {
        LoadAvatarSkeleton();
        if (_avatarSkeleton == null) return (false, "the avatar skeleton did not load");
        const string Joint = "mHipLeft";
        var hipDef = _avatarSkeleton.GetBone(Joint);
        if (hipDef == null || hipDef.ParentName == null) return (false, $"{Joint} is not a child joint of the skeleton definition");

        const ulong Region = 11437119954698752UL;
        var priorWorld = _world;
        var world = new World();
        _world = world;

        AvatarVisual? visual = null;
        var wearer = world.GetOrCreateEntity(Region, 100);
        try
        {
            wearer.SetComponent(new AvatarComponent(Guid.NewGuid(), "Self", "Test", false));
            visual = new AvatarVisual { AgentId = wearer.GetComponent<AvatarComponent>()!.AgentId };
            AddChild(visual.Root);
            var skeleton = SkeletonBuilder.Build(_avatarSkeleton);
            skeleton.Name = "Skeleton3D";
            visual.Root.AddChild(skeleton);
            visual.Skeleton = skeleton;
            ApplyShape(visual, skeleton, _avatarSkeleton, visual.LastDistortions);
            skeleton.ResetBonePoses();
            _visuals[wearer.Id] = visual;

            int hipIdx = skeleton.FindBone(Joint);
            if (hipIdx < 0) return (false, $"{Joint} is not in the built skeleton");
            var restBefore = skeleton.GetBoneRest(hipIdx).Origin;

            // The creature: mHipLeft sits 0.3 m lower than the stock hip, the skin pins joint scale
            // (lock_scale_if_joint_position) and carries a pelvis offset. mPelvis is the joint the
            // bar's weights point at; the hip carries the override.
            const float Drop = 0.3f;
            var meshId = Guid.NewGuid();
            var bar = SelfTestRiggedBar(0.5f, out _);
            var pelvisDef = _avatarSkeleton.GetBone("mPelvis")!;
            var skin = new MeshSkin(
                new[] { "mPelvis", Joint },
                new[]
                {
                    System.Numerics.Matrix4x4.CreateTranslation(0f, 0f, -pelvisDef.Position.Z),
                    System.Numerics.Matrix4x4.Identity,
                },
                System.Numerics.Matrix4x4.Identity, 0.25f,
                new[]
                {
                    System.Numerics.Matrix4x4.CreateTranslation(pelvisDef.Position),
                    System.Numerics.Matrix4x4.CreateTranslation(hipDef.Position + new System.Numerics.Vector3(0f, 0f, -Drop)),
                },
                LockScaleIfJointPosition: true);
            var data = new MeshData(bar.Submeshes, skin);

            Guid Worn(uint localId, uint parentLocalId, bool animatedRoot)
            {
                var e = world.GetOrCreateEntity(Region, localId);
                e.SetComponent(new TransformComponent { ParentLocalId = parentLocalId });
                e.SetComponent(new PrimitiveComponent(System.Numerics.Vector3.One, profileCurve: 0) { IsAnimatedMesh = animatedRoot });
                e.SetComponent(new AttachmentComponent(wearer.Id, 1));
                return e.Id;
            }

            PendingRig Rig() => new(data, visual, skeleton, meshId, null, default, _avatarSkeleton, false);
            Godot.Vector3 Hip() => skeleton.GetBoneRest(hipIdx).Origin;
            string Mm(Godot.Vector3 v) => $"({v.X * 1000f:0}, {v.Y * 1000f:0}, {v.Z * 1000f:0}) mm";

            // 1. An animated object's mesh, worn: the wearer is left exactly as it was.
            var pet = Worn(101, parentLocalId: 100, animatedRoot: true);
            ApplyWornJointOverrides(pet, Rig());
            bool petLeavesWearer = visual.JointOverrides.MeshCount == 0
                                   && visual.JointPosOverrides.Count == 0
                                   && visual.JointScaleLocks.Count == 0
                                   && visual.PelvisFixups.Count == 0
                                   && visual.OverrideContributors.Count == 0
                                   && Hip().IsEqualApprox(restBefore);

            // 1b. The same holds for a child prim of that object: only the ROOT's flag decides.
            var petChild = Worn(102, parentLocalId: 101, animatedRoot: false);
            ApplyWornJointOverrides(petChild, Rig());
            bool petChildLeavesWearer = visual.JointOverrides.MeshCount == 0 && Hip().IsEqualApprox(restBefore);

            // 2. An ordinary rigged attachment with the identical mesh: its overrides DO apply.
            var body = Worn(103, parentLocalId: 100, animatedRoot: false);
            ApplyWornJointOverrides(body, Rig());
            var restWorn = Hip();
            bool bodyMovesHip = visual.JointOverrides.MeshCount == 1
                                && visual.JointScaleLocks.Contains(Joint)
                                && visual.PelvisFixups.Count == 1
                                && visual.OverrideContributors.TryGetValue(body, out var contributed) && contributed == meshId
                                && System.MathF.Abs(restWorn.Y - restBefore.Y) > 0.5f * Drop;

            // 3. Taking it off gives the hip back -- the fix for "deformed until a relog".
            _attachmentMeshIds[body] = (meshId, null, default, wearer.Id, MeshDetailLevel.Highest);
            RemoveVisual(body);
            bool detachReverts = visual.JointOverrides.MeshCount == 0
                                 && visual.JointPosOverrides.Count == 0
                                 && visual.JointScaleLocks.Count == 0
                                 && visual.PelvisFixups.Count == 0
                                 && visual.OverrideContributors.Count == 0
                                 && Hip().IsEqualApprox(restBefore);

            // 4. The overrides are the MESH's: two items wearing it, one taken off, keeps them.
            var bodyA = Worn(104, parentLocalId: 100, animatedRoot: false);
            var bodyB = Worn(105, parentLocalId: 100, animatedRoot: false);
            ApplyWornJointOverrides(bodyA, Rig());
            ApplyWornJointOverrides(bodyB, Rig());
            _attachmentMeshIds[bodyA] = (meshId, null, default, wearer.Id, MeshDetailLevel.Highest);
            _attachmentMeshIds[bodyB] = (meshId, null, default, wearer.Id, MeshDetailLevel.Highest);
            RemoveVisual(bodyA);
            bool sharedKept = visual.JointOverrides.MeshCount == 1 && !Hip().IsEqualApprox(restBefore);
            RemoveVisual(bodyB);
            bool sharedReleased = visual.JointOverrides.MeshCount == 0 && Hip().IsEqualApprox(restBefore);

            // 5. A root that turns out animated AFTER its overrides went in hands them back.
            var late = Worn(106, parentLocalId: 100, animatedRoot: false);
            ApplyWornJointOverrides(late, Rig());
            bool lateApplied = visual.JointOverrides.MeshCount == 1;
            world.GetEntity(late)!.GetComponent<PrimitiveComponent>()!.IsAnimatedMesh = true;
            ApplyWornJointOverrides(late, Rig());
            bool lateReleased = lateApplied && visual.JointOverrides.MeshCount == 0 && Hip().IsEqualApprox(restBefore);

            bool ok = petLeavesWearer && petChildLeavesWearer && bodyMovesHip && detachReverts
                      && sharedKept && sharedReleased && lateReleased;
            return (ok, ok
                ? $"an animated object's mesh leaves the wearer alone (root and child), an ordinary one moves {Joint} " +
                  $"{Mm(restWorn - restBefore)} and gives it back on detach, two items of one mesh share its overrides, " +
                  "a root flagged late hands them back"
                : $"animated mesh leaves the wearer {(petLeavesWearer ? "ok" : $"WRONG (hip {Mm(Hip() - restBefore)} off, {visual.JointOverrides.MeshCount} mesh(es) in the set)")}; " +
                  $"animated child {(petChildLeavesWearer ? "ok" : "WRONG")}; " +
                  $"ordinary mesh moves the hip {(bodyMovesHip ? "ok" : $"WRONG (moved {Mm(restWorn - restBefore)})")}; " +
                  $"detach reverts {(detachReverts ? "ok" : "WRONG")}; shared mesh kept {(sharedKept ? "ok" : "WRONG")} / released {(sharedReleased ? "ok" : "WRONG")}; " +
                  $"late animated flag {(lateReleased ? "ok" : "WRONG")}");
        }
        finally
        {
            _world = priorWorld;
            _attachmentMeshIds.Clear();
            if (visual != null)
            {
                _visuals.Remove(wearer.Id);
                if (IsInstanceValid(visual.Root)) visual.Root.QueueFree();
            }
        }
    }

    /// <summary>A bodyless-free wearer for the self-tests: an avatar entity with a real visual and
    /// skeleton, registered in <c>_visuals</c> the way CreateVisual leaves one.</summary>
    private AvatarVisual SelfTestWearer(Entity wearer)
    {
        wearer.SetComponent(new AvatarComponent(Guid.NewGuid(), "Self", "Test", false));
        var visual = new AvatarVisual { AgentId = wearer.GetComponent<AvatarComponent>()!.AgentId };
        AddChild(visual.Root);
        var skeleton = SkeletonBuilder.Build(_avatarSkeleton!);
        skeleton.Name = "Skeleton3D";
        visual.Root.AddChild(skeleton);
        visual.Skeleton = skeleton;
        ApplyShape(visual, skeleton, _avatarSkeleton!, visual.LastDistortions);
        skeleton.ResetBonePoses();
        _visuals[wearer.Id] = visual;
        return visual;
    }

    /// <summary>
    /// BUG-AVATAR-10: a worn animated object gets a control avatar of its own -- its mesh is skinned to
    /// a skeleton that carries the mesh's joint overrides, that skeleton is put where the wearer's
    /// attachment point is, plays the object's own animations, and lives and dies with the wearer and
    /// the attachment -- while the wearer's skeleton stays exactly as it was.
    ///
    /// <para>The creature is the dragon in miniature: a rig whose pelvis stands 0.145 m above the feet
    /// instead of 1.067 m, with a knee 0.2 m lower than a person's. Placement is checked against the
    /// viewer's own formula written out in SL terms (llcontrolavatar.cpp:182-193: the root prim's local
    /// position rotated by the attachment point's world rotation, plus the point's position; rotation
    /// <c>obj_rot * joint_rot</c>), not against the Godot product the renderer computes.</para>
    ///
    /// <para>Needs the renderer in a scene tree (node paths are resolved); nothing is fetched.</para>
    /// </summary>
    internal (bool Passed, string Detail) SelfTestWornAnimesh()
    {
        LoadAvatarSkeleton();
        if (_avatarSkeleton == null) return (false, "the avatar skeleton did not load");
        const string Knee = "mKneeLeft";
        const string Shoulder = "mShoulderLeft";
        var pelvisDef = _avatarSkeleton.GetBone("mPelvis");
        var kneeDef = _avatarSkeleton.GetBone(Knee);
        if (pelvisDef == null || pelvisDef.ParentName != null || kneeDef == null)
            return (false, "the skeleton definition is not the one this check is written against");

        var failures = new List<string>();
        void Expect(bool ok, string what) { if (!ok) failures.Add(what); }
        void Settle() => MainThreadWorkQueue.Pump(double.MaxValue);

        const ulong Region = 11437119954698752UL;
        var priorWorld = _world;
        var priorLoader = AnimationLoaderOverride;
        var world = new World();
        _world = world;

        var wearerEntity = world.GetOrCreateEntity(Region, 100);
        AvatarVisual? visual = null;
        AvatarVisual? rebuilt = null;
        try
        {
            visual = SelfTestWearer(wearerEntity);
            var skeleton = visual.Skeleton!;
            int wearerKnee = skeleton.FindBone(Knee);
            int wearerPelvis = skeleton.FindBone("mPelvis");
            var wearerKneeBefore = skeleton.GetBoneRest(wearerKnee).Origin;
            var wearerPelvisBefore = skeleton.GetBoneRest(wearerPelvis).Origin;

            // The creature: pelvis 0.145 m high, knee 0.2 m lower than stock, weighted to the pelvis.
            const float PetPelvisZ = 0.145f;
            const float KneeDrop = 0.2f;
            var meshId = Guid.NewGuid();
            var bar = SelfTestRiggedBar(0.5f, out _);
            var skin = new MeshSkin(
                new[] { "mPelvis", Knee },
                new[]
                {
                    System.Numerics.Matrix4x4.CreateTranslation(0f, 0f, -PetPelvisZ),
                    System.Numerics.Matrix4x4.Identity,
                },
                System.Numerics.Matrix4x4.Identity, 0f,
                new[]
                {
                    System.Numerics.Matrix4x4.CreateTranslation(0f, 0f, PetPelvisZ),
                    System.Numerics.Matrix4x4.CreateTranslation(kneeDef.Position + new System.Numerics.Vector3(0f, 0f, -KneeDrop)),
                },
                LockScaleIfJointPosition: true);
            var data = new MeshData(bar.Submeshes, skin);

            // The worn object: a root and one rigged child, the root flagged as animated mesh and
            // carrying the whole linkset's animation list the way a sim's ObjectAnimation names it.
            var objPos = new System.Numerics.Vector3(0.1f, -0.2f, 0.3f);
            var objRot = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, MathF.PI * 0.2f);
            Entity Worn(uint localId, uint parentLocalId, bool root)
            {
                var e = world.GetOrCreateEntity(Region, localId);
                e.SetComponent(new MetadataComponent(Guid.NewGuid()));
                e.SetComponent(new TransformComponent
                {
                    ParentLocalId = parentLocalId,
                    Position = root ? objPos : System.Numerics.Vector3.Zero,
                    Rotation = root ? objRot : System.Numerics.Quaternion.Identity,
                });
                e.SetComponent(new PrimitiveComponent(System.Numerics.Vector3.One, profileCurve: 0)
                {
                    IsAnimatedMesh = root,
                    IsMesh = true,
                    MeshId = meshId,
                });
                e.SetComponent(new AttachmentComponent(wearerEntity.Id, 1));
                return e;
            }
            var rootEntity = Worn(101, parentLocalId: 100, root: true);
            var childEntity = Worn(102, parentLocalId: 101, root: false);
            var rootPrim = rootEntity.GetComponent<PrimitiveComponent>()!;

            // The attachment point the object follows is built by the real update path.
            UpdateAttachment(rootEntity.Id.ToString());
            bool nodeBuilt = TryGetAttachmentFrame(rootEntity.Id, out _);
            Expect(nodeBuilt, "the root's attachment point was not built");

            PendingRig Rig(AvatarVisual on) => new(data, on, on.Skeleton!, meshId, null, default, _avatarSkeleton, false);

            // 1. The mesh goes to a control avatar of its own; the wearer gets nothing from it. Through
            //    the loader's own entry point and the rig worker, so the hook in CommitPreparedRig is
            //    what is under test, not just the routing method behind it.
            ApplyAttachmentMeshDataAsync(data, visual.Root, visual, meshId, null, default,
                System.Numerics.Vector3.One, objPos, objRot, rootEntity.Id);
            for (int i = 0; i < 1000 && !_controlAvatars.ContainsKey(rootEntity.Id); i++)
            {
                Settle();
                if (!_controlAvatars.ContainsKey(rootEntity.Id)) System.Threading.Thread.Sleep(2);
            }
            _controlAvatars.TryGetValue(rootEntity.Id, out var ca);
            bool routed = ca != null;
            Expect(routed, "the animated root's rig was not taken by a control avatar");
            if (ca == null) return (false, string.Join("; ", failures));
            Expect(ca != null && ca.IsAttached && ca.WearerEntityId == wearerEntity.Id && ca.AttachmentPoint == 1,
                   "no attached control avatar keyed by the root");
            Expect(ca != null && ca.Parts.ContainsKey(rootEntity.Id), "the root's part is missing");
            Expect(visual.JointOverrides.MeshCount == 0 && visual.RiggedAttachments.Count == 0
                   && !_riggedAttachments.ContainsKey(rootEntity.Id) && visual.PelvisFixups.Count == 0,
                   "the wearer took something from the animated mesh");
            Expect(skeleton.GetBoneRest(wearerKnee).Origin.IsEqualApprox(wearerKneeBefore)
                   && skeleton.GetBoneRest(wearerPelvis).Origin.IsEqualApprox(wearerPelvisBefore),
                   "the wearer's skeleton moved");

            // 2. The pet's skeleton carries the creature's overrides.
            float petPelvisY = 0f, petKneeDrop = 0f;
            if (ca?.Visual.Skeleton is { } petSkeleton)
            {
                int pelvis = petSkeleton.FindBone("mPelvis");
                int knee = petSkeleton.FindBone(Knee);
                petPelvisY = petSkeleton.GetBoneRest(pelvis).Origin.Y;
                petKneeDrop = wearerKneeBefore.Y - petSkeleton.GetBoneRest(knee).Origin.Y;
            }
            Expect(ca != null && ca.Visual.JointOverrides.MeshCount == 1 && MathF.Abs(petPelvisY - PetPelvisZ) < 0.01f,
                   $"the pet's pelvis is at {petPelvisY:0.###} m, wanted {PetPelvisZ} (the override)");
            Expect(MathF.Abs(petKneeDrop - KneeDrop) < 0.01f, $"the pet's knee is {petKneeDrop:0.###} m lower, wanted {KneeDrop}");
            Expect(MathF.Abs(wearerPelvisBefore.Y - PetPelvisZ) > 0.5f, "the wearer's pelvis is already creature-sized");

            // 3. A second, rigged child prim of the same object shares that skeleton.
            bool childRouted = TryRouteWornRigToControlAvatar(childEntity.Id, Rig(visual));
            Expect(childRouted && _controlAvatars.Count == 1 && ca != null && ca.Parts.Count == 2, "the child did not join the root's skeleton");

            // 4. Placement, against the viewer's formula in SL terms.
            SelfTestTick(0.016f);
            string placement = "no frame";
            if (ca != null && TryGetPosedAttachmentFrame(visual, rootEntity.Id, out var frame))
            {
                var jointPos = new System.Numerics.Vector3(frame.Origin.X, -frame.Origin.Z, frame.Origin.Y);
                var jq = frame.Basis.GetRotationQuaternion();
                var jointRot = System.Numerics.Quaternion.Normalize(new System.Numerics.Quaternion(jq.X, -jq.Z, jq.Y, jq.W));
                var wantPos = System.Numerics.Vector3.Transform(objPos, jointRot) + jointPos;
                var wantRot = System.Numerics.Quaternion.Concatenate(objRot, jointRot);

                var got = ca.Visual.Root.GlobalTransform;
                var gotPos = new System.Numerics.Vector3(got.Origin.X, -got.Origin.Z, got.Origin.Y);
                var gq = got.Basis.GetRotationQuaternion();
                var gotRot = new System.Numerics.Quaternion(gq.X, -gq.Z, gq.Y, gq.W);
                float dot = MathF.Abs(System.Numerics.Quaternion.Dot(gotRot, wantRot));
                placement = $"at ({gotPos.X:0.###}, {gotPos.Y:0.###}, {gotPos.Z:0.###}) wanted ({wantPos.X:0.###}, {wantPos.Y:0.###}, {wantPos.Z:0.###}), rotation dot {dot:0.####}";
                Expect((gotPos - wantPos).Length() < 2e-3f && dot > 0.9999f, "placement differs from the viewer's formula: " + placement);
                Expect(ca.Visual.Root.Visible && got.Basis.Scale.IsEqualApprox(Godot.Vector3.One), "the pet is hidden or scaled");
            }
            else Expect(false, "no attachment frame to place against");

            // 4b. The attachment point's own frame is the viewer's. Chest turns about two axes (0 90 90), and a
            //     point's Euler angles are combined by LLQuaternion::setQuat (Z first, then Y, then X) -- NOT the
            //     way a joint's are (X first). For Chest that is a third of a turn about the diagonal: the
            //     point's x lies along the joint's y, its y along z, its z along x (SL axes; Godot is (x, z, -y)).
            //     Everything above is blind to this: it takes the frame as given.
            var chestPoint = AttachmentPointMap.GetPoint(1);
            if (chestPoint is { } chest)
            {
                var chestBasis = AttachPointOffset(chest, null, "mChest").Basis;
                bool chestFrame =
                    (chestBasis * new Godot.Vector3(1f, 0f, 0f)).IsEqualApprox(new Godot.Vector3(0f, 0f, -1f))
                    && (chestBasis * new Godot.Vector3(0f, 0f, -1f)).IsEqualApprox(new Godot.Vector3(0f, 1f, 0f))
                    && (chestBasis * new Godot.Vector3(0f, 1f, 0f)).IsEqualApprox(new Godot.Vector3(1f, 0f, 0f));
                Expect(chestFrame, "the Chest attachment point's frame is not the viewer's (setQuat order)");
            }
            else Expect(false, "no Chest attachment point");

            // 5. It follows the wearer's animation and movement: the attachment point moves, so does the pet.
            var before = ca?.Visual.Root.GlobalTransform.Origin ?? default;
            visual.Root.Position += new Godot.Vector3(2f, 0f, 1f);
            SelfTestTick(0.016f);
            var after = ca?.Visual.Root.GlobalTransform.Origin ?? default;
            Expect((after - before).IsEqualApprox(new Godot.Vector3(2f, 0f, 1f)), $"the pet did not follow the wearer (moved {(after - before)})");

            // 6. Its own animation, from the ROOT's list; the wearer's player is not involved.
            var key = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, MathF.PI / 3f);
            var animId = Guid.NewGuid();
            AnimationLoaderOverride = id => System.Threading.Tasks.Task.FromResult<AnimationData?>(
                id == animId ? SelfTestAnimation(Shoulder, key, loop: true, length: 2f) : null);
            rootPrim.SignaledAnimations = new[] { new SignaledAnimation(animId, 1) };
            UpdateAttachment(rootEntity.Id.ToString());   // what the world's component-updated event runs
            for (int i = 0; i < 20; i++)
            {
                SelfTestTick(0.05f);
                Settle();
                if (SelfTestPlaying(rootEntity.Id).Length > 0 && SelfTestBoneAtKey(rootEntity.Id, Shoulder, key)) break;
                System.Threading.Thread.Sleep(2);
            }
            Expect(SelfTestBoneAtKey(rootEntity.Id, Shoulder, key), "the pet's shoulder is not at the animation key");
            Expect(SelfTestAtRest(skeleton, Shoulder) && !visual.AnimPlayer.IsPlaying, "the wearer played the pet's animation");

            // 7. Visibility is the wearer's.
            visual.Root.Visible = false;
            SelfTestTick(0.016f);
            bool hidden = ca != null && !ca.Visual.Root.Visible && ca.Parts.Values.All(part => !part.Visible);
            visual.Root.Visible = true;
            SelfTestTick(0.016f);
            bool shownAgain = ca != null && ca.Visual.Root.Visible && ca.Parts.Values.All(part => part.Visible);
            Expect(hidden && shownAgain, $"visibility did not follow the wearer (hidden {hidden}, back {shownAgain})");

            // 8. The flag going away takes the mesh off the control avatar (the update then reloads it as an ordinary attachment).
            rootPrim.IsAnimatedMesh = false;
            UpdateAttachment(childEntity.Id.ToString());
            Expect(!_controlAvatarOfPrim.ContainsKey(childEntity.Id), "the child stayed on the control avatar after the flag went");
            rootPrim.IsAnimatedMesh = true;
            Expect(_controlAvatars.Count == 1 && ca != null && ca.Parts.Count == 1, "releasing one part freed the shared skeleton");

            // 9. The wearer's visual is rebuilt: the old skeleton goes, the attachments are asked for again.
            var staleRoot = ca!.Visual.Root;
            rebuilt = SelfTestWearer(wearerEntity);   // replaces _visuals[wearer]
            SelfTestTick(0.016f);
            Expect(_controlAvatars.Count == 0 && _controlAvatarOfPrim.Count == 0, "the control avatar outlived the wearer's visual");
            Expect(staleRoot.IsQueuedForDeletion(), "the old skeleton was not freed");
            Expect(!_attachmentMeshIds.ContainsKey(rootEntity.Id), "the root was not asked for again");

            // ...and the next rig for the new visual makes a new one.
            bool again = TryRouteWornRigToControlAvatar(rootEntity.Id, Rig(rebuilt));
            Expect(again && _controlAvatars.TryGetValue(rootEntity.Id, out var ca2) && ca2.WearerVisual == rebuilt && ca2 != ca,
                   "no new control avatar for the rebuilt wearer");

            // 10. Detach: the attachment goes, and so does the skeleton. Nothing is left behind.
            _controlAvatars.TryGetValue(rootEntity.Id, out var live);
            var liveRoot = live?.Visual.Root;
            RemoveVisual(rootEntity.Id);
            Expect(_controlAvatars.Count == 0 && _controlAvatarOfPrim.Count == 0 && _pendingControlParts.Count == 0,
                   "detaching left a control avatar behind");
            Expect(liveRoot == null || liveRoot.IsQueuedForDeletion(), "the detached pet's skeleton was not freed");

            // 11. The wearer going away takes a pet that is still there with it.
            bool third = TryRouteWornRigToControlAvatar(rootEntity.Id, Rig(rebuilt));
            _visuals.Remove(wearerEntity.Id);
            SelfTestTick(0.016f);
            Expect(third && _controlAvatars.Count == 0, "the wearer leaving left the pet standing");

            bool ok = failures.Count == 0;
            return (ok, ok
                ? $"an animated object's rigs go to a control avatar of their own and the wearer is untouched; its skeleton carries the " +
                  $"overrides (pelvis {petPelvisY:0.###} m, knee {petKneeDrop:0.###} m lower), is placed per llcontrolavatar.cpp:182-193 " +
                  $"({placement}), follows the wearer, plays its own animation, hides with the wearer, and is freed with a rebuilt wearer, " +
                  "a detach, or a wearer that left"
                : string.Join("; ", failures));
        }
        finally
        {
            _world = priorWorld;
            AnimationLoaderOverride = priorLoader;
            foreach (var rootId in _controlAvatars.Keys.ToList()) FreeControlAvatar(rootId);
            _controlAvatarOfPrim.Clear();
            _controlAvatarRerouteRoots.Clear();
            _attachmentMeshIds.Clear();
            _riggedAttachments.Clear();
            foreach (var node in _attachmentNodes.Values) if (IsInstanceValid(node)) node.QueueFree();
            _attachmentNodes.Clear();
            _visuals.Remove(wearerEntity.Id);
            if (visual != null && IsInstanceValid(visual.Root)) visual.Root.QueueFree();
            if (rebuilt != null && IsInstanceValid(rebuilt.Root)) rebuilt.Root.QueueFree();
        }
    }
}
