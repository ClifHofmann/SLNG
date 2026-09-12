using System.Collections.Generic;
using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class SlJointComposerTests
{
    // Builds a minimal skeleton from bare (name, parent, pos, rotDeg) tuples for tests that don't
    // need a full avatar_skeleton.xml — SlJointComposer only needs AvatarSkeleton's public shape
    // (Bones list, each parent appearing before its children).
    private static AvatarSkeleton MakeSkeleton(params (string Name, string? Parent, Vector3 Pos, Vector3 RotDeg)[] bones)
    {
        // AvatarSkeleton.LoadFromXml is the only public constructor path; build the equivalent
        // minimal XML rather than reflect into private fields, so this test exercises the exact
        // same parse path real skeletons go through.
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        string V(Vector3 v) => $"{v.X.ToString(ic)} {v.Y.ToString(ic)} {v.Z.ToString(ic)}";

        var sb = new System.Text.StringBuilder("<linden_skeleton>");
        void Write(int idx)
        {
            var b = bones[idx];
            sb.Append($"<bone name=\"{b.Name}\" pos=\"{V(b.Pos)}\" rot=\"{V(b.RotDeg)}\" scale=\"1 1 1\" end=\"0 0 0\" support=\"base\">");
            for (int j = 0; j < bones.Length; j++)
                if (bones[j].Parent == b.Name) Write(j);
            sb.Append("</bone>");
        }
        for (int i = 0; i < bones.Length; i++)
            if (bones[i].Parent == null) Write(i);
        sb.Append("</linden_skeleton>");
        return AvatarSkeleton.LoadFromXml(sb.ToString());
    }

    [Fact]
    public void Root_bone_world_pose_equals_its_own_local_values()
    {
        var skel = MakeSkeleton(("mPelvis", null, new Vector3(0, 0, 1.0f), Vector3.Zero));
        var poses = SlJointComposer.ComputePoses(skel);

        Assert.True(poses.TryGetValue("mPelvis", out var p));
        Assert.Equal(new Vector3(0, 0, 1.0f), p.WorldPosition);
        Assert.Equal(Vector3.One, p.OwnScale);
        Assert.True(Quaternion.Dot(p.WorldRotation, Quaternion.Identity) > 0.9999f);
    }

    [Fact]
    public void Zero_rotation_chain_composes_position_as_a_simple_sum()
    {
        // Matches AvatarSkeleton.GetGlobalRestPosition's own model when every scale is (1,1,1).
        var skel = MakeSkeleton(
            ("mTorso", null, new Vector3(0, 0, 1.0f), Vector3.Zero),
            ("mChest", "mTorso", new Vector3(0, 0, 0.2f), Vector3.Zero));
        var poses = SlJointComposer.ComputePoses(skel);

        Assert.True((poses["mChest"].WorldPosition - new Vector3(0, 0, 1.2f)).Length() < 1e-5f);
    }

    [Fact]
    public void Parent_scale_offsets_child_position_by_exactly_one_level()
    {
        // LLXformMatrix::update(): mWorldPosition.scaleVec(parentScale) — the child's LOCAL
        // position is scaled by the parent's OWN scale before being rotated/translated into the
        // parent's world frame. Give the parent a distortion so its OwnScale != 1 and confirm the
        // child's world position reflects exactly that multiplier, once.
        var skel = MakeSkeleton(
            ("mChest", null, Vector3.Zero, Vector3.Zero),
            ("mCollarLeft", "mChest", new Vector3(1f, 0, 0), Vector3.Zero));
        var distortions = new Dictionary<string, (Vector3 Scale, Vector3 Position)>
        {
            ["mChest"] = (new Vector3(2f, 3f, 4f), Vector3.Zero),
        };

        var poses = SlJointComposer.ComputePoses(skel, distortions);

        // mChest's OwnScale = base(1,1,1) + distortion(2,3,4) = (3,4,5).
        Assert.True((poses["mChest"].OwnScale - new Vector3(3, 4, 5)).Length() < 1e-5f);
        // mCollarLeft's local (1,0,0) scaled by parent's OwnScale (3,4,5) -> (3,0,0), then
        // (identity rotation) placed at parent's world position (origin) -> (3,0,0).
        Assert.True((poses["mCollarLeft"].WorldPosition - new Vector3(3, 0, 0)).Length() < 1e-4f);
    }

    [Fact]
    public void Own_scale_never_inherits_the_parent_scale_the_central_property()
    {
        // THE property this whole class exists for: verified against LLMatrix4::initAll — a
        // joint's world matrix is built from ITS OWN scale only. A parent with a huge distortion
        // must NOT change a child's OwnScale at all, even through several generations.
        var skel = MakeSkeleton(
            ("mTorso", null, Vector3.Zero, Vector3.Zero),
            ("mChest", "mTorso", new Vector3(0, 0, 0.2f), Vector3.Zero),
            ("mCollarLeft", "mChest", new Vector3(0, 0.1f, 0), Vector3.Zero),
            ("mShoulderLeft", "mCollarLeft", new Vector3(0, 0.08f, 0), Vector3.Zero),
            ("mElbowLeft", "mShoulderLeft", new Vector3(0, 0.25f, 0), Vector3.Zero));

        var distortions = new Dictionary<string, (Vector3 Scale, Vector3 Position)>
        {
            ["mTorso"] = (new Vector3(5f, 5f, 5f), Vector3.Zero),
            ["mChest"] = (new Vector3(3f, 3f, 3f), Vector3.Zero),
            ["mCollarLeft"] = (new Vector3(4f, 4f, 4f), Vector3.Zero),
            ["mShoulderLeft"] = (new Vector3(2f, 2f, 2f), Vector3.Zero),
            // mElbowLeft itself gets NO distortion — base scale stays exactly (1,1,1).
        };

        var poses = SlJointComposer.ComputePoses(skel, distortions);

        Assert.True((poses["mElbowLeft"].OwnScale - Vector3.One).Length() < 1e-5f,
            $"expected mElbowLeft OwnScale (1,1,1) regardless of ancestors, got {poses["mElbowLeft"].OwnScale}");
    }

    [Fact]
    public void Regression_real_measured_chain_does_not_compound_to_the_observed_bug_magnitude()
    {
        // The actual DBG-CHAIN2 distortion values measured this session (2026-07-03) on a real
        // avatar whose sleeves rendered 1.2-2.7 m off their joints and ~4-6x too large in SLNG's
        // OLD (Skeleton3D-compounding) renderer, while Firestorm rendered the identical shape
        // data correctly. With SL-accurate composition, mElbowLeft's own scale must come out to
        // its own distortion only (~1.15-1.19), never anywhere near the observed (6.1, 4.4, 4.78).
        var skel = MakeSkeleton(
            ("mTorso", null, Vector3.Zero, Vector3.Zero),
            ("mChest", "mTorso", new Vector3(0, 0, 0.2f), Vector3.Zero),
            ("mCollarLeft", "mChest", new Vector3(0, 0.1f, 0), Vector3.Zero),
            ("mShoulderLeft", "mCollarLeft", new Vector3(0, 0.08f, 0), Vector3.Zero),
            ("mElbowLeft", "mShoulderLeft", new Vector3(0, 0.248f, 0), Vector3.Zero));

        var distortions = new Dictionary<string, (Vector3 Scale, Vector3 Position)>
        {
            ["mTorso"] = (new Vector3(0.15f, 0.15f, 0.24521571f), Vector3.Zero),
            ["mChest"] = (new Vector3(0.18357648f, 0.1343059f, 0.12639217f), Vector3.Zero),
            ["mCollarLeft"] = (new Vector3(0f, 0.70000005f, 0f), Vector3.Zero),
            ["mShoulderLeft"] = (new Vector3(0.15f, 0.47144315f, 0.15f), Vector3.Zero),
            ["mElbowLeft"] = (new Vector3(0.15f, 0.19049414f, 0.15f), Vector3.Zero),
        };

        var poses = SlJointComposer.ComputePoses(skel, distortions);

        var expected = new Vector3(1.15f, 1.19049414f, 1.15f);
        Assert.True((poses["mElbowLeft"].OwnScale - expected).Length() < 1e-4f,
            $"expected mElbowLeft OwnScale {expected} (its own distortion only), got {poses["mElbowLeft"].OwnScale}");

        // Sanity: this must be nowhere near the buggy compounded magnitude that was actually
        // observed (6.1, 4.4, 4.78) — any component anywhere near 2x would indicate compounding
        // crept back in.
        Assert.True(poses["mElbowLeft"].OwnScale.X < 1.5f);
        Assert.True(poses["mElbowLeft"].OwnScale.Y < 1.5f);
        Assert.True(poses["mElbowLeft"].OwnScale.Z < 1.5f);
    }

    [Fact]
    public void Position_override_replaces_local_position_before_composing_with_the_parent()
    {
        var skel = MakeSkeleton(
            ("mChest", null, Vector3.Zero, Vector3.Zero),
            ("mCollarLeft", "mChest", new Vector3(1f, 0, 0), Vector3.Zero));
        var overrides = new Dictionary<string, Vector3> { ["mCollarLeft"] = new Vector3(9f, 9f, 9f) };

        var poses = SlJointComposer.ComputePoses(skel, distortions: null, positionOverrides: overrides);

        Assert.True((poses["mCollarLeft"].WorldPosition - new Vector3(9, 9, 9)).Length() < 1e-5f);
    }

    [Fact]
    public void Rotation_composes_child_first_then_parent_matching_sl_row_vector_order()
    {
        // SL: mayaQ(x,y,z,XYZ) = xQ*yQ*zQ under v*q (leftmost first) => rotate X, then Y, then Z.
        // A single bone with rot=(0,0,90) should rotate the local +X axis to +Y (SL space, degrees,
        // right-handed: Z-axis rotation by +90 takes X toward Y).
        var skel = MakeSkeleton(("mTest", null, Vector3.Zero, new Vector3(0, 0, 90)));
        var poses = SlJointComposer.ComputePoses(skel);

        var rotatedX = Vector3.Transform(Vector3.UnitX, poses["mTest"].WorldRotation);
        Assert.True((rotatedX - Vector3.UnitY).Length() < 1e-4f, $"expected local +X to rotate to +Y, got {rotatedX}");
    }

    // Local (pos.z relative to parent) values copied verbatim from the real SL
    // avatar_skeleton.xml (scratch/slviewer/indra/newview/character/avatar_skeleton.xml) so this
    // test exercises ComputeBodySize against real default-shape numbers, not synthetic ones.
    private static AvatarSkeleton MakeDefaultBodySizeSkeleton() => MakeSkeleton(
        ("mPelvis", null, new Vector3(0, 0, 1.067f), Vector3.Zero),
        ("mTorso", "mPelvis", new Vector3(0, 0, 0.084f), Vector3.Zero),
        ("mChest", "mTorso", new Vector3(-0.015f, 0, 0.205f), Vector3.Zero),
        ("mNeck", "mChest", new Vector3(-0.010f, 0, 0.251f), Vector3.Zero),
        ("mHead", "mNeck", new Vector3(0, 0, 0.076f), Vector3.Zero),
        ("mSkull", "mHead", new Vector3(0, 0, 0.079f), Vector3.Zero),
        ("mHipLeft", "mPelvis", new Vector3(0.034f, 0.127f, -0.041f), Vector3.Zero),
        ("mKneeLeft", "mHipLeft", new Vector3(-0.001f, -0.046f, -0.491f), Vector3.Zero),
        ("mAnkleLeft", "mKneeLeft", new Vector3(-0.029f, 0.001f, -0.468f), Vector3.Zero),
        ("mFootLeft", "mAnkleLeft", new Vector3(0.112f, -0.000f, -0.061f), Vector3.Zero));

    [Fact]
    public void ComputeBodySize_matches_LLAvatarAppearance_computeBodySize_for_the_default_skeleton()
    {
        // Reference values hand-derived from llavatarappearance.cpp's literal formula (all
        // joint/parent scales = 1 for the un-distorted default skeleton):
        //   pelvisToFoot = hipZ - kneeZ - ankleZ - footZ
        //                = -0.041 - (-0.491) - (-0.468) - (-0.061) = 0.979
        //   bodySizeZ = pelvisToFoot + sqrt2*skullZ + headZ + neckZ + chestZ + torsoZ
        //             = 0.979 + 1.41421356*0.079 + 0.076 + 0.251 + 0.205 + 0.084 ~= 1.7067
        var skel = MakeDefaultBodySizeSkeleton();
        var body = SlJointComposer.ComputeBodySize(skel);

        Assert.True(System.MathF.Abs(body.PelvisToFoot - 0.979f) < 1e-3f,
            $"expected PelvisToFoot ~0.979, got {body.PelvisToFoot}");
        Assert.True(System.MathF.Abs(body.BodySizeZ - 1.7067f) < 1e-3f,
            $"expected BodySizeZ ~1.7067, got {body.BodySizeZ}");
    }

    [Fact]
    public void ComputeBodySize_is_not_the_same_as_telescoping_ComputePoses_world_positions()
    {
        // Documents the asymmetry called out in BodySize's doc comment: naively taking
        // ComputePoses' fully-composed world Z for mPelvis/mFootLeft (a true chain distance)
        // gives a DIFFERENT number than the real viewer's mPelvisToFoot. Both are "correct" for
        // what they each compute; only ComputeBodySize's number matches the real viewer's root
        // Z correction.
        var skel = MakeDefaultBodySizeSkeleton();
        var body = SlJointComposer.ComputeBodySize(skel);

        var poses = SlJointComposer.ComputePoses(skel);
        float telescoped = poses["mPelvis"].WorldPosition.Z - poses["mFootLeft"].WorldPosition.Z;

        Assert.True(System.MathF.Abs(telescoped - 1.061f) < 1e-3f,
            $"expected telescoped chain distance ~1.061, got {telescoped}");
        Assert.True(System.MathF.Abs(telescoped - body.PelvisToFoot) > 0.05f,
            "expected ComputeBodySize to genuinely differ from the telescoped chain distance");
    }

    [Fact]
    public void ComputeBodySize_honors_shape_distortions_and_position_overrides()
    {
        var skel = MakeDefaultBodySizeSkeleton();
        var baseline = SlJointComposer.ComputeBodySize(skel);

        // Stretching mKneeLeft->mAnkleLeft's parent scale (mKneeLeft's own scale) should change
        // PelvisToFoot by exactly (ankleZ * deltaKneeScaleZ), matching the formula's
        // "- ankleZ * kneeScaleZ" term.
        var distortions = new Dictionary<string, (Vector3 Scale, Vector3 Position)>
        {
            ["mKneeLeft"] = (new Vector3(0, 0, 0.2f), Vector3.Zero),
        };
        var stretched = SlJointComposer.ComputeBodySize(skel, distortions);
        float expectedDelta = -(-0.468f) * 0.2f; // -ankleZ * deltaKneeScaleZ
        Assert.True(System.MathF.Abs((stretched.PelvisToFoot - baseline.PelvisToFoot) - expectedDelta) < 1e-4f,
            $"expected delta {expectedDelta}, got {stretched.PelvisToFoot - baseline.PelvisToFoot}");

        // A joint position override on a body-size-relevant bone (e.g. a fitted mesh's alternate
        // bind moving mAnkleLeft) must feed straight into the same formula, exactly like
        // ComputePoses' own position-override handling.
        var overrides = new Dictionary<string, Vector3> { ["mAnkleLeft"] = new Vector3(-0.029f, 0.001f, -1.0f) };
        var overridden = SlJointComposer.ComputeBodySize(skel, positionOverrides: overrides);
        Assert.NotEqual(baseline.PelvisToFoot, overridden.PelvisToFoot);
    }

    [Fact]
    public void ComputeBodySize_ignores_mPelvis_position_override_entirely()
    {
        // Pins the fact AvatarRenderer.ApplyJointPositionOverrides' root-joint exclusion relies on
        // (2026-07-22 investigation into a real coat/boots asset whose mPelvis alt_inverse_bind_
        // matrix decoded to a ~9.6 m, 10x-too-large, non-negated translation vs. every sibling
        // joint's sub-cm delta in the same asset — see AvatarRenderer's doc comment for the full
        // trace). Confirmed against llavatarappearance.cpp's literal computeBodySize(): it reads
        // mPelvisp->getScale() but never mPelvisp->getPosition() — a position override on "mPelvis"
        // must be a complete no-op for BodySize/PelvisToFoot, matching this port. If this test ever
        // fails, ComputeBodySize's formula changed to read mPelvis's position somewhere, which would
        // mean a corrupt/garbage mPelvis override (a real, observed hazard) could silently corrupt
        // the whole avatar's root-Z correction — re-evaluate the exclusion in AvatarRenderer before
        // "fixing" this test.
        var skel = MakeDefaultBodySizeSkeleton();
        var baseline = SlJointComposer.ComputeBodySize(skel);

        var overrides = new Dictionary<string, Vector3> { ["mPelvis"] = new Vector3(0f, 0f, 10.6701f) };
        var withBogusPelvisOverride = SlJointComposer.ComputeBodySize(skel, positionOverrides: overrides);

        Assert.Equal(baseline.PelvisToFoot, withBogusPelvisOverride.PelvisToFoot);
        Assert.Equal(baseline.BodySizeZ, withBogusPelvisOverride.BodySizeZ);
    }

    // ---- BUG-AVATAR-07: lock_scale_if_joint_position ----------------------------------------
    // A worn rigged mesh whose skin section sets lock_scale_if_joint_position pins the SCALE of
    // every joint it position-overrides to the skeleton default, and the shape sliders' skeletal
    // scale distortion on those joints is discarded (viewer: LLVOAvatar::
    // addAttachmentOverridesForObject -> LLJoint::addAttachmentScaleOverride, which
    // LLPolySkeletalDistortion::apply's setScale(..., apply_attachment_overrides: true) loses to).

    [Fact]
    public void Scale_locked_bone_ignores_its_shape_scale_distortion()
    {
        var skel = MakeSkeleton(("mHead", null, Vector3.Zero, Vector3.Zero));
        var distortions = new Dictionary<string, (Vector3 Scale, Vector3 Position)>
        {
            ["mHead"] = (new Vector3(-0.0757f, -0.0757f, -0.0757f), Vector3.Zero),
        };

        var unlocked = SlJointComposer.ComputePoses(skel, distortions);
        var locked = SlJointComposer.ComputePoses(
            skel, distortions, positionOverrides: null,
            scaleLockedBones: new HashSet<string> { "mHead" });

        // Without the lock the real "Head Size" slider at its DEFAULT (682 = 0.5 -> driven 655 =
        // -0.0757) already shrinks mHead ~7.6%; with it the joint stays at the skeleton default.
        Assert.True((unlocked["mHead"].OwnScale - new Vector3(0.9243f, 0.9243f, 0.9243f)).Length() < 1e-4f);
        Assert.True((locked["mHead"].OwnScale - Vector3.One).Length() < 1e-6f);
    }

    [Fact]
    public void Scale_lock_leaves_the_position_distortion_alone()
    {
        // The viewer keeps position overrides and scale overrides on separate maps — locking the
        // scale must not also freeze the joint's shape-driven position offset.
        var skel = MakeSkeleton(("mSkull", null, new Vector3(0, 0, 0.1f), Vector3.Zero));
        var distortions = new Dictionary<string, (Vector3 Scale, Vector3 Position)>
        {
            ["mSkull"] = (new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0, 0, -0.0076f)),
        };

        var poses = SlJointComposer.ComputePoses(
            skel, distortions, positionOverrides: null,
            scaleLockedBones: new HashSet<string> { "mSkull" });

        Assert.True((poses["mSkull"].OwnScale - Vector3.One).Length() < 1e-6f);
        Assert.True((poses["mSkull"].WorldPosition - new Vector3(0, 0, 0.0924f)).Length() < 1e-5f);
    }

    [Fact]
    public void Scale_lock_only_affects_the_bones_it_names()
    {
        var skel = MakeSkeleton(
            ("mTorso", null, Vector3.Zero, Vector3.Zero),
            ("mChest", "mTorso", new Vector3(0, 0, 0.2f), Vector3.Zero));
        var distortions = new Dictionary<string, (Vector3 Scale, Vector3 Position)>
        {
            ["mTorso"] = (new Vector3(-0.2f, -0.2f, -0.2f), Vector3.Zero),
            ["mChest"] = (new Vector3(-0.1f, -0.1f, -0.1f), Vector3.Zero),
        };

        var poses = SlJointComposer.ComputePoses(
            skel, distortions, positionOverrides: null,
            scaleLockedBones: new HashSet<string> { "mTorso" });

        Assert.True((poses["mTorso"].OwnScale - Vector3.One).Length() < 1e-6f);
        Assert.True((poses["mChest"].OwnScale - new Vector3(0.9f, 0.9f, 0.9f)).Length() < 1e-5f);
        // mChest's local (0,0,0.2) is offset by its parent's OWN scale, which the lock restored to
        // 1 — so the lock propagates to child POSITIONS exactly the way the viewer's does.
        Assert.True((poses["mChest"].WorldPosition - new Vector3(0, 0, 0.2f)).Length() < 1e-5f);
    }

    [Fact]
    public void ComputeBodySize_honors_the_same_scale_lock()
    {
        var skel = MakeSkeleton(
            ("mPelvis", null, new Vector3(0, 0, 1.0f), Vector3.Zero),
            ("mHipLeft", "mPelvis", new Vector3(0, 0.1f, -0.08f), Vector3.Zero),
            ("mKneeLeft", "mHipLeft", new Vector3(0, 0, -0.49f), Vector3.Zero),
            ("mAnkleLeft", "mKneeLeft", new Vector3(0, 0, -0.49f), Vector3.Zero),
            ("mFootLeft", "mAnkleLeft", new Vector3(0.1f, 0, -0.05f), Vector3.Zero));
        var distortions = new Dictionary<string, (Vector3 Scale, Vector3 Position)>
        {
            ["mPelvis"] = (new Vector3(0, 0, -0.3f), Vector3.Zero),
        };

        var shrunk = SlJointComposer.ComputeBodySize(skel, distortions);
        var lockedSize = SlJointComposer.ComputeBodySize(
            skel, distortions, positionOverrides: null,
            scaleLockedBones: new HashSet<string> { "mPelvis" });
        var undistorted = SlJointComposer.ComputeBodySize(skel);

        // mPelvis' Z scale multiplies mHipLeft's Z in the PelvisToFoot term, so shrinking it moves
        // the number; locking it must land back exactly on the undistorted measurement.
        Assert.NotEqual(shrunk.PelvisToFoot, lockedSize.PelvisToFoot, 5);
        Assert.Equal(undistorted.PelvisToFoot, lockedSize.PelvisToFoot, 5);
    }

    [Fact]
    public void No_scale_lock_set_changes_nothing()
    {
        var skel = MakeSkeleton(("mHead", null, Vector3.Zero, Vector3.Zero));
        var distortions = new Dictionary<string, (Vector3 Scale, Vector3 Position)>
        {
            ["mHead"] = (new Vector3(-0.1f, -0.1f, -0.1f), Vector3.Zero),
        };

        var none = SlJointComposer.ComputePoses(skel, distortions, null, new HashSet<string>());
        var nullSet = SlJointComposer.ComputePoses(skel, distortions);

        Assert.Equal(nullSet["mHead"].OwnScale, none["mHead"].OwnScale);
        Assert.False(SlJointComposer.IsScaleLocked(null, "mHead"));
        Assert.False(SlJointComposer.IsScaleLocked(new HashSet<string>(), "mHead"));
        Assert.True(SlJointComposer.IsScaleLocked(new HashSet<string> { "mHead" }, "mHead"));
        // A plain collection (not IReadOnlySet) must still resolve correctly.
        Assert.True(SlJointComposer.IsScaleLocked(new List<string> { "mHead" }, "mHead"));
    }
}
