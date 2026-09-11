using System.Numerics;

namespace SLNG.Core;

/// <summary>One sky state — a single EEP sky frame, or a legacy Windlight sky, or the result of
/// interpolating between two day-cycle keyframes (FEAT-ENV-01).
///
/// Field selection is not arbitrary: these are exactly the inputs of the viewer's surface
/// atmospherics, <c>calcAtmosphericVars</c> in
/// <c>class1/windlight/atmosphericsFuncs.glsl</c>, plus the cloud and celestial-body parameters
/// the sky dome needs. The advanced-atmospherics fields (<c>rayleigh_config</c>,
/// <c>mie_config</c>, <c>absorption_config</c>, <c>planet_radius</c>) are deliberately absent:
/// they reach no shader anywhere in the viewer tree, so carrying them would imply a fidelity we
/// would not be delivering.
///
/// Colours are <see cref="Vector3"/> RGB, not a colour type, because that is what they are on the
/// wire and in the shader — SL's sky colours are unbounded scattering coefficients, several of
/// them well outside 0..1, and squeezing them into a clamped colour type loses data.
///
/// Every default here is the viewer's own, taken from <c>LLSettingsSky::defaults()</c> and the
/// hard fallbacks in <c>LLSettingsSky::loadValuesFromLLSD</c> (llsettingssky.cpp:1224-1230). A
/// sky that omits a key must render like the viewer's, not like zero.</summary>
public record SkySettings
{
    // --- Haze model: the atmospherics inputs -------------------------------------------------
    // Defaults from llsettingssky.cpp:1224-1230. Note these are the fallbacks used when the key
    // is absent from BOTH the "legacy_haze" sub-map and the top level -- see
    // EnvironmentLlsdParser for why that two-level lookup exists.

    /// <summary>Ambient sky colour. Viewer default (0.25, 0.25, 0.25).</summary>
    public Vector3 AmbientColor { get; init; } = new(0.25f, 0.25f, 0.25f);

    /// <summary>Rayleigh-ish scattering density per channel. Viewer default
    /// (0.2447, 0.4487, 0.7599) — note it is far from grey; the blue bias is the sky.</summary>
    public Vector3 BlueDensity { get; init; } = new(0.2447f, 0.4487f, 0.7599f);

    /// <summary>Horizon scattering colour. Viewer default (0.4954, 0.4954, 0.6399).</summary>
    public Vector3 BlueHorizon { get; init; } = new(0.4954f, 0.4954f, 0.6399f);

    /// <summary>Haze density. Viewer default 0.7.</summary>
    public float HazeDensity { get; init; } = 0.7f;

    /// <summary>Haze contribution at the horizon. Viewer default 0.19.</summary>
    public float HazeHorizon { get; init; } = 0.19f;

    /// <summary>Scattering density along the view ray. Viewer default 0.0001 — small, and the
    /// scattering integral is very sensitive to it.</summary>
    public float DensityMultiplier { get; init; } = 0.0001f;

    /// <summary>Scales how far haze reaches. Viewer default 0.8.</summary>
    public float DistanceMultiplier { get; init; } = 0.8f;

    /// <summary>Altitude at which the atmosphere is clamped, in metres. Viewer default 1605.</summary>
    public float MaxY { get; init; } = 1605f;

    /// <summary>Sun-glow shaping: (size, unused, falloff exponent). Viewer default
    /// (5.0, 0.001, -0.4799); the third component is negative on purpose — the shader raises the
    /// glow angle to this power, so it acts as a reciprocal.</summary>
    public Vector3 Glow { get; init; } = new(5.0f, 0.001f, -0.4799f);

    /// <summary>Where the camera sits inside the sky dome, as a fraction of its radius. Viewer
    /// default 0.96.
    ///
    /// Load-bearing for cloud placement, and easy to mistake for a cosmetic tweak.
    /// <c>LLEnvironment::getCamHeight</c> (llenvironment.cpp:1045-1048) returns
    /// <c>dome_offset * dome_radius</c>, and <c>renderDome</c> translates the dome down by exactly
    /// that before drawing. So the camera sits 96% of the way up the inside of the sphere, which
    /// is what makes the dome's 22.5-degree polar cap subtend the WHOLE sky rather than a patch
    /// overhead. The dome radius itself cancels out of the resulting UV, so only this ratio is
    /// needed on the renderer side.</summary>
    public float DomeOffset { get; init; } = 0.96f;

