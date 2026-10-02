using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// The Millenium terrace at SLT 12:15 (log of the v0.25.9 session): sun elevation 34.86 degrees,
/// ambient (0.291, 0.42, 0.506), cloud_shadow 0.52 so tmpAmbient (0.475, 0.571, 0.634). The surface
/// sunlit is the raw sunlight_color (0.911, 0.618, 0.529) times exp(-light_atten / sin(elevation)) =
/// 0.8551 -- what atmosphericsFuncs.glsl:60-73 hands the lighting. (The logged SunDiffuse of
/// (0.565, 0.384, 0.328) is that times a Beer's-law transmittance 0.725 the surface shaders never
/// apply; blue_density is taken as uniform, which the logged G and B SunDiffuse confirm to 0.1%.)
/// </summary>
public class ClassicLightBalanceTests
{
    private static readonly Vector3 TmpAmbient = new(0.4753f, 0.5708f, 0.6344f);
    private static readonly Vector3 SurfaceSunlit = new Vector3(0.911f, 0.618f, 0.529f) * 0.8551f;
    private static readonly float Sine = MathF.Sin(34.86f * MathF.PI / 180f);

    [Fact]
    public void TheShadowedEndpointMatchesPerChannelWithTheOrientationAveragedAmbientFactor()
    {
        var r = ClassicLightBalance.Compute(SurfaceSunlit, TmpAmbient, Sine);

        // viewer, horizontal floor: amblit * ambientLighting(0.918) -> srgb_to_linear(.) * 1.1
        Assert.Equal(0.0522f, r.Viewer.Shadow.X, 3);
        Assert.Equal(0.0723f, r.Viewer.Shadow.Y, 3);
        Assert.Equal(0.0877f, r.Viewer.Shadow.Z, 3);
        // godot: the same with the orientation average 11/12 = 0.9167 (the floor sits at 0.9183)
        Assert.Equal(0.0520f, r.Godot.Shadow.X, 3);
        Assert.Equal(0.0721f, r.Godot.Shadow.Y, 3);
        Assert.Equal(0.0874f, r.Godot.Shadow.Z, 3);
        Assert.Equal(1f, r.Godot.Shadow.X / r.Viewer.Shadow.X, 2);
        Assert.Equal(1f, r.Godot.Shadow.Z / r.Viewer.Shadow.Z, 2);
    }

    [Fact]
    public void TheAmbientFactorIsTheViewersAndItsAverageIsElevenTwelfths()
    {
        Assert.Equal(1f, ClassicLightBalance.AmbientLightingFactor(0f));
        Assert.Equal(0.75f, ClassicLightBalance.AmbientLightingFactor(1f));
        Assert.Equal(0.75f, ClassicLightBalance.AmbientLightingFactor(-1f));
        Assert.Equal(0.9183f, ClassicLightBalance.AmbientLightingFactor(Sine), 3);
        // mean of 1 - (x/2)^2 over x in [0,1]
        double mean = 0;
        for (int i = 0; i < 10000; i++) { double x = (i + 0.5) / 10000; mean += 1 - x * x / 4; }
        Assert.Equal(ClassicLightBalance.OrientationAveragedAmbientFactor, (float)(mean / 10000), 4);
    }

    [Fact]
    public void ViewerAndGodotAgreeOnTheDiffuseContrastAtThisSun()
    {
        var r = ClassicLightBalance.Compute(SurfaceSunlit, TmpAmbient, Sine);

        Assert.Equal(0.6418f, r.Viewer.Lit.X, 3);
        Assert.Equal(0.4256f, r.Viewer.Lit.Y, 3);
        Assert.Equal(0.3905f, r.Viewer.Lit.Z, 2);
        Assert.Equal(0.6185f, r.Godot.Lit.X, 3);
        Assert.Equal(0.3930f, r.Godot.Lit.Y, 3);
        Assert.Equal(0.3559f, r.Godot.Lit.Z, 3);

        // lit : shadow -- 12.3 / 5.9 / 4.5 in the viewer, 11.9 / 5.5 / 4.1 here. (With the SunDiffuse
        // transmittance the old setup gave 6.5 / 3.4 / 2.7 -- the weak sunlit brightening.)
        Assert.InRange(r.Viewer.Ratio.X, 12.0f, 12.6f);
        Assert.InRange(r.Godot.Ratio.X, 11.6f, 12.2f);
        Assert.InRange(r.Godot.Ratio.Z, 4.0f, 4.2f);
        // within 4% / 8% / 9% of the viewer in R / G / B: only the N.L encoding is left
        Assert.True(r.Godot.Lit.X / r.Viewer.Lit.X > 0.96f);
        Assert.True(r.Godot.Lit.Y / r.Viewer.Lit.Y > 0.91f);
        Assert.True(r.Godot.Lit.Z / r.Viewer.Lit.Z > 0.90f);
    }

