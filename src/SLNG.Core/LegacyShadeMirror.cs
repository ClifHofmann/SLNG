using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// A C# MIRROR of the legacy-material half of <c>slng_shade</c> (app/materials/prim/prim_common.gdshaderinc):
/// what roughness, metallic and specular the shader will end up writing for a face, given the
/// uniforms and texels it reads. It exists for the <c>--diag</c> click dump, so that the values a
/// live material was ACTUALLY given can be set beside the values the shader derives from them, and
/// for the tests that pin the arithmetic. It is not used for rendering: if the shader changes, this
/// has to change with it (the unit tests say what the two are expected to agree on).
/// </summary>
public static class LegacyShadeMirror
{
    /// <summary>The environment intensity at which a legacy face is a full, sharp mirror: Shiny LOW,
    /// the smallest value the build tool can produce (SHININESS_TO_ALPHA, llface.cpp:1420). Below it
    /// the sharp mirror lobe is blended in by <c>env / this</c>; at and above it the face is exactly
    /// as sharp as it always was. Same constant as <c>MIRROR_FULL_SHARPNESS_ENV</c> in the shader.</summary>
    public const float MirrorFullSharpnessEnvironment = 0.25f;

    /// <summary>The material lab's global shader uniform that scales the SPECULAR of a face with a
    /// specular map (Godot's SPECULAR drives the sun highlight AND the sky/probe reflection).
    /// Declared in prim_common.gdshaderinc and registered in project.godot.</summary>
    public const string LegacySpecularScaleUniform = "slng_legacy_specular_scale";

    /// <summary>1 = today's behaviour; the shader default and the project.godot value are pinned to
    /// it by a test, so merely shipping the lab changes nothing.</summary>
    public const float DefaultLegacySpecularScale = 1.0f;

    /// <summary>Upper end of the lab slider (the lower end is 0: no highlight and no reflection).</summary>
    public const float MaxLegacySpecularScale = 1.5f;

    /// <summary>RenderSpecularExponent, the default of the viewer's setting
    /// (app_settings/settings.xml:8484): the Blinn-Phong exponent of a face is
    /// <c>glossiness^2 * this</c> (pipeline.cpp:1466, <c>n = spec * spec * specExp</c>).</summary>
    public const float ViewerSpecularExponentScale = 368f;

    /// <summary>The viewer's specular lookup table, <c>texture(lightFunc, vec2(nh, glossiness)).r</c>,
    /// built in <c>LLPipeline::createLUTBuffers</c> (pipeline.cpp:1466-1475): a Blinn-Phong lobe
    /// <c>nh^n</c> times its normalisation <c>(n+2)(n+4) / (8 pi (2^(-n/2) + n))</c>.</summary>
    public static float ViewerSpecularLut(float nh, float glossiness)
    {
        float n = glossiness * glossiness * ViewerSpecularExponentScale;
        float lobe = MathF.Pow(Math.Clamp(nh, 0f, 1f), n);
        float norm = (n + 2f) * (n + 4f) / (8f * MathF.PI * (MathF.Pow(2f, -n / 2f) + n));
        return lobe * norm;
    }

