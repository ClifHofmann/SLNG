using System;
using System.IO;
using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// The legacy half of slng_shade, by value. The case that matters is the Millenium terrace floor
/// (mesh 329749214, legacy material f9c12f38: gloss 30, environment 5, white specular map tinted
/// beige, an RGB normal map). On paper it is a matte terracotta; before the mirror lobe was
/// weighted by its intensity, ANY non-zero environment intensity forced roughness to exactly 0 --
/// a glass floor.
/// </summary>
public class LegacyShadeMirrorTests
{
    private static LegacyShadeMirror.Inputs Floor(float env = 5f / 255f) => new(
        HasSpecularTexture: true,
        SpecularTexel: Vector3.One,
        SpecularTint: new Vector3(0.87f, 0.80f, 0.72f),
        HasNormalTexture: true,
        NormalAlpha: 1f,
        SpecularGlossiness: 30f / 255f,
        SpecularEnvironment: env,
        LegacyShininess: 0f,
        MetallicFactor: 0f,
        RoughnessFactor: 1f,
        Albedo: new Vector3(0.7f, 0.4f, 0.3f));

    [Fact]
    public void GlossinessMapsToTheViewersLutExponent()
    {
        // n = 0.1176^2 * 368 = 5.09 ; sqrt(2 / (n + 2)) = 0.531
        Assert.Equal(0.531f, LegacyShadeMirror.GlossinessToRoughness(30f / 255f), 3);
        Assert.Equal(1f, LegacyShadeMirror.GlossinessToRoughness(0f), 5);
    }

    [Fact]
    public void ATerraceFloorWithEnvironmentFiveStaysMatte()
    {
        var r = LegacyShadeMirror.Predict(Floor());

        // Not the 0.531 of the glossiness alone (the mirror lobe is blended in a little, 5/255 of
        // the way to full), but nowhere near the 0 it used to be forced to.
        Assert.InRange(r.Roughness, 0.45f, 0.531f);
        // Metallic ~ env * specular = 0.0196 * 0.81, then the env mix: about 0.04 -- a dielectric.
        Assert.InRange(r.Metallic, 0f, 0.06f);
        Assert.InRange(r.Specular, 0.78f, 0.84f);
        // Albedo barely moves toward grey.
        Assert.True(Vector3.Distance(r.Albedo, new Vector3(0.7f, 0.4f, 0.3f)) < 0.01f);
    }

    [Theory]
    [InlineData(0.25f)]   // Shiny LOW
    [InlineData(0.5f)]    // Shiny MEDIUM
    [InlineData(0.75f)]   // Shiny HIGH
    [InlineData(1f)]      // a full-strength mirror material
    public void FromShinyLowUpAMirrorIsExactlyAsSharpAsBefore(float env)
    {
        var viaMap = LegacyShadeMirror.Predict(Floor(env));
        Assert.Equal(0f, viaMap.Roughness);

        var viaShiny = LegacyShadeMirror.Predict(new LegacyShadeMirror.Inputs(
            false, Vector3.One, Vector3.One, false, 1f, 0.2f, 0f, env, 0f, 1f, Vector3.One));
        Assert.Equal(0f, viaShiny.Roughness);
        Assert.Equal(env, viaShiny.Metallic, 4);
    }

    [Fact]
    public void BelowShinyLowTheMirrorLobeRampsInRatherThanSnapping()
    {
        float gloss = LegacyShadeMirror.GlossinessToRoughness(30f / 255f);
        float previous = gloss + 1e-4f;
        foreach (float env in new[] { 0f, 0.02f, 0.05f, 0.1f, 0.2f, 0.25f })
        {
            float roughness = LegacyShadeMirror.Predict(Floor(env)).Roughness;
            Assert.True(roughness <= previous, $"roughness must fall as the environment intensity rises ({env})");
            previous = roughness;
        }
        // No environment at all: the glossiness is all there is.
        Assert.Equal(gloss, LegacyShadeMirror.Predict(Floor(0f)).Roughness, 4);
    }

