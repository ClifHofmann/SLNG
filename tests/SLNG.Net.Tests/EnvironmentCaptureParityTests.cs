using System;
using System.IO;
using System.Linq;
using LibreMetaverse.StructuredData;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>Parses a REAL environment document captured from a live region, rather than one this
/// project wrote (FEAT-ENV-01 Phase A → B).
///
/// The fixture is the raw <c>ExtEnvironment</c> response from OSGrid's The Dangazi Forest,
/// captured 2026-08-19 and committed verbatim. It is the difference between "the parser handles
/// the shape we believe grids send" and "the parser handles the shape this grid sent" — and the
/// assertions below encode what that capture actually turned out to contain, so a future parser
/// change that quietly stops handling real data fails here instead of on screen.</summary>
public class EnvironmentCaptureParityTests
{
    private static OSD LoadCapture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "environment-dangazi-eep.llsd");
        return OSDParser.DeserializeLLSDNotation(File.ReadAllText(path));
    }

    [Fact]
    public void RealCapture_IsAFullDayCycleWithBothTracksPopulated()
    {
        var cycle = EnvironmentLlsdParser.ParseDayCycle(LoadCapture(), 14400, 0);

        // The capture's track 1 carries 7 sky keyframes and track 0 carries 5 water keyframes.
        Assert.Equal(7, cycle.SkyFrames.Count);
        Assert.Equal(5, cycle.WaterFrames.Count);
    }

    [Fact]
    public void RealCapture_KeyframesAreInAscendingOrderAndInRange()
    {
        var cycle = EnvironmentLlsdParser.ParseDayCycle(LoadCapture(), 14400, 0);

        var positions = cycle.SkyFrames.Select(f => f.Position).ToArray();

        Assert.Equal(positions.OrderBy(p => p), positions);
        Assert.All(positions, p => Assert.InRange(p, 0f, 1f));
    }

    /// <summary>The finding that justifies the whole two-level lookup: every one of the capture's
    /// 21 sky frames nests its haze parameters under "legacy_haze". A parser reading only the top
    /// level would render this region — and by extension most of OpenSim — with default haze and
    /// no error anywhere to explain it.</summary>
    [Fact]
    public void RealCapture_SkyFramesCarryTheirHazeInTheLegacyHazeSubMap()
    {
        var cycle = EnvironmentLlsdParser.ParseDayCycle(LoadCapture(), 14400, 0);

        // If the nested block were being missed, every frame would come back holding the viewer
        // default instead, and they would all be identical.
        var horizons = cycle.SkyFrames.Select(f => f.Settings.BlueHorizon).Distinct().ToArray();

        Assert.True(horizons.Length > 1,
            "every sky keyframe parsed to the same blue_horizon — the legacy_haze block is not being read");
        Assert.DoesNotContain(SkySettings.Default.BlueHorizon, horizons);
    }

    [Fact]
    public void RealCapture_WaterFramesAreParsedNotDefaulted()
    {
        var cycle = EnvironmentLlsdParser.ParseDayCycle(LoadCapture(), 14400, 0);

        // The capture's water frames name a real normal map; the default is Guid.Empty.
        Assert.All(cycle.WaterFrames, f => Assert.NotEqual(Guid.Empty, f.Settings.NormalMapId));
    }

    /// <summary>Walks the whole cycle and checks nothing degenerates. Cheap, and it is the class
    /// of failure — a NaN from a zero-length span, a wrap that skips a segment — that only shows
    /// up at one particular time of day and is therefore easy to ship.</summary>
    [Fact]
    public void RealCapture_EveryPositionInTheCycleEvaluatesToAFiniteSky()
    {
        var cycle = EnvironmentLlsdParser.ParseDayCycle(LoadCapture(), 14400, 0);

        for (int i = 0; i <= 100; i++)
        {
            var sky = cycle.EvaluateSkyAt(i / 100f);

            Assert.True(float.IsFinite(sky.BlueHorizon.X), $"non-finite sky at position {i / 100f}");
            Assert.True(float.IsFinite(sky.HazeDensity), $"non-finite haze at position {i / 100f}");

            var lighting = SkyLighting.Calculate(sky, lightDirectionZ: 0.5f);
            Assert.True(float.IsFinite(lighting.SunDiffuse.X), $"non-finite sun at position {i / 100f}");
        }
    }

    /// <summary>The cycle has to actually change over the day — a parse that silently produced one
    /// repeated frame would pass every structural check above and still render a static sky.</summary>
    [Fact]
    public void RealCapture_SkyChangesOverTheCourseOfTheDay()
    {
        var cycle = EnvironmentLlsdParser.ParseDayCycle(LoadCapture(), 14400, 0);

        var night = cycle.EvaluateSkyAt(0.0f);
        var noon = cycle.EvaluateSkyAt(0.5f);

        Assert.NotEqual(night.BlueHorizon, noon.BlueHorizon);
        Assert.NotEqual(
            SkyLighting.Calculate(night, 0.5f).SunDiffuse,
            SkyLighting.Calculate(noon, 0.5f).SunDiffuse);
    }
}