    /// <summary>Scene gamma, range 0…20. Viewer default 1.0.
    ///
    /// Parsed and deliberately NOT applied. In the modern viewer this only reaches
    /// <c>legacyGamma</c> in <c>postDeferredGammaCorrect.glsl</c>, which sits behind an
    /// <c>#ifdef LEGACY_GAMMA</c> — i.e. the pre-SL-7.0 "classic" path. The dedicated
    /// <c>gammaF.glsl</c> that used to own the soft-clip is marked "DEPRECATED … this file is
    /// effectively dead" and every one of its functions now returns its input unchanged.
    /// Applying gamma would therefore be a DIVERGENCE from modern rendering, not parity with it.
    /// Kept in the model because it round-trips the region's document faithfully and because
    /// <c>classic_mode</c> remains an open question for Phase E (see ADR 0003).</summary>
    public float Gamma { get; init; } = 1.0f;

    /// <summary>How much cloud cover dims the sun and lifts the ambient. Viewer default
    /// 0.2699.</summary>
    public float CloudShadow { get; init; } = 0.2699f;

    /// <summary>Raw sunlight colour BEFORE atmospheric attenuation. The shader's
    /// <c>sunlight_color</c> uniform is not this value — see
    /// <see cref="SkyLighting.Calculate"/>.</summary>
    public Vector3 SunlightColor { get; init; } = new(0.7342f, 0.7815f, 0.8999f);

    // --- Celestial bodies ---------------------------------------------------------------------

    /// <summary>Sun orientation. The sun direction is this rotation applied to the reference
    /// direction; SL's own <c>SimulatorViewerTimeMessage</c> also carries a sun direction and the
    /// two are not yet known to agree — see the spec's open questions before trusting this one
    /// over the live message.</summary>
    public Quaternion SunRotation { get; init; } = Quaternion.Identity;

    /// <summary>Moon orientation, same convention as <see cref="SunRotation"/>.</summary>
    public Quaternion MoonRotation { get; init; } = Quaternion.Identity;

    /// <summary>Moon brightness. Viewer default 0.5.</summary>
    public float MoonBrightness { get; init; } = 0.5f;

    /// <summary>Star brightness. Viewer default 250.</summary>
    public float StarBrightness { get; init; } = 250f;

    /// <summary>Sun disc size multiplier. Absent from the viewer's own defaults table, so 1.</summary>
    public float SunScale { get; init; } = 1.0f;

    /// <summary>Moon disc size multiplier. Absent from the viewer's own defaults table, so 1.</summary>
    public float MoonScale { get; init; } = 1.0f;

    // --- Clouds --------------------------------------------------------------------------------

    /// <summary>Cloud tint. Viewer default (0.4099, 0.4099, 0.4099).</summary>
    public Vector3 CloudColor { get; init; } = new(0.4099f, 0.4099f, 0.4099f);

    /// <summary>Cloud layer 1 position/density: XY position, Z density. Viewer default
    /// (1.0, 0.526, 1.0).</summary>
    public Vector3 CloudPosDensity1 { get; init; } = new(1.0f, 0.526f, 1.0f);

    /// <summary>Cloud layer 2 position/density. Viewer default (1.0, 0.526, 1.0).</summary>
    public Vector3 CloudPosDensity2 { get; init; } = new(1.0f, 0.526f, 1.0f);

    /// <summary>Cloud texture scale. Viewer default 0.4199.</summary>
    public float CloudScale { get; init; } = 0.4199f;

    /// <summary>Cloud scroll speed in X/Y. Viewer default (0.2, 0.01).</summary>
    public Vector2 CloudScrollRate { get; init; } = new(0.2f, 0.01f);

    /// <summary>Cloud shape variance. Viewer default 0.</summary>
    public float CloudVariance { get; init; }

    /// <summary>Cloud noise texture asset, or <see cref="Guid.Empty"/> for the viewer default.</summary>
    public Guid CloudTextureId { get; init; }

    /// <summary>Sun texture asset, or <see cref="Guid.Empty"/> for the viewer default.</summary>
    public Guid SunTextureId { get; init; }

    /// <summary>Moon texture asset, or <see cref="Guid.Empty"/> for the viewer default.</summary>
    public Guid MoonTextureId { get; init; }

    // --- FEAT-ENV-03: which lighting/tonemap path this sky takes -----------------------------

