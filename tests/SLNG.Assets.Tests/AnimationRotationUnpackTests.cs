using System;
using System.Numerics;
using SLNG.Assets;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>Pins the keyframe-rotation reconstruction against
/// <c>LLQuaternion::unpackFromVector3</c> (llquaternion.cpp:943).
///
/// The case that matters is the last one. SL stores only x, y and z and rebuilds w from them, so
/// quantization can hand back a quaternion slightly longer than 1 — the viewer clamps the negative
/// radicand to w = 0 and carries on, because LLQuaternion's own slerp and lerp do not care. Godot's
/// do: <c>Quaternion.Slerp</c> throws on a non-unit operand, and a non-unit bone pose scales the
/// bone. These tests exist to keep that premise visible, so nobody "tidies up" the clamp into a
/// normalize here and moves the bug somewhere harder to find.</summary>
public class AnimationRotationUnpackTests
{
    private const float Tolerance = 1e-6f;

    [Fact]
    public void ZeroVector_IsTheIdentityRotation()
    {
        var q = AnimationDecodeService.UnpackRotation(0f, 0f, 0f);
        Assert.Equal(0f, q.X, Tolerance);
        Assert.Equal(0f, q.Y, Tolerance);
        Assert.Equal(0f, q.Z, Tolerance);
        Assert.Equal(1f, q.W, Tolerance);
    }

    [Fact]
    public void WIsRebuiltSoTheResultIsUnitLength()
    {
        // A quarter turn about Z: xyz = (0, 0, sin(45 deg)), so w must come back as cos(45 deg).
        float s = MathF.Sin(MathF.PI / 4f);
        var q = AnimationDecodeService.UnpackRotation(0f, 0f, s);

        Assert.Equal(MathF.Cos(MathF.PI / 4f), q.W, Tolerance);
        Assert.Equal(1f, q.Length(), Tolerance);
    }

    [Fact]
    public void ExactlyUnitXyz_GivesWZero()
    {
        var q = AnimationDecodeService.UnpackRotation(1f, 0f, 0f);
        Assert.Equal(0f, q.W, Tolerance);
        Assert.Equal(1f, q.Length(), Tolerance);
    }

    [Fact]
    public void OverUnitXyz_ClampsWToZeroAndLeavesTheQuaternionLongerThanOne()
    {
        // The whole reason AvatarAnimationPlayer normalizes. sqrt of a negative radicand would be
        // NaN, so the viewer clamps -- and the price is a quaternion that is no longer unit.
        var q = AnimationDecodeService.UnpackRotation(0.9f, 0.9f, 0f);

        Assert.Equal(0f, q.W);
        Assert.False(float.IsNaN(q.W));
        Assert.True(q.Length() > 1f, $"expected an over-unit quaternion, got length {q.Length()}");
        Assert.Equal(MathF.Sqrt(0.9f * 0.9f * 2f), q.Length(), Tolerance);
    }

    [Fact]
    public void XyzArePassedThroughUnchanged()
    {
        var q = AnimationDecodeService.UnpackRotation(0.1f, -0.2f, 0.3f);
        Assert.Equal(0.1f, q.X, Tolerance);
        Assert.Equal(-0.2f, q.Y, Tolerance);
        Assert.Equal(0.3f, q.Z, Tolerance);
    }

    [Fact]
    public void UnpackPosition_NeutralOffset_DecodesToZeroMeters()
    {
        // Second Life neutral 0m position is quantized to 32768, which LMV decodes to 0.5f.
        var p = AnimationDecodeService.UnpackPosition(0.5f, 0.5f, 0.5f);
        Assert.Equal(0f, p.X, Tolerance);
        Assert.Equal(0f, p.Y, Tolerance);
        Assert.Equal(0f, p.Z, Tolerance);
    }

    [Fact]
    public void UnpackPosition_MinAndMaxRange_DecodesToPlusMinusFiveMeters()
    {
        // LL_MAX_PELVIS_OFFSET is 5.0m: min (-0.5f) -> -5.0m, max (1.5f) -> +5.0m.
        var min = AnimationDecodeService.UnpackPosition(-0.5f, -0.5f, -0.5f);
        Assert.Equal(-5.0f, min.X, Tolerance);
        Assert.Equal(-5.0f, min.Y, Tolerance);
        Assert.Equal(-5.0f, min.Z, Tolerance);

        var max = AnimationDecodeService.UnpackPosition(1.5f, 1.5f, 1.5f);
        Assert.Equal(5.0f, max.X, Tolerance);
        Assert.Equal(5.0f, max.Y, Tolerance);
        Assert.Equal(5.0f, max.Z, Tolerance);
    }

    [Fact]
    public void UnpackPosition_ArbitraryOffset_ScalesAccurately()
    {
        // 0.6f -> (0.6 - 0.5) * 5.0 = +0.5m; 0.4f -> (0.4 - 0.5) * 5.0 = -0.5m.
        var p = AnimationDecodeService.UnpackPosition(0.6f, 0.4f, 0.5f);
        Assert.Equal(0.5f, p.X, Tolerance);
        Assert.Equal(-0.5f, p.Y, Tolerance);
        Assert.Equal(0f, p.Z, Tolerance);
    }

    [Fact]
    public void BinBvhReader_NeutralPositionKey_DecodesToZeroMeters()
    {
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms);
        bw.Write((ushort)1); // version
        bw.Write((ushort)0); // subversion
        bw.Write((int)3); // priority
        bw.Write((float)1.0f); // duration
        bw.Write((byte)0); // empty emote string
        bw.Write((float)0f); // loop in
        bw.Write((float)1f); // loop out
        bw.Write((int)1); // loop
        bw.Write((float)0f); // ease in
        bw.Write((float)0f); // ease out
        bw.Write((uint)0); // hand pose
        bw.Write((int)1); // num joints

        // Joint:
        bw.Write(System.Text.Encoding.ASCII.GetBytes("mPelvis\0"));
        bw.Write((int)3); // joint priority
        bw.Write((int)0); // num rot keys
        bw.Write((int)1); // num pos keys
        // Pos key: time 0, pos X, Y, Z (uint16) = neutral 32768
        bw.Write((ushort)0);
        bw.Write((ushort)32768);
        bw.Write((ushort)32768);
        bw.Write((ushort)32768);

        var reader = new LibreMetaverse.BinBVHAnimationReader(ms.ToArray());
        var joint = reader.joints[0];
        var key = joint.positionkeys[0];
        var unpacked = AnimationDecodeService.UnpackPosition(key.key_element.X, key.key_element.Y, key.key_element.Z);
        Assert.Equal(0f, unpacked.X, 1e-3f);
        Assert.Equal(0f, unpacked.Y, 1e-3f);
        Assert.Equal(0f, unpacked.Z, 1e-3f);
    }
}


