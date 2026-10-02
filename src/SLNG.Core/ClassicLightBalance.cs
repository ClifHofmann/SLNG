using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// What the reference viewer and SLNG's Godot setup each put on a HORIZONTAL, white (albedo 1)
/// legacy-material surface in full sun and in full shadow, from one sky's derived lighting -- the
/// numbers behind "lit-versus-shadow contrast" and "saturation" when a floor looks pale. For the
/// <c>--diag</c> [LightBalance] line and the tests; nothing renders from it.
/// </summary>
/// <remarks>
/// <b>Viewer</b> (classic mode, which every sky without reflection_probe_ambiance takes;
/// class3/deferred/softenLightF.glsl:159-160, :226-233, :280-281):
/// <c>amblit = pow(tmpAmbient, 0.9) * 0.57</c>, <c>sunlit = SunDiffuse * 1.35</c>, then
/// <c>color = srgb_to_linear(amblit * 0.9 + linear_to_srgb(min(da^1.2, shadow)) * sunlit * 0.7)</c>
/// and finally <c>* 1.1</c>. Note the N.L term is gamma-encoded and the sum is made in sRGB.
///
/// <b>SLNG</b> (EnvironmentDriver.ApplySun / ApplyAmbient): Godot adds in linear light, so the two
/// ENDPOINTS are matched -- the ambient light is the viewer's fully shadowed value, the sun carries
/// the difference to the fully lit one at N.L = 1, and Godot then scales the sun by N.L LINEARLY.
/// The two agree exactly in shadow and at N.L = 1; in between they differ because the viewer's N.L
/// goes through <c>pow(.,1.2)</c> and an sRGB encode. This type is a mirror of that arithmetic, and
/// the tests pin what it says.
/// </remarks>
public static class ClassicLightBalance
{
    private const float AmblitScale = 0.57f;
    private const float AmbientMix = 0.9f;
    private const float SunlitBoost = 1.35f;
    private const float SunlitMix = 0.7f;
    private const float FinalScale = 1.1f;

    public readonly record struct Balance(Vector3 Shadow, Vector3 Lit)
    {
        /// <summary>Lit radiance over shadow radiance, per channel.</summary>
        public Vector3 Ratio => new(
            Shadow.X > 0f ? Lit.X / Shadow.X : 0f,
            Shadow.Y > 0f ? Lit.Y / Shadow.Y : 0f,
            Shadow.Z > 0f ? Lit.Z / Shadow.Z : 0f);
    }

    public readonly record struct Result(Balance Viewer, Balance Godot);

    public static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public static float LinearToSrgb(float c) =>
        c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;

    /// <param name="sunDiffuse"><see cref="SkyLighting.SunDiffuse"/>.</param>
    /// <param name="sunAmbient"><see cref="SkyLighting.SunAmbient"/> (tmpAmbient).</param>
    /// <param name="sunElevationSine">The sun's elevation as sin(angle), i.e. N.L of a horizontal
    /// surface.</param>
    public static Result Compute(Vector3 sunDiffuse, Vector3 sunAmbient, float sunElevationSine)
    {
        float da = Math.Clamp(sunElevationSine, 0f, 1f);
        float viewerSun = LinearToSrgb(MathF.Pow(da, 1.2f));

        var shadow = Vector3.Zero;
        var lit = Vector3.Zero;
        var godotShadow = Vector3.Zero;
        var godotLit = Vector3.Zero;

        for (int c = 0; c < 3; c++)
        {
            float amb = MathF.Pow(MathF.Max(Channel(sunAmbient, c), 0f), 0.9f) * AmblitScale * AmbientMix;
            float sun = Channel(sunDiffuse, c) * SunlitBoost * SunlitMix;

            float vShadow = SrgbToLinear(amb) * FinalScale;
            float vLit = SrgbToLinear(amb + viewerSun * sun) * FinalScale;

            // SLNG: ambient = the shadow endpoint; sun radiance = (lit at N.L=1) - shadow, scaled
            // by N.L in linear light.
            float vLitFull = SrgbToLinear(amb + sun) * FinalScale;
            float sunRadiance = MathF.Max(vLitFull - vShadow, 0f);

            Set(ref shadow, c, vShadow);
            Set(ref lit, c, vLit);
            Set(ref godotShadow, c, vShadow);
            Set(ref godotLit, c, vShadow + sunRadiance * da);
        }

        return new Result(new Balance(shadow, lit), new Balance(godotShadow, godotLit));
    }

    /// <summary>The weight the viewer gives the environment reflection of a legacy glossy face (the
    /// <c>applyGlossEnv</c> term, reflectionProbeF.glsl:893-902), at the view angle where its Fresnel
    /// term sits at its floor: <c>0.5 (fudge) * spec.rgb * fresnel^2 * spec.a * 0.5</c>, with
    /// <c>fresnel = clamp(1 + dot(view, n), 0.3, 1)</c>. The <c>(1 - color)</c> energy-conservation
    /// factor is left out (it only lowers it), so this is an UPPER bound on the viewer's veil.
    /// Multiply by the reflected radiance to get the added light.</summary>
    public static float ViewerGlossEnvWeight(float specularLuminance, float glossiness, float fresnelTerm = 0.3f)
    {
        float f = Math.Clamp(fresnelTerm, 0.3f, 1f);
        return 0.5f * specularLuminance * f * f * glossiness * 0.5f;
    }

    /// <summary>What Godot reflects of the environment at normal incidence for a dielectric given the
    /// shader's <c>SPECULAR</c> output: F0 = 0.08 * SPECULAR, times the reflection probe's
    /// <c>Intensity</c>. Multiply by the reflected radiance to get the added light.</summary>
    public static float GodotSpecularWeight(float specular, float probeIntensity) =>
        0.08f * specular * probeIntensity;

    /// <summary>The sun colour the viewer feeds its legacy highlight with: <c>sunlit_linear</c> after
    /// the classic-mode <c>* 1.35</c> and <c>srgb_to_linear</c> (softenLightF.glsl:152-153, :226-232),
    /// times the final <c>* 1.1</c> (:280-281). The material lab's "viewer sun highlight" uses Godot's
    /// own sun radiance (<c>LIGHT_COLOR / PI</c>) in its place; <see cref="Compute"/>'s Godot sun
    /// radiance (lit at N.L = 1 minus shadow) is within about 0.1% of this for the red channel of the
    /// logged terrace sky, which is what makes that substitution defensible.</summary>
    public static Vector3 ViewerSunlitForSpecular(Vector3 sunDiffuse) => new(
        SrgbToLinear(sunDiffuse.X * SunlitBoost) * FinalScale,
        SrgbToLinear(sunDiffuse.Y * SunlitBoost) * FinalScale,
        SrgbToLinear(sunDiffuse.Z * SunlitBoost) * FinalScale);

    private static float Channel(Vector3 v, int c) => c == 0 ? v.X : c == 1 ? v.Y : v.Z;

    private static void Set(ref Vector3 v, int c, float value)
    {
        if (c == 0) v.X = value;
        else if (c == 1) v.Y = value;
        else v.Z = value;
    }
}