    [Fact]
    public void TheTransmittanceThatSunDiffuseCarriesWasWhatMadeTheSunFarTooDim()
    {
        // The pre-fix input: SunDiffuse = SurfaceSunlit * 0.7255 (R 0.565, G 0.384, B 0.328).
        var dim = ClassicLightBalance.Compute(new Vector3(0.565f, 0.384f, 0.328f), TmpAmbient, Sine);
        var right = ClassicLightBalance.Compute(SurfaceSunlit, TmpAmbient, Sine);

        Assert.Equal(0.3755f, dim.Godot.Lit.X, 2);        // what Godot was lighting a white floor with (red)
        Assert.True(right.Viewer.Lit.X > dim.Godot.Lit.X * 1.5f);   // the viewer's lit floor is 70% brighter in red
        Assert.True(right.Godot.Ratio.X > dim.Godot.Ratio.X * 1.5f);
    }

    [Fact]
    public void TheTwoDivergeAtAGrazingSunBecauseTheViewerEncodesNdotL()
    {
        // At 10 degrees the viewer's sun term is srgb(0.174^1.2) = 0.35 where Godot's linear N.L is
        // 0.174: the viewer is markedly brighter on a low sun (a known, separate difference).
        var r = ClassicLightBalance.Compute(SurfaceSunlit, TmpAmbient, MathF.Sin(10f * MathF.PI / 180f));

        Assert.True(r.Viewer.Lit.X > r.Godot.Lit.X * 1.15f);
    }

    [Fact]
    public void AtNdotLOneTheLitEndpointsDifferOnlyByTheViewersAmbientFactor()
    {
        // N.L = 1: the viewer's own ambient factor is 0.75 where Godot's flat ambient carries 0.9167,
        // so the viewer is ~10% darker in red there; sun terms are identical.
        var r = ClassicLightBalance.Compute(SurfaceSunlit, TmpAmbient, 1f);
        Assert.InRange(r.Godot.Lit.X / r.Viewer.Lit.X, 1.05f, 1.15f);
    }

    [Fact]
    public void TheHighlightSunColourIsTheViewersBareSunlitNotTheDiffuseRadiance()
    {
        var spec = ClassicLightBalance.ViewerSunlitForSpecular(SurfaceSunlit);
        Assert.Equal(1.2337f, spec.X, 3);
        Assert.Equal(0.5141f, spec.Y, 3);
        Assert.Equal(0.3643f, spec.Z, 3);

        // and it is NOT what the diffuse path puts on the surface: (lit@N.L=1 - shadow) is whiter
        var r1 = ClassicLightBalance.Compute(SurfaceSunlit, TmpAmbient, 1f);
        var diffuseSun = r1.Godot.Lit - r1.Godot.Shadow;
        Assert.True(diffuseSun.Y / diffuseSun.X > spec.Y / spec.X * 1.15f);
    }

    [Fact]
    public void TheSpecularVeilOnALegacyGlossyFaceIsFarAboveTheViewers()
    {
        // The terrace floor: gloss 30/255, specular map white tinted (0.87, 0.80, 0.72), luminance
        // 0.81 -> shader SPECULAR 0.81; reflection probe Intensity 1.5 (Boot).
        float viewer = ClassicLightBalance.ViewerGlossEnvWeight(0.81f, 30f / 255f);
        float godot = ClassicLightBalance.GodotSpecularWeight(0.81f, 1.5f);

        Assert.Equal(0.0021f, viewer, 3);   // 0.5 * 0.81 * 0.09 * 0.1176 * 0.5
        Assert.Equal(0.0972f, godot, 3);    // 0.08 * 0.81 * 1.5
        Assert.True(godot / viewer > 40f, $"Godot reflects {godot / viewer:0}x what the viewer does");
    }

    [Fact]
    public void TheVeilIsLargerThanTheShadowedDiffuseOnATerracottaFloor()
    {
        var r = ClassicLightBalance.Compute(SurfaceSunlit, TmpAmbient, Sine);
        // A terracotta in linear light, and a sky reflected at about this radiance (estimate: the
        // dome's red/green/blue toward the sky at the floor's mirror angle).
        var albedo = new Vector3(0.52f, 0.17f, 0.07f);
        var sky = new Vector3(0.17f, 0.25f, 0.40f);
        float godot = ClassicLightBalance.GodotSpecularWeight(0.81f, 1.5f);
        float viewer = ClassicLightBalance.ViewerGlossEnvWeight(0.81f, 30f / 255f);

        var shadowDiffuse = albedo * r.Godot.Shadow;
        var godotVeil = sky * godot;
        var viewerVeil = sky * viewer;

        // blue: the veil is several times the shadowed diffuse (a pale blue-grey shadow); the
        // viewer's own is a few percent of it.
        Assert.True(godotVeil.Z > 3f * shadowDiffuse.Z);
        Assert.True(viewerVeil.Z < 0.2f * shadowDiffuse.Z);
        // and in the lit area it still adds a third of the green and more than the blue's diffuse.
        var litDiffuse = albedo * r.Godot.Lit;
        Assert.True(godotVeil.Y / litDiffuse.Y > 0.25f);
        Assert.True(godotVeil.Z > litDiffuse.Z);
    }
}
