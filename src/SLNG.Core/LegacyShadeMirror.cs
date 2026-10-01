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
