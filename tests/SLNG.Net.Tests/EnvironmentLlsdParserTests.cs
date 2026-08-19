using System.Numerics;
using LibreMetaverse.StructuredData;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>Covers the environment LLSD parser (FEAT-ENV-01 Phase B).
///
/// These are written against the viewer's own source rather than against a live capture: key
/// names from llsettingssky.cpp / llsettingswater.cpp, defaults from their <c>defaults()</c>
/// tables, day-cycle shape from llsettingsdaycycle.cpp. A capture from a real region is still
/// wanted — it confirms which of these shapes OpenSim actually sends — but the shapes themselves
/// come from the reference implementation, not from guesswork.</summary>
public class EnvironmentLlsdParserTests
{
    private static OSDArray Color(float r, float g, float b) =>
        new() { OSD.FromReal(r), OSD.FromReal(g), OSD.FromReal(b) };

    // --- Sky --------------------------------------------------------------------------------

    [Fact]
    public void ParseSky_EmptyDocument_YieldsViewerDefaults()
    {
        var sky = EnvironmentLlsdParser.ParseSky(new OSDMap());

        Assert.Equal(SkySettings.Default, sky);
    }

    [Fact]
    public void ParseSky_TopLevelHazeKeys_AreRead()
    {
        var map = new OSDMap
        {
            ["blue_horizon"] = Color(0.1f, 0.2f, 0.3f),
            ["haze_density"] = OSD.FromReal(0.42),
        };

        var sky = EnvironmentLlsdParser.ParseSky(map);

        Assert.Equal(new Vector3(0.1f, 0.2f, 0.3f), sky.BlueHorizon);
        Assert.Equal(0.42f, sky.HazeDensity, 5);
    }

    /// <summary>The one that is easy to get wrong: SL nests the haze block under "legacy_haze"
    /// when it came from a legacy Windlight setting, and the viewer's getters check that sub-map
    /// BEFORE the top level (llsettingssky.cpp:1383-1411). A parser reading only the top level
    /// renders those regions with default haze and no error anywhere.</summary>
    [Fact]
    public void ParseSky_LegacyHazeSubMap_TakesPrecedenceOverTopLevel()
    {
        var map = new OSDMap
        {
            ["blue_horizon"] = Color(9f, 9f, 9f),
            ["legacy_haze"] = new OSDMap
            {
                ["blue_horizon"] = Color(0.1f, 0.2f, 0.3f),
            },
        };

        var sky = EnvironmentLlsdParser.ParseSky(map);

        Assert.Equal(new Vector3(0.1f, 0.2f, 0.3f), sky.BlueHorizon);
    }

    [Fact]
    public void ParseSky_LegacyHazePresentButKeyMissing_FallsThroughToTopLevel()
    {
        var map = new OSDMap
        {
            ["haze_horizon"] = OSD.FromReal(0.75),
            ["legacy_haze"] = new OSDMap { ["blue_density"] = Color(1f, 1f, 1f) },
        };

        var sky = EnvironmentLlsdParser.ParseSky(map);

        Assert.Equal(0.75f, sky.HazeHorizon, 5);
        Assert.Equal(Vector3.One, sky.BlueDensity);
    }

    [Fact]
    public void ParseSky_QuaternionIsReadInXyzwOrder()
    {
        var map = new OSDMap
        {
            ["sun_rotation"] = new OSDArray
            {
                OSD.FromReal(0.1), OSD.FromReal(0.2), OSD.FromReal(0.3), OSD.FromReal(0.4),
            },
        };

        var sky = EnvironmentLlsdParser.ParseSky(map);

        Assert.Equal(0.1f, sky.SunRotation.X, 5);
        Assert.Equal(0.2f, sky.SunRotation.Y, 5);
        Assert.Equal(0.3f, sky.SunRotation.Z, 5);
        Assert.Equal(0.4f, sky.SunRotation.W, 5);
    }

    // --- Water ------------------------------------------------------------------------------

    [Fact]
    public void ParseWater_CamelCaseLegacyKeys_AreAccepted()
    {
        var map = new OSDMap
        {
            ["waterFogColor"] = Color(0.5f, 0.6f, 0.7f),
            ["fresnelScale"] = OSD.FromReal(0.9),
        };

        var water = EnvironmentLlsdParser.ParseWater(map);

        Assert.Equal(new Vector3(0.5f, 0.6f, 0.7f), water.FogColor);
        Assert.Equal(0.9f, water.FresnelScale, 5);
    }

