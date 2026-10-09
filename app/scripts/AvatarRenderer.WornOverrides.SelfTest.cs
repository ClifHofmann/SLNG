using Godot;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using System;
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
}
