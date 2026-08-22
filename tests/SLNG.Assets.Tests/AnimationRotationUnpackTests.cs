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
}
