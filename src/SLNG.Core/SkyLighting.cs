using System.Numerics;

namespace SLNG.Core;

/// <summary>The derived lighting values a sky produces: what the renderer actually feeds to the
/// atmospherics, as opposed to the raw settings it was built from (FEAT-ENV-01).</summary>
/// <param name="SunDiffuse">Sunlight after atmospheric attenuation — the shader's
/// <c>sunlight_color</c> uniform. NOT <see cref="SkySettings.SunlightColor"/>.</param>
/// <param name="SunAmbient">Ambient after the cloud-cover lift — the shader's
/// <c>ambient_color</c> uniform.</param>
/// <param name="MoonDiffuse">Moonlight after attenuation and moon brightness.</param>
/// <param name="MoonAmbient">Scotopic ambient, used while the moon is the light source.</param>
/// <param name="HazeColor">The haze tint the sky dome is drawn with.</param>
/// <param name="TotalAmbient">The unmodified ambient from the settings.</param>
public readonly record struct SkyLighting(
    Vector3 SunDiffuse,
    Vector3 SunAmbient,
    Vector3 MoonDiffuse,
    Vector3 MoonAmbient,
    Vector3 HazeColor,
    Vector3 TotalAmbient)
{
    /// <summary>Turns raw sky settings into the lighting the renderer publishes, as a port of
    /// <c>LLSettingsSky::calculateLightSettings</c> (llsettingssky.cpp:1704).
    ///
    /// This step is not optional and not cosmetic. The atmospherics shader's
    /// <c>sunlight_color</c> and <c>ambient_color</c> uniforms are the OUTPUT of this function,
    /// not the settings of the same name — feeding the raw values straight through produces a
    /// sky that looks plausible and is wrong at every sun angle, because it skips the
    /// <c>exp(-light_atten / |lightnorm.z|)</c> attenuation that is precisely what makes a low sun
    /// go red. The viewer's own source flags the pairing: <c>calcAtmosphericVars</c> and this
    /// function name each other as "similar/shared algorithms".</summary>
    /// <param name="sky">The sky to derive from.</param>
    /// <param name="lightDirectionZ">Vertical component of the direction TOWARD the light, in SL
    /// coordinates where Z is up. The viewer reads <c>lightnorm[2]</c> here; note its own
    /// GLSL uses <c>lightnorm.y</c> because the shader works in eye space, which is the same
    /// quantity in a different frame.</param>
    public static SkyLighting Calculate(SkySettings sky, float lightDirectionZ, float? moonDirectionZ = null)
    {
        Vector3 sunlight = sky.SunlightColor;
        Vector3 ambient = sky.AmbientColor;
        float cloudShadow = sky.CloudShadow;

        // Approximate line integral over max_y (getLightAttenuation, llsettingssky.cpp:1585).
        Vector3 lightAtten =
            (sky.BlueDensity + new Vector3(sky.HazeDensity * 0.25f)) * sky.DensityMultiplier * sky.MaxY;

        // Beer's law over the same distance (getLightTransmittance).
        Vector3 totalDensity = sky.BlueDensity + new Vector3(sky.HazeDensity);
        Vector3 lightTransmittance = Exp(totalDensity * -(sky.DensityMultiplier * sky.MaxY));

        // 1 / |lightnorm.z|, i.e. roughly the cosecant of the sun's elevation: the lower the sun,
        // the longer the path through the atmosphere and the harder the attenuation bites. The
        // viewer guards the reciprocal with 8 * FLT_EPSILON rather than a plain zero check, and
        // that floor is what keeps a sun exactly on the horizon finite instead of black.
        const float limit = float.Epsilon * 8.0f;
        float sunElev = Math.Abs(lightDirectionZ);
        float sunLighty = sunElev >= limit ? 1.0f / sunElev : 1.0f / limit;
        sunLighty = Math.Max(limit, sunLighty);

        sunlight *= Exp(lightAtten * -1.0f * sunLighty);
        sunlight *= lightTransmittance;

        // More cloud cover means more of the sky acts as a diffuser, so ambient goes UP.
        Vector3 tmpAmbient = ambient + (Vector3.One - ambient) * cloudShadow * 0.5f;

        Vector3 sunDiffuse = sunlight;
        Vector3 sunAmbient = tmpAmbient;

        Vector3 hazeInput = sunlight * (1.0f - cloudShadow) + tmpAmbient;
        Vector3 hazeColor = sky.BlueHorizon * sky.BlueDensity * hazeInput
                            + new Vector3(sky.HazeHorizon) * sky.HazeDensity * hazeInput;

        // Moon elevation and attenuation: if moonDirectionZ is provided, use its own elevation
        // rather than the sun's elevation. The sun can be sitting on the horizon (high attenuation)
        // while the moon is high in the sky (low attenuation).
        float actualMoonZ = moonDirectionZ ?? -lightDirectionZ;
        bool moonUp = actualMoonZ > 0f;
        float moonElev = Math.Abs(actualMoonZ);
        float moonLighty = moonElev >= limit ? 1.0f / moonElev : 1.0f / limit;
        moonLighty = Math.Max(limit, moonLighty);

        float moonBrightness = moonUp ? sky.MoonBrightness : 0.001f;
        Vector3 moonlight = sky.SunlightColor;
        moonlight *= Exp(lightAtten * -1.0f * moonLighty);

        Vector3 moonDiffuse = moonlight * lightTransmittance * moonBrightness;
        // The viewer's hardcoded scotopic ambient: (0.66, 0.66, 1.2) * 0.0125.
        Vector3 moonAmbient = new Vector3(0.66f, 0.66f, 1.2f) * 0.0125f;

        return new SkyLighting(sunDiffuse, sunAmbient, moonDiffuse, moonAmbient, hazeColor, ambient);
    }

    /// <summary>Component-wise exponential — the viewer's <c>componentExp</c>. Extracted because
    /// it appears three times above and a per-channel exp is easy to mistake for a scalar one.</summary>
    private static Vector3 Exp(Vector3 v) => new(MathF.Exp(v.X), MathF.Exp(v.Y), MathF.Exp(v.Z));
}
