using System;
using System.IO;
using System.Linq;
using System.Text;
using SLNG.Assets;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>BUG-ANIM-07. A v1.0 <c>.anim</c> that only sets POSITIONS -- the kind an add-on HUD plays
/// to shape its add-on on mHindLimb1-4 and mGroin -- decodes with every position key intact.
/// The bytes are written here, field by field, in the layout <c>LLKeyframeMotion::deserialize</c>
/// reads (llkeyframemotion.cpp:1393-1782); no third-party animation is a fixture.</summary>
public class AnimationPositionKeyDecodeTests
{
    // 10 m across a U16: the largest error one quantised component can carry is half a step.
    private const float PositionTolerance = 2e-4f;
    private const float Duration = 2.0f;

    private static ushort Quantize(float meters) => (ushort)MathF.Round((meters + 5f) / 10f * 65535f);

    private static void WritePositionKey(BinaryWriter w, ushort time, float x, float y, float z)
    {
        w.Write(time);
        w.Write(Quantize(x));
        w.Write(Quantize(y));
        w.Write(Quantize(z));
    }

    /// <summary>One joint, no rotation keys, the given position keys.</summary>
    private static void WritePositionOnlyJoint(BinaryWriter w, string name, int priority, params (ushort Time, float X, float Y, float Z)[] keys)
    {
        w.Write(Encoding.ASCII.GetBytes(name + "\0"));
        w.Write(priority);
        w.Write(0);                    // rotation keys
        w.Write(keys.Length);          // position keys
        foreach (var k in keys) WritePositionKey(w, k.Time, k.X, k.Y, k.Z);
    }

    private static byte[] HudPose()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)1);            // version
        w.Write((ushort)0);            // sub version
        w.Write(2);                    // base priority
        w.Write(Duration);
        w.Write((byte)0);              // empty emote name
        w.Write(0f);                   // loop in
        w.Write(Duration);             // loop out
        w.Write(1);                    // loop
        w.Write(0f);                   // ease in
        w.Write(0f);                   // ease out
        w.Write(0u);                   // hand pose
        w.Write(3u);                   // joints

        WritePositionOnlyJoint(w, "mHindLimb1Left", 2, (0, 0.214f, 0f, -0.138f));
        WritePositionOnlyJoint(w, "mHindLimb2Left", 2, (0, 0f, 0.006f, -0.023f));
        // Two keys, to pin the key times: 0 and the end of the animation.
        WritePositionOnlyJoint(w, "mGroin", 2, (0, 0.064f, 0f, -0.097f), (ushort.MaxValue, 0.07f, 0.01f, -0.09f));

        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void PositionOnlyJoints_KeepTheirPositionKeys()
    {
        var data = AnimationDecodeService.Decode(HudPose());

        Assert.NotNull(data);
        Assert.Equal(2, data!.Priority);
        Assert.Equal(new[] { "mHindLimb1Left", "mHindLimb2Left", "mGroin" }, data.Joints.Select(j => j.JointName).ToArray());
        Assert.All(data.Joints, j =>
        {
            Assert.Empty(j.RotationKeys);
            Assert.Equal(2, j.Priority);
        });
    }

    [Fact]
    public void HindLimb1Left_DecodesToTheAuthoredMeters()
    {
        var joint = AnimationDecodeService.Decode(HudPose())!.Joints.Single(j => j.JointName == "mHindLimb1Left");

        var key = Assert.Single(joint.PositionKeys);
        Assert.Equal(0f, key.Time);
        Assert.Equal(0.214f, key.Position.X, PositionTolerance);
        Assert.Equal(0f, key.Position.Y, PositionTolerance);
        Assert.Equal(-0.138f, key.Position.Z, PositionTolerance);
    }

    [Fact]
    public void HindLimb2Left_DecodesToTheAuthoredMeters()
    {
        var joint = AnimationDecodeService.Decode(HudPose())!.Joints.Single(j => j.JointName == "mHindLimb2Left");

        var key = Assert.Single(joint.PositionKeys);
        Assert.Equal(0f, key.Position.X, PositionTolerance);
        Assert.Equal(0.006f, key.Position.Y, PositionTolerance);
        Assert.Equal(-0.023f, key.Position.Z, PositionTolerance);
    }

    [Fact]
    public void Groin_KeepsBothKeysAndTheirTimes()
    {
        var joint = AnimationDecodeService.Decode(HudPose())!.Joints.Single(j => j.JointName == "mGroin");

        Assert.Equal(2, joint.PositionKeys.Length);
        Assert.Equal(0f, joint.PositionKeys[0].Time);
        Assert.Equal(Duration, joint.PositionKeys[1].Time, 1e-4f);
        Assert.Equal(0.064f, joint.PositionKeys[0].Position.X, PositionTolerance);
        Assert.Equal(-0.097f, joint.PositionKeys[0].Position.Z, PositionTolerance);
        Assert.Equal(0.07f, joint.PositionKeys[1].Position.X, PositionTolerance);
        Assert.Equal(0.01f, joint.PositionKeys[1].Position.Y, PositionTolerance);
        Assert.Equal(-0.09f, joint.PositionKeys[1].Position.Z, PositionTolerance);
    }
}
