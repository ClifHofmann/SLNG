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
}
