using System;
using System.IO;
using System.Linq;
using System.Numerics;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>Parses the legacy Windlight presets the client SHIPS (FEAT-ENV-02), not hand-written
/// fixtures: the files under <c>app/assets/windlight</c> are linked into the test output, so a
/// parser change that stops handling the real library fails here rather than on screen.
///
/// The conversions asserted below are the ones that separate a legacy preset from an EEP
/// document, all of them silent failures if skipped — a preset would still load and still render,
/// just wrong.</summary>
public class WindlightPresetParserTests
{
    private static string PresetDir(string kind)
        => Path.Combine(AppContext.BaseDirectory, "TestData", "windlight", kind);

    private static string ReadPreset(string kind, string name)
        => File.ReadAllText(Path.Combine(PresetDir(kind), name + ".xml"));

    [Fact]
    public void EveryShippedSkyPreset_Parses()
    {
        var files = Directory.GetFiles(PresetDir("skies"), "*.xml");
        Assert.True(files.Length >= 30, $"expected the shipped sky library, found {files.Length} files");

        foreach (var file in files)
        {
            var sky = WindlightPresetParser.ParseSky(File.ReadAllText(file));
            Assert.NotNull(sky);

            // The legacy encoding stores these as [value, 0, 0, 1]. Reading the array with
            // AsReal() yields 0 for all of them without throwing, so a zero here means the
            // unwrap regressed -- not that a preset is unusual.
            Assert.True(sky!.CloudScale > 0f, $"{Path.GetFileName(file)}: cloud scale collapsed to 0");
            Assert.True(sky.MaxY > 0f, $"{Path.GetFileName(file)}: max_y collapsed to 0");
            Assert.True(sky.DensityMultiplier > 0f, $"{Path.GetFileName(file)}: density multiplier collapsed to 0");
        }
    }

    [Fact]
    public void EveryShippedWaterPreset_Parses()
    {
        var files = Directory.GetFiles(PresetDir("water"), "*.xml");
        Assert.True(files.Length >= 5, $"expected the shipped water library, found {files.Length} files");

        foreach (var file in files)
        {
            var water = WindlightPresetParser.ParseWater(File.ReadAllText(file));
            Assert.NotNull(water);
        }
    }

    [Fact]
    public void LegacyScalars_AreReadFromTheFirstArrayElement()
    {
        var sky = WindlightPresetParser.ParseSky(ReadPreset("skies", "Blue Midday"))!;

        // Values straight out of the file: cloud_scale [0.10999…], haze_density [2.8830…],
        // distance_multiplier [2.9846…], max_y [600].
        Assert.Equal(0.10999f, sky.CloudScale, 4);
        Assert.Equal(2.8830f, sky.HazeDensity, 3);
        Assert.Equal(2.9846f, sky.DistanceMultiplier, 3);
        Assert.Equal(600f, sky.MaxY, 1);
    }

    [Fact]
    public void LegacyCloudScroll_IsShiftedDownByTen()
    {
        var sky = WindlightPresetParser.ParseSky(ReadPreset("skies", "Blue Midday"))!;

        // File carries [10.4994, 10.0109] in the legacy 0..20 encoding of a -10..+10 range.
        Assert.Equal(0.4994f, sky.CloudScrollRate.X, 3);
        Assert.Equal(0.0109f, sky.CloudScrollRate.Y, 3);
    }

    [Fact]
    public void LegacyStarBrightness_IsScaledToTheEepRange()
    {
        // Legacy is 0..2, EEP is 0..500; the viewer multiplies by 250 on conversion
        // (llsettingssky.cpp:1063). Midnight's file says 2 -- i.e. a full sky of stars.
        var sky = WindlightPresetParser.ParseSky(ReadPreset("skies", "Midnight"))!;

        Assert.Equal(500f, sky.StarBrightness, 1);
    }

    [Fact]
    public void LegacySunAngles_BecomeASunRotationMatchingTheFilesOwnLightnorm()
    {
        // A legacy preset has no sun_rotation at all -- only sun_angle (altitude) and east_angle
        // (azimuth). Without the conversion the rotation stays identity and the sun sits on the
        // horizon due east no matter which preset is picked.
        //
        // The file also carries the light direction it was authored with, as "lightnorm"
        // [0, 0.86074, -0.50904, 0] -- SL's own y-up render frame. In the Z-up frame this parser
        // works in that is (x, z, y) = (-0.50904, 0, 0.86074), which is what the reconstructed
        // rotation has to produce.
        var sky = WindlightPresetParser.ParseSky(ReadPreset("skies", "Blue Midday"))!;

        var direction = Vector3.Transform(Vector3.UnitX, sky.SunRotation);

        Assert.Equal(-0.50904f, direction.X, 3);
        Assert.Equal(0f, direction.Y, 3);
        Assert.Equal(0.86074f, direction.Z, 3);
    }

    [Fact]
    public void MidnightSunIsBelowTheHorizon_AndItsMoonIsAbove()
    {
        // sun_angle 4.7124 rad = 270 degrees: straight down. The moon was diametrically opposed
        // in legacy Windlight, which is the only thing that lights a midnight preset.
        var sky = WindlightPresetParser.ParseSky(ReadPreset("skies", "Midnight"))!;

        var sun = Vector3.Transform(Vector3.UnitX, sky.SunRotation);
        var moon = Vector3.Transform(Vector3.UnitX, sky.MoonRotation);

        Assert.True(sun.Z < -0.9f, $"sun should be below the horizon, was z={sun.Z}");
        Assert.True(moon.Z > 0.9f, $"moon should be above the horizon, was z={moon.Z}");
    }

    [Fact]
    public void WaterPreset_ReadsTheCamelCaseLegacyKeysIncludingTheNormalMap()
    {
        var water = WindlightPresetParser.ParseWater(ReadPreset("water", "Glassy"))!;

        Assert.Equal(new Guid("822ded49-9a6c-f61c-cb89-6df54f42cdf4"), water.NormalMapId);
        Assert.Equal(1f, water.FogDensity, 3);
        Assert.Equal(0.58f, water.FresnelOffset, 3);
        Assert.Equal(new Vector3(2f, 2f, 2f), water.NormalScale);
        Assert.Equal(0.5f, water.Wave1Direction.X, 3);
        Assert.Equal(-0.17f, water.Wave1Direction.Y, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not llsd at all")]
    [InlineData("<llsd><array><integer>1</integer></array></llsd>")]
    public void MalformedInput_ReturnsNullRatherThanThrowing(string text)
    {
        Assert.Null(WindlightPresetParser.ParseSky(text));
        Assert.Null(WindlightPresetParser.ParseWater(text));
    }
}