    [Fact]
    public void NoSpecularMapAndNoShinyIsPlainMatte()
    {
        var r = LegacyShadeMirror.Predict(new LegacyShadeMirror.Inputs(
            false, Vector3.One, Vector3.One, false, 1f, 0.9f, 0.9f, 0f, 0f, 1f, Vector3.One));

        // The viewer reads neither the material's glossiness nor its environment without a map.
        Assert.Equal(1f, r.Roughness);
        Assert.Equal(0f, r.Metallic);
        Assert.Equal(0f, r.Specular);
    }

    [Fact]
    public void ANormalMapAlphaOfZeroKillsTheGlossAndTheHighlight()
    {
        // The shape of the failure a mis-imported normal map would give: alpha 0 -> glossiness 0
        // -> roughness 1, specular 0. (Kept so the dump's reading of the alpha is interpretable.)
        var inputs = Floor(0f) with { NormalAlpha = 0f };
        var r = LegacyShadeMirror.Predict(inputs);
        Assert.Equal(1f, r.Roughness, 4);
        Assert.Equal(0f, r.Specular);
    }

    [Fact]
    public void TheShaderCarriesTheSameConstantAndTheSameRamp()
    {
        // The mirror is only worth anything while it says what the shader says. The shader cannot be
        // run here, so pin the two lines it is made of.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "app", "materials", "prim", "prim_common.gdshaderinc")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var shader = File.ReadAllText(Path.Combine(dir!.FullName, "app", "materials", "prim", "prim_common.gdshaderinc"));

        Assert.Contains("const float MIRROR_FULL_SHARPNESS_ENV = " + LegacyShadeMirror.MirrorFullSharpnessEnvironment.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ";", shader);
        Assert.Contains("out_roughness = mix(out_roughness, 0.0, clamp(env_intensity / MIRROR_FULL_SHARPNESS_ENV, 0.0, 1.0));", shader);
        // and the old unconditional snap is gone
        Assert.DoesNotContain("out_roughness = 0.0;" + Environment.NewLine + "        out_metallic", shader);
        Assert.DoesNotContain("out_roughness = 0.0;\n        out_metallic", shader);
    }

    [Fact]
    public void TheMaterialLabScaleIsDeclaredRegisteredAtItsDefaultAndAppliedAfterTheMetallicFold()
    {
        // The lab must change nothing until the slider is moved: the project.godot initial value is
        // the default, and the shader multiplies SPECULAR only inside the specular-map branch and
        // only AFTER the Environment-slider metallic fold (so the mirror path is untouched).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "app", "project.godot")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var shader = File.ReadAllText(Path.Combine(dir!.FullName, "app", "materials", "prim", "prim_common.gdshaderinc"));
        var project = File.ReadAllText(Path.Combine(dir.FullName, "app", "project.godot"));
        string name = LegacyShadeMirror.LegacySpecularScaleUniform;

        Assert.Contains("global uniform float " + name + ";", shader);
        string value = LegacyShadeMirror.DefaultLegacySpecularScale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        var entry = System.Text.RegularExpressions.Regex.Match(project,
            System.Text.RegularExpressions.Regex.Escape(name) + @"=\{\s*""type"": ""float"",\s*""value"": " + System.Text.RegularExpressions.Regex.Escape(value) + @"\s*\}");
        Assert.True(entry.Success, name + " must be registered in project.godot as a float with the default value");

        int fold = shader.IndexOf("out_metallic = clamp(metallic_factor + env_intensity * out_specular, 0.0, 1.0);", StringComparison.Ordinal);
        int scale = shader.IndexOf("out_specular *= " + name + ";", StringComparison.Ordinal);
        int legacyBranch = shader.IndexOf("else if (legacy_shininess > 0.0)", StringComparison.Ordinal);
        Assert.True(fold > 0 && scale > fold && scale < legacyBranch, "the scale must follow the metallic fold inside the specular-map branch");
        Assert.Equal(1, shader.Split("out_specular *= " + name).Length - 1);
        Assert.Equal(1.0f, LegacyShadeMirror.DefaultLegacySpecularScale);
    }
}
