using Godot;
using SLNG.Assets;
using System;
using System.Collections.Generic;

namespace SLNG.App;

public partial class AvatarRenderer
{
    /// <summary>
    /// BUG-ANIM-07: an animation's POSITION key drives any joint, not only the pelvis.
    ///
    /// <para>The unit tests pin the arithmetic (<c>AnimatedJointPosition</c>) and the priority rule
    /// (<c>JointPriorityTable</c>); this runs the rest of the chain on a real avatar skeleton through
    /// the real player: the key, the parent's scale <c>ApplyShape</c> hands over, the rest a worn
    /// mesh's joint override lives in, and the way back when the animation stops. The failure it
    /// exists for does not throw and does not log: an add-on HUD's shape pose that is simply not
    /// applied, leaving the add-on's bones where the mesh or the skeleton put them.</para>
    ///
    /// <para>Known answers are written in SL terms: a joint's pose position is the key, scaled axis by
    /// axis by its PARENT's own scale, then SL (x, y, z) to Godot (x, z, -y). The pelvis is the
    /// exception -- its key is added to its rest -- and must not have changed.</para>
    ///
    /// <para>Needs the renderer in a scene tree; nothing is fetched.</para>
    /// </summary>
    internal (bool Passed, string Detail) SelfTestAnimationPositionKeys()
    {
        LoadAvatarSkeleton();
        if (_avatarSkeleton == null) return (false, "the avatar skeleton did not load");

        const string Joint = "mHindLimb2Left";
        const string Tongue = "mFaceTongueTip";
        const string Pelvis = "mPelvis";
        var jointDef = _avatarSkeleton.GetBone(Joint);
        var tongueDef = _avatarSkeleton.GetBone(Tongue);
        if (jointDef?.ParentName == null || tongueDef?.ParentName == null)
            return (false, "the skeleton definition is not the one this check is written against");
        string parentName = jointDef.ParentName;

        var visual = new AvatarVisual();
        AddChild(visual.Root);
        try
        {
            var skeleton = SkeletonBuilder.Build(_avatarSkeleton);
            skeleton.Name = "Skeleton3D";
            visual.Root.AddChild(skeleton);
            visual.Skeleton = skeleton;
            ApplyShape(visual, skeleton, _avatarSkeleton, visual.LastDistortions);
            skeleton.ResetBonePoses();
            var player = visual.AnimPlayer;
            player.SetSkeleton(skeleton);

            int jointIdx = skeleton.FindBone(Joint);
            int tongueIdx = skeleton.FindBone(Tongue);
            int pelvisIdx = skeleton.FindBone(Pelvis);
            if (jointIdx < 0 || tongueIdx < 0 || pelvisIdx < 0)
                return (false, $"{Joint}, {Tongue} or {Pelvis} is not in the built skeleton");

            Godot.Vector3 Rest(int idx) => skeleton.GetBoneRest(idx).Origin;
            Godot.Vector3 Pose(int idx) => skeleton.GetBonePosePosition(idx);
            bool Near(Godot.Vector3 a, Godot.Vector3 b) => (a - b).Length() < 1e-5f;
            string Mm(Godot.Vector3 v) => $"({v.X * 1000f:0.0}, {v.Y * 1000f:0.0}, {v.Z * 1000f:0.0}) mm";
            Godot.Vector3 ToGodot(System.Numerics.Vector3 v) => new(v.X, v.Z, -v.Y);
            // The known answer: the key, scaled axis by axis by the parent's own scale, SL to Godot.
            Godot.Vector3 Expected(System.Numerics.Vector3 key, string parent)
                => ToGodot(key * visual.BoneOwnScale[parent]);

            AnimationData PositionPose(int priority, string joint, System.Numerics.Vector3 key) => new()
            {
                Length = 1f,
                InPoint = 0f,
                OutPoint = 1f,
                Loop = true,
                Priority = priority,
                Joints = new[]
                {
                    new AnimationJointData
                    {
                        JointName = joint,
                        Priority = priority,
                        PositionKeys = new[] { new PositionKeyframe(0f, key) },
                    },
                },
            };

            // 1. The tongue: a key equal to the skeleton default puts the joint where it already was.
            // This is the BUG-ANIM-01 symptom ("dummy tracks"), which was rest + key counting it twice.
            var tongueKey = tongueDef.Position;
            var tongueRest = Rest(tongueIdx);
            var tongueId = Guid.NewGuid();
            player.SetActiveAnimations(new[] { (tongueId, PositionPose(2, Tongue, tongueKey)) });
            player.Advance(0.05f);
            bool tongueStays = Near(Pose(tongueIdx), tongueRest);
            var tonguePose = Pose(tongueIdx);
            player.Stop();

            // 2. The pelvis is measured from the skeleton root and keeps its meaning: rest + key
            // (and the bare key for a control avatar). Unchanged.
            var pelvisKey = new System.Numerics.Vector3(0.05f, -0.10f, 0.20f);
            var pelvisRest = Rest(pelvisIdx);
            player.SetActiveAnimations(new[] { (Guid.NewGuid(), PositionPose(2, Pelvis, pelvisKey)) });
            player.Advance(0.05f);
            bool pelvisRestPlusKey = Near(Pose(pelvisIdx), pelvisRest + ToGodot(pelvisKey));
            var pelvisPose = Pose(pelvisIdx);
            player.Stop();
            player.AbsolutePelvisPosition = true;
            player.SetActiveAnimations(new[] { (Guid.NewGuid(), PositionPose(2, Pelvis, pelvisKey)) });
            player.Advance(0.05f);
            bool pelvisAbsolute = Near(Pose(pelvisIdx), ToGodot(pelvisKey));
            player.AbsolutePelvisPosition = false;
            player.Stop();
            bool pelvisBack = Near(Pose(pelvisIdx), pelvisRest);

            // 3. A joint's key is its absolute local position, scaled by its PARENT: the parent is made
            // visibly non-unit by the shape, and the answer is written from that scale.
            var distortions = new Dictionary<string, (System.Numerics.Vector3 Scale, System.Numerics.Vector3 Position)>
            {
                [parentName] = (new System.Numerics.Vector3(0.5f, 0.25f, 1.0f), default),
            };
            ApplyShape(visual, skeleton, _avatarSkeleton, distortions);
            skeleton.ResetBonePoses();
            var parentScale = visual.BoneOwnScale[parentName];
            bool parentIsScaled = !parentScale.Equals(System.Numerics.Vector3.One);

            var key = new System.Numerics.Vector3(0f, 0.006f, -0.023f);   // the add-on HUD's mHindLimb2Left
            var restPlain = Rest(jointIdx);
            var hudId = Guid.NewGuid();
            player.SetActiveAnimations(new[] { (hudId, PositionPose(2, Joint, key)) });
            player.Advance(0.05f);
            var drivenPose = Pose(jointIdx);
            bool movesToKey = Near(drivenPose, Expected(key, parentName));
            bool actuallyMoved = (drivenPose - restPlain).Length() > 0.1f;   // the default sits 0.47 m lower

            // 3b. The shape changes while the pose plays: the very next frame uses the NEW parent scale.
            distortions[parentName] = (new System.Numerics.Vector3(-0.25f, 1.0f, 0.5f), default);
            ApplyShape(visual, skeleton, _avatarSkeleton, distortions);
            player.Advance(0.05f);
            bool followsShape = Near(Pose(jointIdx), Expected(key, parentName))
                                && !Near(Pose(jointIdx), drivenPose);

            // 3c. Stopped: the joint is back at its rest.
            player.SetActiveAnimations(Array.Empty<(Guid, AnimationData)>());
            bool backAtRest = Near(Pose(jointIdx), Rest(jointIdx));

            // 4. A worn mesh's joint-position override lives in the rest. While the pose drives the
            // joint its key wins (the viewer writes the key without overrides, lljoint.h:233); stopped,
            // the joint is at the override again.
            var meshId = Guid.NewGuid();
            var overridePos = new System.Numerics.Vector3(0.30f, 0.05f, -0.10f);
            visual.JointOverrides.Apply(meshId,
                new[] { new KeyValuePair<string, System.Numerics.Vector3>(Joint, overridePos) },
                Array.Empty<string>());
            ApplyShape(visual, skeleton, _avatarSkeleton, distortions, visual.JointPosOverrides);
            skeleton.ResetBonePoses();
            var restOverridden = Rest(jointIdx);
            bool overrideInRest = Near(restOverridden, ToGodot(overridePos * visual.BoneOwnScale[parentName]));
            player.SetActiveAnimations(new[] { (hudId, PositionPose(2, Joint, key)) });
            player.Advance(0.05f);
            bool keyBeatsOverride = Near(Pose(jointIdx), Expected(key, parentName))
                                    && !Near(Pose(jointIdx), restOverridden);
            player.SetActiveAnimations(Array.Empty<(Guid, AnimationData)>());
            bool overrideBack = Near(Pose(jointIdx), restOverridden);

            // 5. Per joint, the higher priority wins -- whichever was offered last at equal priority.
            var high = new System.Numerics.Vector3(0.10f, 0.00f, -0.20f);
            player.SetActiveAnimations(new[]
            {
                (Guid.NewGuid(), PositionPose(4, Joint, high)),
                (Guid.NewGuid(), PositionPose(2, Joint, key)),
            });
            player.Advance(0.05f);
            bool higherWins = Near(Pose(jointIdx), Expected(high, parentName));
            player.Stop();
            player.SetActiveAnimations(new[]
            {
                (Guid.NewGuid(), PositionPose(2, Joint, key)),
                (Guid.NewGuid(), PositionPose(4, Joint, high)),
            });
            player.Advance(0.05f);
            bool higherWinsOtherOrder = Near(Pose(jointIdx), Expected(high, parentName));
            player.Stop();

            // 6. A rotation-only animation leaves the joint's position at its rest (or override).
            player.SetActiveAnimations(new[]
            {
                (Guid.NewGuid(), new AnimationData
                {
                    Length = 1f, InPoint = 0f, OutPoint = 1f, Loop = true, Priority = 3,
                    Joints = new[]
                    {
                        new AnimationJointData
                        {
                            JointName = Joint, Priority = 3,
                            RotationKeys = new[] { new RotationKeyframe(0f, System.Numerics.Quaternion.CreateFromYawPitchRoll(0f, 0f, 0.5f)) },
                        },
                    },
                }),
            });
            player.Advance(0.05f);
            bool rotationLeavesPosition = Near(Pose(jointIdx), restOverridden);
            player.Stop();

            bool ok = tongueStays && pelvisRestPlusKey && pelvisAbsolute && pelvisBack && parentIsScaled && movesToKey
                      && actuallyMoved && followsShape && backAtRest && overrideInRest && keyBeatsOverride
                      && overrideBack && higherWins && higherWinsOtherOrder && rotationLeavesPosition;
            return (ok, ok
                ? $"a position key on {Joint} puts it at the key scaled by its parent's scale {parentScale.X:0.##}/{parentScale.Y:0.##}/{parentScale.Z:0.##} " +
                  $"(rest {Mm(restPlain)} -> {Mm(drivenPose)}), follows a shape change on the next frame, beats a worn mesh's override and gives the joint back when it stops; " +
                  "the higher priority wins whatever the order; a tongue key equal to the default stays put; the pelvis is still rest + key (absolute for a control avatar)"
                : $"tongue key equal to its default stays put {(tongueStays ? "ok" : $"WRONG (rest {Mm(tongueRest)}, pose {Mm(tonguePose)})")}; " +
                  $"pelvis rest+key {(pelvisRestPlusKey ? "ok" : $"WRONG (pose {Mm(pelvisPose)}, rest {Mm(pelvisRest)})")} / absolute {(pelvisAbsolute ? "ok" : "WRONG")} / back {(pelvisBack ? "ok" : "WRONG")}; " +
                  $"parent scaled {(parentIsScaled ? "ok" : "WRONG (the check's shape did not scale it)")}; " +
                  $"{Joint} moves to the scaled key {(movesToKey ? "ok" : $"WRONG (pose {Mm(drivenPose)}, expected {Mm(Expected(key, parentName))})")} " +
                  $"and really moved {(actuallyMoved ? "ok" : "WRONG")}; follows a shape change {(followsShape ? "ok" : "WRONG")}; back at rest when stopped {(backAtRest ? "ok" : "WRONG")}; " +
                  $"override sits in the rest {(overrideInRest ? "ok" : "WRONG")}, key beats it {(keyBeatsOverride ? "ok" : "WRONG")}, override back {(overrideBack ? "ok" : "WRONG")}; " +
                  $"higher priority wins {(higherWins ? "ok" : "WRONG")} / other order {(higherWinsOtherOrder ? "ok" : "WRONG")}; " +
                  $"rotation-only leaves the position {(rotationLeavesPosition ? "ok" : "WRONG")}");
        }
        finally
        {
            if (IsInstanceValid(visual.Root)) visual.Root.QueueFree();
        }
    }
}
