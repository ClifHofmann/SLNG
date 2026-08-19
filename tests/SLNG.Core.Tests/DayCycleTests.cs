using System;
using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>Covers day-cycle evaluation and the derived lighting (FEAT-ENV-01 Phases C and E).
/// Both are pure functions of their inputs, so none of this needs a grid, a clock or a
/// renderer.</summary>
public class DayCycleTests
{
    private static SkySettings Sky(float hazeDensity) =>
        SkySettings.Default with { HazeDensity = hazeDensity };

    private static DayCycle TwoFrames() => new(
        new[]
        {
            new DayCycleFrame<SkySettings>(0.0f, Sky(0f)),
            new DayCycleFrame<SkySettings>(0.5f, Sky(1f)),
        },
        new[] { new DayCycleFrame<WaterSettings>(0f, WaterSettings.Default) });

    [Fact]
    public void EvaluateSkyAt_OnAKeyframe_ReturnsThatKeyframeExactly()
    {
        var cycle = TwoFrames();

        Assert.Equal(0f, cycle.EvaluateSkyAt(0.0f).HazeDensity, 5);
        Assert.Equal(1f, cycle.EvaluateSkyAt(0.5f).HazeDensity, 5);
    }

    [Fact]
    public void EvaluateSkyAt_BetweenKeyframes_InterpolatesLinearly()
    {
        Assert.Equal(0.5f, TwoFrames().EvaluateSkyAt(0.25f).HazeDensity, 5);
    }

    /// <summary>The track is a loop. Past the last keyframe the blend runs back into the first
    /// across the wrap — without this a cycle whose last frame differs from its first snaps
    /// visibly once per day.</summary>
    [Fact]
    public void EvaluateSkyAt_PastTheLastKeyframe_WrapsBackToTheFirst()
    {
        var cycle = TwoFrames();

        // 0.5 -> 1.0/0.0 is a half-cycle span; 0.75 sits exactly halfway along it.
        Assert.Equal(0.5f, cycle.EvaluateSkyAt(0.75f).HazeDensity, 5);
        Assert.Equal(0f, cycle.EvaluateSkyAt(1.0f).HazeDensity, 5);
    }

    [Fact]
    public void EvaluateSkyAt_SingleKeyframe_IsAFixedSky()
    {
        var cycle = new DayCycle(
            new[] { new DayCycleFrame<SkySettings>(0.3f, Sky(0.7f)) },
            Array.Empty<DayCycleFrame<WaterSettings>>());

        Assert.Equal(0.7f, cycle.EvaluateSkyAt(0.0f).HazeDensity, 5);
        Assert.Equal(0.7f, cycle.EvaluateSkyAt(0.9f).HazeDensity, 5);
    }

    [Fact]
    public void EvaluateWaterAt_EmptyTrack_FallsBackToTheViewerDefault()
    {
        var cycle = new DayCycle(
            new[] { new DayCycleFrame<SkySettings>(0f, SkySettings.Default) },
            Array.Empty<DayCycleFrame<WaterSettings>>());

        Assert.Equal(WaterSettings.Default, cycle.EvaluateWaterAt(0.5f));
    }

    /// <summary>Duplicate positions are legal on the wire and must resolve deterministically
    /// rather than by scan order luck: the last keyframe at that position wins, because it is the
    /// last one the track applies.</summary>
    [Fact]
    public void EvaluateSkyAt_TwoKeyframesAtTheSamePosition_TakesTheLater()
    {
        var cycle = new DayCycle(
            new[]
            {
                new DayCycleFrame<SkySettings>(0.5f, Sky(0.2f)),
                new DayCycleFrame<SkySettings>(0.5f, Sky(0.8f)),
            },
            Array.Empty<DayCycleFrame<WaterSettings>>());

        Assert.Equal(0.8f, cycle.EvaluateSkyAt(0.5f).HazeDensity, 5);
    }

    /// <summary>The one genuinely degenerate case: keyframes at exactly 0.0 and 1.0 leave the
    /// wrap segment zero-length, so the blend across it would divide by zero.</summary>
    [Fact]
    public void EvaluateSkyAt_KeyframesAtBothEndsOfTheCycle_DoesNotDivideByZero()
    {
        var cycle = new DayCycle(
            new[]
            {
                new DayCycleFrame<SkySettings>(0.0f, Sky(0.2f)),
                new DayCycleFrame<SkySettings>(1.0f, Sky(0.8f)),
            },
            Array.Empty<DayCycleFrame<WaterSettings>>());

        var atEnd = cycle.EvaluateSkyAt(1.0f).HazeDensity;

        Assert.True(float.IsFinite(atEnd));
        Assert.Equal(0.8f, atEnd, 5);
    }

