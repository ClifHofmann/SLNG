using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-RENDER-48: <see cref="WaterFog"/> against the viewer's own getWaterFogViewNoClip.
/// The reference below is written the way waterFogF.glsl is -- a view VECTOR, a plane in eye space, an
/// intersection point -- where the production code works from heights and a distance, so agreeing is
/// evidence and not the same arithmetic twice.</summary>
public class WaterFogTests
{
    // Default water fog colour (0.0156, 0.149, 0.2509) through srgb_to_linear.
    private static readonly Vector3 DefaultColorLinear = new(0.00120743f, 0.01937789f, 0.05123714f);

    /// <summary>waterFogF.glsl:40-74 line for line. Eye at the origin, up = +Y, the plane at y = -eyeHeight
    /// (waterPlane = (0, 1, 0, eyeHeight)); the point sits at (offsetX, pointHeight - eyeHeight, 0).</summary>
    private static (Vector3 Radiance, float D) Reference(
        float eyeHeight, float pointHeight, float offsetX, float kd, float ks, Vector3 kc)
    {
        var pos = new Vector3(offsetX, pointHeight - eyeHeight, 0f);
        var planeNormal = Vector3.UnitY;
        float planeW = eyeHeight;

        // getWaterFogView: above the plane -> (0, 0, 0, 1)
        if (Vector3.Dot(pos, planeNormal) + planeW > 0f) return (Vector3.Zero, 1f);

        var view = Vector3.Normalize(pos);
        float es = -Vector3.Dot(view, planeNormal);
        float e0 = MathF.Max(-planeW, 0f);
        Vector3 intV = planeW > 0f ? view * planeW / es : Vector3.Zero;
        float depth = (pos - intV).Length();
        float l = MathF.Max(depth, 0.1f);

        const float f = 0.98f;
        float t1 = -kd * MathF.Pow(f, ks * e0);
        float t2 = kd + ks * es;
        float t3 = MathF.Pow(f, t2 * l) - 1f;
        float lum = MathF.Pow(MathF.Min(t1 / t2 * t3, 1f), 1f / 1.7f);
        float d = MathF.Pow(0.98f, l * kd);
        return (kc * lum, d);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float tol = 1e-5f)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, tol);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, tol);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, tol);
    }

    /// <summary>Straight down through 4 m of default water from 10 m up, ks = 1 (sun overhead): the
    /// numbers were worked out by hand from the viewer's formula (L = 0.319205, D = 0.850763).</summary>
    [Fact]
    public void StraightDown_MatchesHandWorkedNumbers()
    {
        // distance = 14 m, depth 4 m, es = 1.
        var fog = WaterFog.Compute(10f, -4f, 14f, 2f, 1f, DefaultColorLinear);

        Assert.InRange(fog.Transmittance, 0.850763f - 1e-4f, 0.850763f + 1e-4f);
        AssertClose(DefaultColorLinear * 0.319205f, fog.Radiance, 1e-5f);
    }

    [Theory]
    [InlineData(10f, -4f, 0f, 2f, 1f)]            // overhead, 4 m
    [InlineData(2f, -0.5f, 4.9f, 2f, 3.3333333f)]  // a shallow beach, low sun
    [InlineData(1.5f, -1f, 39f, 2f, 2f)]           // grazing: 1.5 m eye, point 1 m under, far off
    [InlineData(10f, -4f, 0f, 0.5f, 1f)]           // clear water
    [InlineData(10f, -4f, 0f, 16f, 1f)]            // murky water
    [InlineData(30f, -0.2f, 80f, 2f, 1.5f)]
    public void MatchesTheViewersVectorForm(float eye, float point, float offsetX, float kd, float ks)
    {
        var expected = Reference(eye, point, offsetX, kd, ks, DefaultColorLinear);

        float dist = new Vector2(offsetX, eye - point).Length();
        var actual = WaterFog.Compute(eye, point, dist, kd, ks, DefaultColorLinear);

        AssertClose(expected.Radiance, actual.Radiance, 1e-5f);
        Assert.InRange(MathF.Abs(expected.D - actual.Transmittance), 0f, 1e-5f);
    }

    [Fact]
    public void ABodyAboveTheSurface_IsNotFogged()
    {
        var fog = WaterFog.Compute(10f, 0.001f, 12f, 2f, 1f, DefaultColorLinear);
        Assert.Equal(WaterFog.Fog.None, fog);
    }

    [Fact]
    public void AnUnderwaterCamera_IsNotModelled()
    {
        // Eye 3 m below the surface: the viewer takes a different branch (e0 > 0, no intersection);
        // we deliberately do nothing there yet, so the camera-above result must not leak into it.
        Assert.Equal(WaterFog.Fog.None, WaterFog.Compute(-3f, -8f, 6f, 2f, 1f, DefaultColorLinear));
        Assert.Equal(WaterFog.Fog.None, WaterFog.Compute(0f, -8f, 6f, 2f, 1f, DefaultColorLinear));
    }

    [Fact]
    public void NanInputs_FallBackToNoFog()
    {
        Assert.Equal(WaterFog.Fog.None, WaterFog.Compute(float.NaN, -1f, 5f, 2f, 1f, DefaultColorLinear));
        Assert.Equal(WaterFog.Fog.None, WaterFog.Compute(5f, -1f, float.NaN, 2f, 1f, DefaultColorLinear));
        Assert.Equal(WaterFog.Fog.None, WaterFog.Compute(5f, -1f, 0f, 2f, 1f, DefaultColorLinear));
    }

    [Fact]
    public void ANegativeDensity_NeverProducesNan()
    {
        var fog = WaterFog.Compute(10f, -4f, 14f, -1f, 1f, DefaultColorLinear);
        Assert.False(float.IsNaN(fog.Radiance.X) || float.IsNaN(fog.Radiance.Y) || float.IsNaN(fog.Radiance.Z));
        Assert.False(float.IsNaN(fog.Transmittance));
    }

    [Fact]
    public void ThickerWater_AddsMoreColourAndKeepsLess()
    {
        float lastRadiance = -1f, lastTransmittance = 2f;
        foreach (float depth in new[] { 0.2f, 0.5f, 1f, 2f, 5f, 10f, 40f })
        {
            // straight down from 10 m
            var fog = WaterFog.Compute(10f, -depth, 10f + depth, 2f, 1f, DefaultColorLinear);
            Assert.True(fog.Radiance.Z > lastRadiance, $"radiance did not grow at {depth} m");
            Assert.True(fog.Transmittance < lastTransmittance, $"transmittance did not fall at {depth} m");
            lastRadiance = fog.Radiance.Z;
            lastTransmittance = fog.Transmittance;
        }
    }

    [Fact]
    public void AtTheSurface_TheFogIsContinuousWithNone()
    {
        // A point 1 mm under the plane: the thinnest-slab floor keeps it a hair from None, no jump at the
        // waterline (which would draw a visible seam across every blended face crossing it).
        var fog = WaterFog.Compute(10f, -0.001f, 10.001f, 2f, 1f, DefaultColorLinear);
        Assert.InRange(fog.Transmittance, 0.99f, 1f);
        Assert.InRange(fog.Radiance.Z, 0f, 0.003f);
    }

    [Fact]
    public void FarAway_SaturatesAtTheViewersCeiling()
    {
        // A sky pixel behind the water has no depth; the shader feeds a huge distance. L tends to
        // (kd / (kd + ks*es))^(1/1.7), D to 0.
        const float kd = 2f, ks = 1f;
        float es = 0.5f; // a ray at 30 degrees below horizontal
        float eye = 10f;
        float dist = 100000f;
        float pointHeight = eye - es * dist; // far below
        var fog = WaterFog.Compute(eye, pointHeight, dist, kd, ks, Vector3.One);

        float limit = MathF.Pow(kd / (kd + ks * es), 1f / 1.7f);
        Assert.InRange(fog.Radiance.X, limit - 1e-4f, limit + 1e-4f);
        Assert.InRange(fog.Transmittance, 0f, 1e-6f);
    }

    [Theory]
    [InlineData(1f, 1f)]
    [InlineData(0.5f, 2f)]
    [InlineData(0.3f, 1f / 0.3f)]
    [InlineData(0.1f, 1f / 0.3f)]   // low sun: clamped
    [InlineData(-0.5f, 1f / 0.3f)]  // below the horizon: clamped
    public void Ks_IsOneOverTheClampedLightElevation(float lightHeight, float expected)
    {
        Assert.InRange(WaterFog.KsFromLightHeight(lightHeight), expected - 1e-5f, expected + 1e-5f);
    }

    [Fact]
    public void TheTwoCompositesDifferByTheGammaOnTransmittance()
    {
        var fog = WaterFog.Compute(10f, -4f, 14f, 2f, 1f, DefaultColorLinear);
        var colour = new Vector3(0.5f, 0.4f, 0.3f);

        // alpha surfaces: colour * D + rgb ; opaque haze: colour * D^1.7 + rgb
        AssertClose(colour * fog.Transmittance + fog.Radiance, WaterFog.ApplyToSurface(colour, fog));
        AssertClose(colour * MathF.Pow(fog.Transmittance, 1.7f) + fog.Radiance, WaterFog.ApplyHaze(colour, fog));
        Assert.True(WaterFog.ApplyHaze(colour, fog).X < WaterFog.ApplyToSurface(colour, fog).X);
    }

    [Fact]
    public void NoFog_LeavesAColourAlone()
    {
        var colour = new Vector3(0.5f, 0.4f, 0.3f);
        Assert.Equal(colour, WaterFog.ApplyHaze(colour, WaterFog.Fog.None));
        Assert.Equal(colour, WaterFog.ApplyToSurface(colour, WaterFog.Fog.None));
    }

    [Theory]
    [InlineData(-1.0f)]    // straight down
    [InlineData(-0.5f)]    // 30 deg below the horizon
    [InlineData(0.0f)]     // at the horizon
    [InlineData(0.5f)]     // looking up through it: clamped to 0
    public void FlatSurfaceTransmission_FollowsCalculateFresnelFactors(float viewDotUp)
    {
        // The expected values are recomputed here from the viewer's three-term form, not copied.
        const float scale = 0.3999f, offset = 0.5f;
        float one = MathF.Max(0f, viewDotUp * scale + offset);
        float expected = 1f - MathF.Min(1f, one * one + one * one + one * one);

        Assert.InRange(WaterFog.FlatSurfaceTransmission(viewDotUp, scale, offset), expected - 1e-6f, expected + 1e-6f);
    }

    [Fact]
    public void FlatSurfaceTransmission_HandWorkedNumbers()
    {
        // straight down: d = -1*0.3999 + 0.5 = 0.1001, 3 d^2 = 0.030060, T = 0.969940
        Assert.InRange(WaterFog.FlatSurfaceTransmission(-1f, 0.3999f, 0.5f), 0.96993f, 0.96995f);
        // grazing (at the horizon): d = 0.5, 3 d^2 = 0.75, T = 0.25
        Assert.InRange(WaterFog.FlatSurfaceTransmission(0f, 0.3999f, 0.5f), 0.24999f, 0.25001f);
    }

    [Fact]
    public void ABlendedFaceIsFoggedFirstAndThenWeighedByTheSurface()
    {
        var fog = WaterFog.Compute(10f, -4f, 14f, 2f, 1f, DefaultColorLinear);
        var colour = new Vector3(0.5f, 0.4f, 0.3f);
        const float t = 0.6f;

        AssertClose((colour * fog.Transmittance + fog.Radiance) * t, WaterFog.ApplyToBlendedSurface(colour, fog, t));
        // ... and with no fog and a transparent surface it is the face's own colour.
        Assert.Equal(colour, WaterFog.ApplyToBlendedSurface(colour, WaterFog.Fog.None, 1f));
    }

    [Theory]
    [InlineData(-1.0f)]
    [InlineData(-0.5f)]
    [InlineData(-0.2f)]
    [InlineData(0.0f)]
    public void FlatSurfaceReflection_IsWhatTheTransmissionDoesNotLetThrough(float viewDotUp)
    {
        // df2.x is shared: transmission = 1 - df2.x, reflection = df2.x * df2.y. Check the shared part, then
        // the extra factor df2.y = d (straight from the bracket of calculateFresnelFactors).
        const float scale = 0.3999f, offset = 0.5f;
        float d = MathF.Max(0f, viewDotUp * scale + offset);
        float df2x = 1f - WaterFog.FlatSurfaceTransmission(viewDotUp, scale, offset);

        Assert.InRange(WaterFog.FlatSurfaceReflectionWeight(viewDotUp, scale, offset),
            df2x * d - 1e-6f, df2x * d + 1e-6f);
    }

    [Fact]
    public void AtTheHorizonTheSurfaceReflectsMostAndLooksStraightDownItReflectsAlmostNothing()
    {
        float grazing = WaterFog.FlatSurfaceReflectionWeight(0f, 0.3999f, 0.5f);   // 0.75 * 0.5
        float overhead = WaterFog.FlatSurfaceReflectionWeight(-1f, 0.3999f, 0.5f); // 0.03006 * 0.1001
        Assert.InRange(grazing, 0.37499f, 0.37501f);
        Assert.InRange(overhead, 0.00300f, 0.00302f);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SLNG.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("SLNG.sln not found above the test binaries");
    }

    /// <summary>A blended prim variant draws AFTER the water (BUG-RENDER-05) and so must fog itself; an opaque or
    /// scissored or hashed one is fogged through the frame the water refracts, and must NOT, or it would be fogged
    /// twice. Nothing in the compiler ties a new variant to either rule, so this does. Godot puts a face in the
    /// transparent pass when its shader writes ALPHA without ALPHA_SCISSOR_THRESHOLD / ALPHA_HASH_SCALE /
    /// ALPHA_ANTIALIASING_EDGE -- not by its render_mode, which spells blend_mix on every variant.</summary>
    [Fact]
    public void OnlyTheBlendedWorldVariantsFogThemselves()
    {
        string dir = Path.Combine(RepoRoot(), "app", "materials", "prim");
        // The HUD is screen-space (no water to be under); prim_depth_core is a depth-only companion pass;
        // prim_mirror replaces a surface with a planar reflection and has its own lighting.
        string[] exempt = { "prim_blend_hud.gdshader", "prim_depth_core.gdshader", "prim_mirror.gdshader" };

        foreach (string path in Directory.GetFiles(dir, "*.gdshader"))
        {
            string name = Path.GetFileName(path);
            string code = Regex.Replace(File.ReadAllText(path), @"//[^
]*", "");
            bool writesAlpha = Regex.IsMatch(code, @"^\s*ALPHA\s*=", RegexOptions.Multiline);
            bool cutout = Regex.IsMatch(code, "ALPHA_SCISSOR_THRESHOLD|ALPHA_HASH_SCALE|ALPHA_ANTIALIASING_EDGE");
            bool drawsAfterWater = writesAlpha && !cutout && !exempt.Contains(name);
            bool fogs = code.Contains("#define SLNG_WATER_FOG_BLEND");

            Assert.True(drawsAfterWater == fogs,
                drawsAfterWater
                    ? $"{name} is blended (drawn after the water) but does not #define SLNG_WATER_FOG_BLEND"
                    : $"{name} is not a blended world variant but #defines SLNG_WATER_FOG_BLEND: it would be fogged twice");
        }
    }

    /// <summary>The numbers the GLSL port and this mirror must share. A string check, because the shader cannot
    /// be run from a unit test; it fails when one side is edited and the other is not.</summary>
    [Fact]
    public void TheShaderCarriesTheMirrorsConstants()
    {
        string glsl = File.ReadAllText(Path.Combine(RepoRoot(), "app", "materials", "slng_water_fog.gdshaderinc"));

        Assert.Contains("float F = 0.98;", glsl);                              // WaterFog.FogBase
        Assert.Contains("max(dist - eye_h / es, 0.1)", glsl);                  // WaterFog.MinThickness
        Assert.Contains("pow(scatter, 1.0 / 1.7)", glsl);                      // WaterFog.Gamma
        Assert.Contains("float t1 = -kd;", glsl);                              // e0 = 0 above the surface
        Assert.Contains("float t2 = kd + ks * es;", glsl);
        Assert.Contains("3.0 * d * d", glsl);                                  // FlatSurfaceTransmission
        Assert.Equal(0.98f, WaterFog.FogBase);
        Assert.Equal(0.1f, WaterFog.MinThickness);
        Assert.Equal(1.7f, WaterFog.Gamma);

        string water = File.ReadAllText(Path.Combine(RepoRoot(), "app", "materials", "water.gdshader"));
        Assert.Contains("pow(fog.a, 1.7)", water);                            // ApplyHaze: transmittance ^ Gamma
    }
}