    [Fact]
    public void ParseWater_SnakeCaseWinsOverCamelCase()
    {
        var map = new OSDMap
        {
            ["water_fog_density"] = OSD.FromReal(1.0),
            ["waterFogDensity"] = OSD.FromReal(9.0),
        };

        Assert.Equal(1.0f, EnvironmentLlsdParser.ParseWater(map).FogDensity, 5);
    }

    // --- Day cycle --------------------------------------------------------------------------

    [Fact]
    public void ParseDayCycle_SingleSkyDocument_BecomesAFixedSky()
    {
        var map = new OSDMap
        {
            ["type"] = OSD.FromString("sky"),
            ["haze_density"] = OSD.FromReal(0.33),
        };

        var cycle = EnvironmentLlsdParser.ParseDayCycle(map, 14400, 0);

        Assert.Equal(0.33f, cycle.EvaluateSkyAt(0f).HazeDensity, 5);
        Assert.Equal(0.33f, cycle.EvaluateSkyAt(0.7f).HazeDensity, 5);
    }

    [Fact]
    public void ParseDayCycle_ResolvesTrackKeyframesAgainstTheFrameTable()
    {
        var cycle = EnvironmentLlsdParser.ParseDayCycle(TwoFrameDayCycle(), 14400, 0);

        Assert.Equal(0.0f, cycle.EvaluateSkyAt(0f).HazeDensity, 5);
        Assert.Equal(1.0f, cycle.EvaluateSkyAt(0.5f).HazeDensity, 5);
    }

    [Fact]
    public void ParseDayCycle_KeyframesAreSortedRegardlessOfWireOrder()
    {
        // Same cycle, keyframes written back to front. Evaluation scans for the bracketing pair
        // and depends on the order; the wire format promises nothing about it.
        var doc = TwoFrameDayCycle();
        var track = (OSDArray)((OSDArray)doc["tracks"])[1];
        (track[0], track[1]) = (track[1], track[0]);

        var cycle = EnvironmentLlsdParser.ParseDayCycle(doc, 14400, 0);

        Assert.Equal(0.0f, cycle.EvaluateSkyAt(0f).HazeDensity, 5);
        Assert.Equal(1.0f, cycle.EvaluateSkyAt(0.5f).HazeDensity, 5);
    }

    [Fact]
    public void ParseDayCycle_UnknownFrameName_SkipsThatKeyframeOnly()
    {
        var doc = TwoFrameDayCycle();
        var track = (OSDArray)((OSDArray)doc["tracks"])[1];
        ((OSDMap)track[1])["key_name"] = OSD.FromString("does_not_exist");

        var cycle = EnvironmentLlsdParser.ParseDayCycle(doc, 14400, 0);

        // The surviving keyframe still applies everywhere, rather than the whole track collapsing
        // to the viewer default.
        Assert.Single(cycle.SkyFrames);
        Assert.Equal(0.0f, cycle.EvaluateSkyAt(0.9f).HazeDensity, 5);
    }

    [Fact]
    public void ParseDayCycle_NotAMap_YieldsTheDefaultCycle()
    {
        Assert.Equal(DayCycle.Default.SkyFrames.Count,
            EnvironmentLlsdParser.ParseDayCycle(null, 14400, 0).SkyFrames.Count);
    }

    [Fact]
    public void ParseDayCycle_CarriesDayLengthAndOffset()
    {
        var cycle = EnvironmentLlsdParser.ParseDayCycle(TwoFrameDayCycle(), 3600, 1800);

        Assert.Equal(3600, cycle.DayLengthSeconds);
        Assert.Equal(1800, cycle.DayOffsetSeconds);
    }

    /// <summary>A minimal but structurally real day cycle: two sky keyframes at 0.0 and 0.5,
    /// distinguishable by haze density, referenced by name from track 1.</summary>
    private static OSDMap TwoFrameDayCycle() => new()
    {
        ["frames"] = new OSDMap
        {
            ["dawn"] = new OSDMap
            {
                ["type"] = OSD.FromString("sky"),
                ["haze_density"] = OSD.FromReal(0.0),
            },
            ["noon"] = new OSDMap
            {
                ["type"] = OSD.FromString("sky"),
                ["haze_density"] = OSD.FromReal(1.0),
            },
        },
        ["tracks"] = new OSDArray
        {
            new OSDArray(),  // track 0 = water
            new OSDArray
            {
                new OSDMap
                {
                    ["key_keyframe"] = OSD.FromReal(0.0),
                    ["key_name"] = OSD.FromString("dawn"),
                },
                new OSDMap
                {
                    ["key_keyframe"] = OSD.FromReal(0.5),
                    ["key_name"] = OSD.FromString("noon"),
                },
            },
        },
    };
}