    /// <summary>True for a sky that predates PBR/HDR — a legacy Windlight sky, or an EEP sky
    /// converted from one. Mirrors the viewer's <c>mCanAutoAdjust</c>
    /// (<c>llsettingssky.cpp:1174</c>): <c>!settings.has("reflection_probe_ambiance")</c> — the
    /// key's mere PRESENCE, not its value, is what takes a sky off the legacy path
    /// (<c>llsettingsvo.cpp:810</c>, <c>classic_mode = canAutoAdjust() &amp;&amp;
    /// !should_auto_adjust()</c>; every grid this project has measured ships
    /// <c>RenderSkyAutoAdjustLegacy=0</c>, so <c>canAutoAdjust()</c> alone decides it here).
    /// Defaults to <c>true</c> so a document that carries no explicit signal — the viewer
    /// default, a single sky/water document, anything parsed before this field existed — keeps
    /// rendering through the already-calibrated legacy path (<c>FEAT-RENDER-19</c>).</summary>
    public bool IsLegacy { get; init; } = true;

    /// <summary>The viewer's own default sky, used when a region advertises no environment at all
    /// and as the base every parsed sky starts from.</summary>
    public static SkySettings Default { get; } = new();

    /// <summary>Blends two day-cycle keyframes. Scalars and colours interpolate linearly;
    /// orientations SLERP, because a component-wise lerp of two quaternions would drag the sun
    /// through the inside of its arc and change its speed across the sky.
    ///
    /// <see cref="CloudTextureId"/>, <see cref="SunTextureId"/>, and <see cref="MoonTextureId"/>
    /// do not interpolate — an asset id has no midpoint. The nearer keyframe's texture wins.</summary>
    public static SkySettings Lerp(SkySettings a, SkySettings b, float t) => new()
    {
        AmbientColor = Vector3.Lerp(a.AmbientColor, b.AmbientColor, t),
        BlueDensity = Vector3.Lerp(a.BlueDensity, b.BlueDensity, t),
        BlueHorizon = Vector3.Lerp(a.BlueHorizon, b.BlueHorizon, t),
        HazeDensity = float.Lerp(a.HazeDensity, b.HazeDensity, t),
        HazeHorizon = float.Lerp(a.HazeHorizon, b.HazeHorizon, t),
        DensityMultiplier = float.Lerp(a.DensityMultiplier, b.DensityMultiplier, t),
        DistanceMultiplier = float.Lerp(a.DistanceMultiplier, b.DistanceMultiplier, t),
        MaxY = float.Lerp(a.MaxY, b.MaxY, t),
        Glow = Vector3.Lerp(a.Glow, b.Glow, t),
        DomeOffset = float.Lerp(a.DomeOffset, b.DomeOffset, t),
        Gamma = float.Lerp(a.Gamma, b.Gamma, t),
        CloudShadow = float.Lerp(a.CloudShadow, b.CloudShadow, t),
        SunlightColor = Vector3.Lerp(a.SunlightColor, b.SunlightColor, t),

        SunRotation = Quaternion.Slerp(a.SunRotation, b.SunRotation, t),
        MoonRotation = Quaternion.Slerp(a.MoonRotation, b.MoonRotation, t),
        MoonBrightness = float.Lerp(a.MoonBrightness, b.MoonBrightness, t),
        StarBrightness = float.Lerp(a.StarBrightness, b.StarBrightness, t),
        SunScale = float.Lerp(a.SunScale, b.SunScale, t),
        MoonScale = float.Lerp(a.MoonScale, b.MoonScale, t),
        SunTextureId = t < 0.5f ? a.SunTextureId : b.SunTextureId,
        MoonTextureId = t < 0.5f ? a.MoonTextureId : b.MoonTextureId,
        // Not a continuous quantity -- like the texture ids above, the nearer keyframe wins. In
        // practice both keyframes of one day cycle come from the same document schema, so this
        // is never actually a choice between two different answers.
        IsLegacy = t < 0.5f ? a.IsLegacy : b.IsLegacy,

        CloudColor = Vector3.Lerp(a.CloudColor, b.CloudColor, t),
        CloudPosDensity1 = Vector3.Lerp(a.CloudPosDensity1, b.CloudPosDensity1, t),
        CloudPosDensity2 = Vector3.Lerp(a.CloudPosDensity2, b.CloudPosDensity2, t),
        CloudScale = float.Lerp(a.CloudScale, b.CloudScale, t),
        CloudScrollRate = Vector2.Lerp(a.CloudScrollRate, b.CloudScrollRate, t),
        CloudVariance = float.Lerp(a.CloudVariance, b.CloudVariance, t),
        CloudTextureId = t < 0.5f ? a.CloudTextureId : b.CloudTextureId,
    };
}
