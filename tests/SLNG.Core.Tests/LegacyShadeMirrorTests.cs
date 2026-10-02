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

        // The shipped defaults (v0.25.16): reflection scale 1 (viewer-faithful), viewer sun highlight on.
        Assert.Equal(1.0f, LegacyShadeMirror.DefaultLegacySpecularScale);
        Assert.True(LegacyShadeMirror.DefaultViewerSunSpecular);
        Assert.Equal("1.0", value);

        // Godot's own F0 is scaled ONLY by the stock Opaque variant (the checkbox-off fallback). The
        // viewer-specular twins scale the viewer's gloss reflection instead, inside
        // slng_viewer_gloss_env_specular, and every other variant (double-sided opaque, avatar, HUD,
        // mirror...) stays unscaled so those faces keep the specular they always had.
        var normalized = System.Text.RegularExpressions.Regex.Replace(shader, @"\s+", " ");
        Assert.Contains("#ifdef SLNG_LAB_SPECULAR_SCALE out_specular *= " + name + "; #endif", normalized);
        Assert.Contains("weight * " + name + " / (VSPEC_GODOT_F0_PER_SPECULAR * VSPEC_PROBE_INTENSITY)", normalized);
        var primDir = Path.Combine(dir.FullName, "app", "materials", "prim");
        foreach (var f in Directory.GetFiles(primDir, "*.gdshader"))
        {
            var text = File.ReadAllText(f);
            string file = Path.GetFileName(f);
            bool labExpected = file == "prim_opaque.gdshader";
            Assert.True(text.Contains("#define SLNG_LAB_SPECULAR_SCALE") == labExpected,
                file + (labExpected ? " must" : " must not") + " define SLNG_LAB_SPECULAR_SCALE");
            bool vspecExpected = file.EndsWith("_vspec.gdshader", StringComparison.Ordinal);
            Assert.True(text.Contains("#define SLNG_VIEWER_SPEC") == vspecExpected,
                file + (vspecExpected ? " must" : " must not") + " define SLNG_VIEWER_SPEC");
        }
    }

    // --- the viewer's sun highlight (material lab, prim_opaque_vspec.gdshader) ------------------

    [Fact]
    public void TheViewerLutAtTheTerraceFloorsGlossIsAWideLobe()
    {
        // gloss 30/255 -> n = 0.1176^2 * 368 = 5.09; normalisation (n+2)(n+4)/(8 pi (2^(-n/2) + n)).
        float g = 30f / 255f;
        Assert.Equal(5.093f, g * g * LegacyShadeMirror.ViewerSpecularExponentScale, 2);
        Assert.Equal(0.4875f, LegacyShadeMirror.ViewerSpecularLut(1f, g), 3);   // N.H = 1 -> the bare normalisation
        Assert.Equal(0.2850f, LegacyShadeMirror.ViewerSpecularLut(0.9f, g), 3); // still more than half at 25 degrees off
        Assert.Equal(0.0143f, LegacyShadeMirror.ViewerSpecularLut(0.5f, g), 3);
    }

    [Fact]
    public void TheViewerSunSpecularMatchesKnownGeometry()
    {
        float g = 30f / 255f;
        // everything aligned: lit 1, fres 0.5, gt/(nh nl) = 2 -> 0.5 * 0.4875 * 2 / ... = the LUT peak
        Assert.Equal(0.4875f, LegacyShadeMirror.ViewerSunSpecular(1f, 1f, 1f, 1f, g), 3);
        // sun 34.9 deg up, camera on the far side 35 deg up (mirror geometry): nl .5716 nh 1 nv .5736 vh .5726
        Assert.Equal(0.861f, LegacyShadeMirror.ViewerSunSpecular(0.5716f, 0.99999925f, 0.5736f, 0.5726f, g), 2);
        // same geometry from the sun's side: the lobe is almost gone
        Assert.True(LegacyShadeMirror.ViewerSunSpecular(0.5716f, 0.5726f, 0.5736f, 0.99999f, g) < 0.05f);
    }

    [Fact]
    public void TheViewerSunSpecularIsGatedShadowedAndFadesAtGrazingLight()
    {
        float g = 30f / 255f;
        // `if (spec.a > 0.0)`: glossiness 0 means no highlight at all
        Assert.Equal(0f, LegacyShadeMirror.ViewerSunSpecular(0.8f, 0.9f, 0.7f, 0.9f, 0f));
        // scol is multiplied by the shadow term
        float lit = LegacyShadeMirror.ViewerSunSpecular(0.6f, 0.95f, 0.6f, 0.7f, g, shadow: 1f);
        Assert.True(lit > 0.1f);
        Assert.Equal(lit * 0.25f, LegacyShadeMirror.ViewerSunSpecular(0.6f, 0.95f, 0.6f, 0.7f, g, shadow: 0.25f), 5);
        Assert.Equal(0f, LegacyShadeMirror.ViewerSunSpecular(0.6f, 0.95f, 0.6f, 0.7f, g, shadow: 0f));
        // lit = min(nl * 6, 1): a light skimming the surface (nl = 0.01) contributes next to nothing
        Assert.True(LegacyShadeMirror.ViewerSunSpecular(0.01f, 0.5f, 0.7f, 0.9f, g) < 0.002f);
    }

    [Fact]
    public void ATanTintScalesTheViewerHighlightByItsOwnColour()
    {
        // The highlight is `lit*scol * sunlit * spec.rgb`: for a warm tint the shader multiplies the
        // scalar above by the (linearised) colour -- the red channel keeps more of it than the blue.
        var tint = new Vector3(0.87f, 0.80f, 0.72f);
        var rgb = new Vector3(
            ClassicLightBalance.SrgbToLinear(tint.X), ClassicLightBalance.SrgbToLinear(tint.Y), ClassicLightBalance.SrgbToLinear(tint.Z));
        Assert.True(rgb.X > rgb.Y && rgb.Y > rgb.Z);
        Assert.Equal(0.7293f, rgb.X, 3);
        Assert.Equal(0.6038f, rgb.Y, 3);
        Assert.Equal(0.4770f, rgb.Z, 3);
    }

    private static string PrimDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "app", "materials", "prim", "prim_common.gdshaderinc")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "app", "materials", "prim");
    }

    [Fact]
    public void TheViewerSpecIncludeCarriesTheSameConstantsAndOnlyReplacesLightingInTheTwins()
    {
        var prim = PrimDir();
        var inc = File.ReadAllText(Path.Combine(prim, "prim_common.gdshaderinc"));
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        Assert.Contains("const float VSPEC_EXPONENT_SCALE = " + LegacyShadeMirror.ViewerSpecularExponentScale.ToString("0.0", inv) + ";", inc);
        Assert.Contains("const float VSPEC_LIT_NL_SCALE = 6.0;", inc);
        Assert.Contains("const float VSPEC_FRESNEL_BASE = 0.5;", inc);
        Assert.Contains("const float VSPEC_FRESNEL_SCALE = 0.4;", inc);
        Assert.Contains("float gt = max(0.0, min(2.0 * nh * nv / vh, 2.0 * nh * nl / vh));", inc);
        Assert.Contains("float lut = pow(nh, n) * ((n + 2.0) * (n + 4.0)) / (8.0 * PI * (exp2(-n / 2.0) + n));", inc);
        Assert.Contains("float scol = ATTENUATION * fres * lut * gt / (nh * nl);", inc);
        Assert.Contains("SPECULAR_LIGHT += lit * scol * radiance * v_vspec_rgb;", inc);
        Assert.Contains("global uniform vec3 slng_viewer_sunlit;", inc);
        Assert.Contains("radiance = slng_viewer_sunlit;", inc);
        Assert.Contains("if (v_vspec_gloss > 0.0)", inc);

        // the gloss/environment reflection constants, and the probe intensity they divide by
        Assert.Contains("const float VSPEC_GLOSS_ENV_WEIGHT = 0.25;", inc);
        Assert.Contains("const float VSPEC_GLOSS_ENV_FRESNEL_MIN = 0.3;", inc);
        Assert.Contains("const float VSPEC_GODOT_F0_PER_SPECULAR = " + LegacyShadeMirror.GodotF0PerSpecular.ToString("0.00", inv) + ";", inc);
        Assert.Contains("const float VSPEC_PROBE_INTENSITY = " + LegacyShadeMirror.ReflectionProbeIntensity.ToString("0.0", inv) + ";", inc);
        Assert.Contains("float fresnel = clamp(1.0 - nv, VSPEC_GLOSS_ENV_FRESNEL_MIN, 1.0);", inc);
        Assert.Contains("VSPEC_GLOSS_ENV_WEIGHT * spec_lum * fresnel * fresnel * glossiness * (1.0 - clamp(albedo_lum, 0.0, 1.0))", inc);

        // light() exists once, inside the SLNG_VIEWER_SPEC block of the include, and in NO shader file:
        // it replaces Godot's whole direct lighting, so only a twin may carry it.
        Assert.Equal(1, inc.Split("void light()").Length - 1);
        Assert.True(inc.IndexOf("#ifdef SLNG_VIEWER_SPEC", StringComparison.Ordinal) < inc.IndexOf("void light()", StringComparison.Ordinal));
        foreach (var f in Directory.GetFiles(prim, "*.gdshader"))
            Assert.DoesNotContain("void light()", File.ReadAllText(f));

        // and the stock Opaque variant is untouched by the experiment
        var opaque = File.ReadAllText(Path.Combine(prim, "prim_opaque.gdshader"));
        Assert.DoesNotContain("vspec", opaque, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryViewerSpecTwinIsItsBaseShaderPlusTheDefine()
    {
        // The twins are copies kept in lockstep by hand (Godot has no way to parametrise render_mode),
        // so pin them: from `shader_type` on, a twin must equal its base with the define in front of
        // the include. A uniform or a fragment() line added to a base and not to its twin fails here.
        var prim = PrimDir();
        string[] bases =
        {
            "prim_opaque", "prim_scissor", "prim_scissor_doublesided", "prim_scissor_edge", "prim_hash",
            "prim_blend", "prim_blend_doublesided", "prim_blend_depth", "prim_blend_prepass",
        };
        static string Body(string text)
        {
            text = text.Replace("\r\n", "\n");
            text = text.Substring(text.IndexOf("shader_type spatial;", StringComparison.Ordinal));
            // the stock Opaque's lab define belongs to the base only
            text = System.Text.RegularExpressions.Regex.Replace(text, @"// Material lab: this variant takes[^\n]*\n#define SLNG_LAB_SPECULAR_SCALE\n", "");
            text = text.Replace("// The viewer's legacy-specular lighting (custom light() + gloss/environment reflection).\n#define SLNG_VIEWER_SPEC\n", "");
            // and the twin's one block in fragment(): what light() reads back
            text = text.Replace("\n    // Twin only: what light() reads back (a varying can be assigned in fragment() only).\n"
                + "    vec4 vspec = slng_viewer_spec_capture(UV);\n"
                + "    v_vspec_rgb = vspec.rgb;\n"
                + "    v_vspec_gloss = vspec.a;\n", "");
            return text;
        }
        foreach (var b in bases)
        {
            var baseText = File.ReadAllText(Path.Combine(prim, b + ".gdshader"));
            var twinText = File.ReadAllText(Path.Combine(prim, b + "_vspec.gdshader"));
            Assert.Contains("#define SLNG_VIEWER_SPEC\n#include", twinText.Replace("\r\n", "\n"));
            Assert.Contains("v_vspec_gloss = vspec.a;", twinText);
            Assert.Equal(Body(baseText), Body(twinText));
        }
    }

    [Fact]
    public void TheViewerGlossEnvironmentReflectionIsNothingAtTheTerraceFloorAndGrowsWithGlossiness()
    {
        // applyGlossEnv: 0.25 * spec * fresnel^2 * glossiness * (1 - color), head-on fresnel = 0.3.
        // Terrace floor, gloss 30, specular luminance 0.81: ~0.0021 -- the upper bound the earlier probe
        // measured, i.e. no glass-like veil. (A pure white map: 0.0026.)
        Assert.Equal(0.0021f, ClassicLightBalance.ViewerGlossEnvWeight(0.81f, 30f / 255f), 4);   // the terrace floor's specular luminance
        Assert.Equal(0.00265f, ClassicLightBalance.ViewerGlossEnvWeight(1f, 30f / 255f), 4);   // a pure white map
        float g30 = LegacyShadeMirror.ViewerGlossEnvSpecular(1f, 30f / 255f, 1f, 0f);
        float g128 = LegacyShadeMirror.ViewerGlossEnvSpecular(1f, 128f / 255f, 1f, 0f);
        float g220 = LegacyShadeMirror.ViewerGlossEnvSpecular(1f, 220f / 255f, 1f, 0f);
        // SPECULAR = weight / (0.08 * 1.5)
        Assert.Equal(0.00265f / 0.12f, g30, 3);
        Assert.True(g30 < 0.03f, "gloss 30 must not bring the constant veil back");
        Assert.True(g128 > g30 * 4f && g220 > g128, "linear in glossiness");
        Assert.Equal(220f / 30f, g220 / g30, 2);
    }

    [Fact]
    public void TheViewerGlossEnvironmentReflectionFollowsTheFresnelRampAndTheOtherFactors()
    {
        float g = 220f / 255f;
        // fresnel = clamp(1 - N.V, 0.3, 1): flat below 1 - nv = 0.3, then (1 - nv)^2
        float head = LegacyShadeMirror.ViewerGlossEnvSpecular(1f, g, 1f, 0f);
        Assert.Equal(head, LegacyShadeMirror.ViewerGlossEnvSpecular(1f, g, 0.85f, 0f), 5);
        float mid = LegacyShadeMirror.ViewerGlossEnvSpecular(1f, g, 0.5f, 0f);
        Assert.Equal(head * (0.5f * 0.5f) / (0.3f * 0.3f), mid, 4);
        // saturates at SPECULAR 1 (F0 0.08 * probe 1.5 = 0.12 of the viewer's 0.22 peak)
        Assert.Equal(1f, LegacyShadeMirror.ViewerGlossEnvSpecular(1f, 1f, 0f, 0f));
        // no gloss, no reflection; a bright surface reflects less (1 - color); the colour scales it; the lab scales it
        Assert.Equal(0f, LegacyShadeMirror.ViewerGlossEnvSpecular(1f, 0f, 0.5f, 0f));
        Assert.Equal(head * 0.5f, LegacyShadeMirror.ViewerGlossEnvSpecular(1f, g, 1f, 0.5f), 5);
        Assert.Equal(0f, LegacyShadeMirror.ViewerGlossEnvSpecular(1f, g, 1f, 1f));
        Assert.Equal(head * 0.5f, LegacyShadeMirror.ViewerGlossEnvSpecular(0.5f, g, 1f, 0f), 5);
        Assert.Equal(head * 0.5f, LegacyShadeMirror.ViewerGlossEnvSpecular(1f, g, 1f, 0f, scale: 0.5f), 5);
        Assert.Equal(0f, LegacyShadeMirror.ViewerGlossEnvSpecular(1f, g, 1f, 0f, scale: 0f));
    }
}