    /// <summary>The scalar the viewer multiplies <c>sunlit_linear * spec.rgb</c> by for a legacy face's
    /// direct highlight: <c>lit * scol</c> from softenLightF.glsl:243-263 (the same expression
    /// materialF.glsl:154-170 uses for local lights) with
    /// <c>lit = min(nl * 6, 1)</c>, <c>fres = (1 - vh)^5 * 0.4 + 0.5</c>,
    /// <c>gt = max(0, min(2 nh nv / vh, 2 nh nl / vh))</c> and
    /// <c>scol = shadow * fres * LUT(nh, glossiness) * gt / (nh * nl)</c>.
    /// The dot products are clamped to [1e-6, 1] first, exactly as calcHalfVectors does
    /// (deferredUtil.glsl:114-128). Gated on <c>glossiness &gt; 0</c> (the viewer's
    /// <c>if (spec.a &gt; 0.0)</c>).</summary>
    public static float ViewerSunSpecular(float nl, float nh, float nv, float vh, float glossiness, float shadow = 1f)
    {
        if (glossiness <= 0f) return 0f;
        const float eps = 0.000001f;
        nl = Math.Clamp(nl, eps, 1f);
        nh = Math.Clamp(nh, eps, 1f);
        nv = Math.Clamp(nv, eps, 1f);
        vh = Math.Clamp(vh, eps, 1f);

        float lit = MathF.Min(nl * 6f, 1f);
        float fres = MathF.Pow(1f - vh, 5f) * 0.4f + 0.5f;
        float gtDenom = 2f * nh;
        float gt = MathF.Max(0f, MathF.Min(gtDenom * nv / vh, gtDenom * nl / vh));
        float scol = shadow * fres * ViewerSpecularLut(nh, glossiness) * gt / (nh * nl);
        return lit * scol;
    }

    public readonly record struct Inputs(
        bool HasSpecularTexture,
        Vector3 SpecularTexel,      // RGB of the specular map where it is sampled (white for IMG_WHITE)
        Vector3 SpecularTint,
        bool HasNormalTexture,
        float NormalAlpha,          // alpha of the normal map where it is sampled; 1 for an RGB map
        float SpecularGlossiness,   // SpecExp / 255
        float SpecularEnvironment,  // EnvIntensity / 255
        float LegacyShininess,      // Shiny level as the viewer packs it: 0, .25, .5, .75
        float MetallicFactor,
        float RoughnessFactor,
        Vector3 Albedo);

    public readonly record struct Result(
        float Glossiness, float Roughness, float Metallic, float Specular, float EnvIntensity, Vector3 Albedo);

    /// <summary><c>slng_glossiness_to_roughness</c>: the Blinn-Phong exponent of SL's lookup table
    /// (<c>glossiness^2 * 368</c>) as a GGX alpha, with no second square root (FEAT-RENDER-20).</summary>
    public static float GlossinessToRoughness(float glossiness)
    {
        float n = glossiness * glossiness * 368f;
        return Math.Clamp(MathF.Sqrt(2f / (n + 2f)), 0f, 1f);
    }

    public static Result Predict(in Inputs i)
    {
        float metallic = i.MetallicFactor;
        float roughness = i.RoughnessFactor;
        float specular = 0f;
        float envIntensity = 0f;
        float glossiness = 0f;

        if (i.HasSpecularTexture)
        {
            var spec = i.SpecularTexel * i.SpecularTint;
            glossiness = i.SpecularGlossiness;
            if (i.HasNormalTexture) glossiness *= i.NormalAlpha;

            roughness = GlossinessToRoughness(glossiness);
            specular = glossiness > 0f
                ? Math.Clamp(Vector3.Dot(spec, new Vector3(0.2126f, 0.7152f, 0.0722f)), 0f, 1f)
                : 0f;
            envIntensity = i.SpecularEnvironment;
            metallic = Math.Clamp(i.MetallicFactor + envIntensity * specular, 0f, 1f);
        }
        else if (i.LegacyShininess > 0f)
        {
            specular = 0.5f;
            glossiness = i.LegacyShininess;
            roughness = GlossinessToRoughness(i.LegacyShininess);
            envIntensity = i.LegacyShininess;
        }

        var albedo = i.Albedo;
        envIntensity = Math.Clamp(envIntensity, 0f, 1f);
        if (envIntensity > 0f)
        {
            // The sharp (lod 0) mirror lobe, weighted by how much of a mirror this face is.
            float sharp = Math.Clamp(envIntensity / MirrorFullSharpnessEnvironment, 0f, 1f);
            roughness = roughness + (0f - roughness) * sharp;
            metallic = metallic + (1f - metallic) * envIntensity;
            albedo = albedo + (new Vector3(0.5f) - albedo) * envIntensity;
        }

        return new Result(glossiness, roughness, metallic, specular, envIntensity, albedo);
    }
}