    // --- Cycle position ---------------------------------------------------------------------

    /// <summary>Matches LLEnvironment::DayInstance::animate: seconds since the epoch plus the
    /// region's day offset, looped over the day length.</summary>
    [Fact]
    public void PositionAt_IsEpochSecondsPlusOffsetOverLength()
    {
        var cycle = new DayCycle(
            Array.Empty<DayCycleFrame<SkySettings>>(),
            Array.Empty<DayCycleFrame<WaterSettings>>(),
            DayLengthSeconds: 4000,
            DayOffsetSeconds: 1000);

        // 3000s past the epoch + 1000s offset = 4000, exactly one full cycle -> back to 0.
        Assert.Equal(0f, cycle.PositionAt(DateTimeOffset.FromUnixTimeSeconds(3000)), 5);
        // 4000 + 1000 = 5000 -> a quarter into the second cycle.
        Assert.Equal(0.25f, cycle.PositionAt(DateTimeOffset.FromUnixTimeSeconds(4000)), 5);
    }

    /// <summary>A fixed-sky region reports a day length of zero. That is a legitimate setting,
    /// not an error, and must not divide by zero.</summary>
    [Fact]
    public void PositionAt_ZeroDayLength_IsAFixedSkyAtPositionZero()
    {
        var cycle = new DayCycle(
            Array.Empty<DayCycleFrame<SkySettings>>(),
            Array.Empty<DayCycleFrame<WaterSettings>>(),
            DayLengthSeconds: 0);

        Assert.Equal(0f, cycle.PositionAt(DateTimeOffset.FromUnixTimeSeconds(12345)), 5);
    }

    // --- Derived lighting -------------------------------------------------------------------

    /// <summary>The whole reason SkyLighting exists: the shader's sunlight_color uniform is the
    /// OUTPUT of calculateLightSettings, not the settings field of the same name. If these were
    /// ever equal, someone has wired the raw value through.</summary>
    [Fact]
    public void Calculate_SunDiffuse_IsNotTheRawSunlightColor()
    {
        var lighting = SkyLighting.Calculate(SkySettings.Default, lightDirectionZ: 1.0f);

        Assert.NotEqual(SkySettings.Default.SunlightColor, lighting.SunDiffuse);
    }

    /// <summary>A low sun travels a longer path through the atmosphere, so it is attenuated
    /// harder. This is the behaviour that makes sunsets work.</summary>
    [Fact]
    public void Calculate_LowSun_IsAttenuatedMoreThanAHighSun()
    {
        var overhead = SkyLighting.Calculate(SkySettings.Default, lightDirectionZ: 1.0f);
        var low = SkyLighting.Calculate(SkySettings.Default, lightDirectionZ: 0.1f);

        Assert.True(Luminance(low.SunDiffuse) < Luminance(overhead.SunDiffuse));
    }

    /// <summary>The viewer guards the 1/|lightnorm.z| reciprocal with a small epsilon rather than
    /// a zero check; a sun exactly on the horizon must stay finite.</summary>
    [Fact]
    public void Calculate_SunExactlyOnTheHorizon_StaysFinite()
    {
        var lighting = SkyLighting.Calculate(SkySettings.Default, lightDirectionZ: 0f);

        Assert.True(float.IsFinite(lighting.SunDiffuse.X));
        Assert.True(float.IsFinite(lighting.SunAmbient.X));
        Assert.True(float.IsFinite(lighting.HazeColor.X));
    }

    /// <summary>More cloud cover lifts the ambient — the sky acts as a bigger diffuser.</summary>
    [Fact]
    public void Calculate_MoreCloudShadow_RaisesAmbient()
    {
        var clear = SkyLighting.Calculate(SkySettings.Default with { CloudShadow = 0f }, 1.0f);
        var cloudy = SkyLighting.Calculate(SkySettings.Default with { CloudShadow = 1f }, 1.0f);

        Assert.True(Luminance(cloudy.SunAmbient) > Luminance(clear.SunAmbient));
    }

    [Fact]
    public void Calculate_MoonBelowHorizon_DimsMoonlightToTheViewersFloor()
    {
        var moonUp = SkyLighting.Calculate(SkySettings.Default, lightDirectionZ: -0.5f);
        var moonDown = SkyLighting.Calculate(SkySettings.Default, lightDirectionZ: 0.5f);

        Assert.True(Luminance(moonDown.MoonDiffuse) < Luminance(moonUp.MoonDiffuse));
    }

    private static float Luminance(Vector3 c) => c.X + c.Y + c.Z;
}
