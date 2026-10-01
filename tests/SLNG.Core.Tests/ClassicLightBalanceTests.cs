using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// The Millenium terrace at SLT 12:15 (log of the v0.25.9 session): sun elevation 34.86 degrees,
/// ambient (0.291, 0.42, 0.506), cloud_shadow 0.52 so tmpAmbient (0.475, 0.571, 0.634), and a red
/// SunDiffuse of 0.911 * 0.8551 (light attenuation) * 0.725 (transmittance) = 0.565. Green and blue
/// of the sun were not logged; they are assumed here and only the RED numbers are asserted.
/// </summary>
public class ClassicLightBalanceTests
{
    private static readonly Vector3 TmpAmbient = new(0.4753f, 0.5708f, 0.6344f);
    private static readonly Vector3 SunDiffuse = new(0.565f, 0.38f, 0.31f); // G, B assumed
    private static readonly float Sine = MathF.Sin(34.86f * MathF.PI / 180f);

    [Fact]
    public void TheShadowedEndpointIsMatchedExactly()
    {
        var r = ClassicLightBalance.Compute(SunDiffuse, TmpAmbient, Sine);

        // linear(0.9 * pow(0.4753, 0.9) * 0.57) * 1.1 = 0.0616 -- this is also the 0.104 ambient
        // energy the log prints (its largest channel is blue, 0.1045).
        Assert.Equal(0.0616f, r.Viewer.Shadow.X, 3);
        Assert.Equal(r.Viewer.Shadow, r.Godot.Shadow);
        Assert.InRange(r.Viewer.Shadow.Z, 0.1040f, 0.1050f);
    }

    [Fact]
    public void ViewerAndGodotAgreeOnTheDiffuseContrastAtThisSun()
    {
        var r = ClassicLightBalance.Compute(SunDiffuse, TmpAmbient, Sine);

        // viewer: srgb_to_linear(0.2626 + srgb(0.5716^1.2) * 0.534) * 1.1 = 0.431
        Assert.Equal(0.431f, r.Viewer.Lit.X, 2);
        // Godot: 0.0616 + (0.658 - 0.0616) * 0.5716 = 0.4025
        Assert.Equal(0.4025f, r.Godot.Lit.X, 2);

        // lit : shadow, red -- 7.0 against 6.5. Close. Diffuse lighting alone does not explain a
        // washed-out, low-contrast floor.
        Assert.InRange(r.Viewer.Ratio.X, 6.8f, 7.2f);
        Assert.InRange(r.Godot.Ratio.X, 6.3f, 6.7f);
        Assert.True(r.Godot.Lit.X / r.Viewer.Lit.X > 0.9f);
    }

    [Fact]
    public void TheTwoDivergeAtAGrazingSunBecauseTheViewerEncodesNdotL()
    {
        // At 10 degrees the viewer's sun term is srgb(0.174^1.2) = 0.35 where Godot's linear N.L is
        // 0.174: the viewer is markedly brighter on a low sun (a known, separate difference).
        var r = ClassicLightBalance.Compute(SunDiffuse, TmpAmbient, MathF.Sin(10f * MathF.PI / 180f));

        Assert.True(r.Viewer.Lit.X > r.Godot.Lit.X * 1.15f);
        Assert.Equal(r.Viewer.Shadow, r.Godot.Shadow);
    }

    [Fact]
    public void AtNdotLOneTheTwoEndpointsCoincide()
    {
        var r = ClassicLightBalance.Compute(SunDiffuse, TmpAmbient, 1f);

        Assert.Equal(r.Viewer.Lit.X, r.Godot.Lit.X, 3);
        Assert.Equal(r.Viewer.Lit.Z, r.Godot.Lit.Z, 3);
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
        var r = ClassicLightBalance.Compute(SunDiffuse, TmpAmbient, Sine);
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
