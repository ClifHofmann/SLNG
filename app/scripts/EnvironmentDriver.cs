using System;
using Godot;
using SLNG.Core;

namespace SLNG.App;

/// <summary>Evaluates the region's day cycle each frame and drives the Godot scene from it
/// (FEAT-ENV-01 Phase D): sun colour/energy, ambient light, the procedural sky dome, classic fog,
/// and the water plane's colour.
///
/// This is the first PHASE of Windlight/EEP that changes anything on screen, and it deliberately
/// does so without touching a single shader — every renderer stays exactly as it was before this
/// feature. The per-fragment atmospherics that make haze respond correctly on every surface (not
/// just the sky dome and Godot's own fog) is Phase E, blocked on `FEAT-RENDER-01` putting avatars,
/// terrain and water on the shader family first.
///
/// Because Phase E is not here yet, the sky-dome and fog mappings below are DELIBERATE
/// approximations, not a port of the viewer's atmospherics — there is nowhere to port
/// `calcAtmosphericVars` INTO until the shader seam is real. What they have to get right is the
/// one thing a screenshot can immediately judge: that the scene visibly changes with the time of
/// day. Pixel-parity with Firestorm is Phase E's job.</summary>
public sealed class EnvironmentDriver
{
    private DayCycle _cycle = DayCycle.Default;
    private EnvironmentSource _source = EnvironmentSource.Default;

    /// <summary>Replaces the active day cycle. Called once per region, from
    /// <c>GridSession.RegionEnvironmentReceived</c> (already marshalled onto the main thread by
    /// the caller).</summary>
    public void SetCycle(DayCycle cycle, EnvironmentSource source)
    {
        _cycle = cycle;
        _source = source;
    }

    /// <summary>Applies the current cycle, evaluated at <paramref name="utcNow"/>, to the scene.
    /// Safe to call every frame — it is four colour/scalar writes and no allocation beyond what
    /// <see cref="DayCycle.EvaluateSky"/> already does.</summary>
    /// <param name="sunDirectionSl">The region's actual sun direction in SL coordinates (Z up),
    /// FROM the region TOWARD the sun — <c>GridSession.SunDirection</c>. This is the SAME
    /// authority <c>Boot.UpdateSunFromRegion</c> already aims the light with; see the open
    /// question in the spec about EEP's own <c>sun_rotation</c> disagreeing with it, which is why
    /// this driver does not use the sky settings' rotation for direction, only for colour.</param>
    public void Update(
        WorldEnvironment? worldEnvironment,
        DirectionalLight3D? sun,
        ShaderMaterial? waterMaterial,
        System.Numerics.Vector3 sunDirectionSl,
        DateTimeOffset utcNow)
    {
        if (worldEnvironment?.Environment is not { } env) return;

        // No SimulatorViewerTimeMessage yet: treat the sun as overhead rather than feeding a zero
        // vector into SkyLighting, which would read as the sun sitting exactly on the horizon and
        // wash the whole scene in sunset tones before the real direction ever arrives.
        float lightDirectionZ = sunDirectionSl.LengthSquared() > 0.0001f
            ? System.Numerics.Vector3.Normalize(sunDirectionSl).Z
            : 1.0f;

        var sky = _cycle.EvaluateSky(utcNow);
        var water = _cycle.EvaluateWater(utcNow);
        var lighting = SkyLighting.Calculate(sky, lightDirectionZ);

        ApplySun(sun, lighting);
        ApplyAmbient(env, lighting);
        ApplySkyDome(env, sky, lighting, lightDirectionZ);
        ApplyFog(env, sky, lighting);
        ApplyWater(waterMaterial, water);
    }

    /// <summary>Splits an SL colour into a normalized Godot <see cref="Color"/> and a scalar
    /// energy multiplier — SL's sky colours are unbounded scattering coefficients, frequently
    /// above 1 on one channel, and Godot's light/ambient energy sliders exist for exactly this
    /// split rather than clamping the colour and losing the magnitude.</summary>
    private static void Split(System.Numerics.Vector3 v, out Color color, out float energy)
    {
        float max = MathF.Max(v.X, MathF.Max(v.Y, v.Z));
        if (max <= 0.0001f)
        {
            color = Colors.Black;
            energy = 0f;
            return;
        }
        color = new Color(v.X / max, v.Y / max, v.Z / max);
        energy = max;
    }

    private static Color ToColor(System.Numerics.Vector3 v)
        => new(Mathf.Clamp(v.X, 0f, 1f), Mathf.Clamp(v.Y, 0f, 1f), Mathf.Clamp(v.Z, 0f, 1f));

    private void ApplySun(DirectionalLight3D? sun, SkyLighting lighting)
    {
        if (sun == null) return;

        Split(lighting.SunDiffuse, out var color, out var energy);
        sun.LightColor = color;
        // Godot's default DirectionalLight3D energy is 1.0 for a clear midday sun; SL's derived
        // sunlight magnitude lands in roughly the same range, so no extra scale is applied here.
        // Clamped rather than left open-ended so a sky with an extreme setting cannot blow the
        // exposure out past what the tonemapper (ACES, set in Boot.SetupEnvironment) can recover.
        sun.LightEnergy = Mathf.Clamp(energy, 0f, 3f);
    }

    private void ApplyAmbient(Godot.Environment env, SkyLighting lighting)
    {
        // Switching AmbientLightSource away from Sky is required, not cosmetic: Godot ignores
        // AmbientLightColor entirely while the source is Sky (it derives ambient from the sky
        // radiance instead), so writing the colour below would silently do nothing otherwise.
        env.AmbientLightSource = Godot.Environment.AmbientSource.Color;

        Split(lighting.SunAmbient, out var color, out var energy);
        env.AmbientLightColor = color;
        env.AmbientLightEnergy = Mathf.Clamp(energy, 0f, 2f);
    }

    /// <summary>Tints the procedural sky dome from the region's haze settings.
    ///
    /// This is an APPROXIMATION, not the viewer's sky rendering: SL computes the dome by
    /// integrating `calcAtmosphericVars` per pixel across the view direction
    /// (`skyF.glsl`/`cloudsF.glsl`), which needs the atmospherics seam Phase E fills. Godot's
    /// `ProceduralSkyMaterial` instead takes four flat colours (zenith/horizon, sky/ground) plus
    /// a curve, so this maps the region's blue/haze horizon colours onto those and scales
    /// brightness with the sun's own derived energy — enough to visibly shift with time of day,
    /// not enough to match Firestorm pixel for pixel.</summary>
    private void ApplySkyDome(Godot.Environment env, SkySettings sky, SkyLighting lighting, float lightDirectionZ)
    {
        if (env.Sky?.SkyMaterial is not ProceduralSkyMaterial mat) return;

        // Zenith reads darker/more saturated than the horizon in a real sky; BlueDensity is SL's
        // own zenith-leaning scattering term, so it stands in here rather than reusing
        // BlueHorizon twice.
        mat.SkyTopColor = ToColor(sky.BlueDensity * 0.6f);
        mat.SkyHorizonColor = ToColor(lighting.HazeColor);
        mat.GroundHorizonColor = ToColor(lighting.HazeColor * 0.85f);
        mat.GroundBottomColor = ToColor(sky.BlueDensity * 0.25f);

        // Brighten toward noon, dim toward the horizon and past it — driven by the SAME
        // lightDirectionZ the lighting above uses, so the dome and the lit scene never disagree
        // about which way is "day" even though this mapping is otherwise independent of it.
        float dayFactor = Mathf.Clamp(lightDirectionZ * 0.5f + 0.5f, 0.15f, 1f);
        mat.SkyEnergyMultiplier = dayFactor;
        mat.GroundEnergyMultiplier = dayFactor;
    }

    /// <summary>Drives Godot's classic depth fog from the region's haze and switches OFF the
    /// flat-density volumetric fog `Boot.SetupEnvironment` starts with.
    ///
    /// The switch is the "reconciled, not left double-applying" requirement from the spec: before
    /// this driver ran, `VolumetricFogEnabled` was already true at a fixed density with nothing
    /// region-aware about it. Leaving both fog models active would mean the visible haze is the
    /// SUM of a region-driven fog and a hardcoded one, which cannot be tuned to look right at any
    /// single region because the hardcoded half never moves. Classic fog is the one Godot fog
    /// model with a colour input, which is the property this data actually has to offer before
    /// Phase E; the volumetric model's value here was never about `DistanceMultiplier`
    /// specifically, so it is retired rather than mapped.</summary>
    private void ApplyFog(Godot.Environment env, SkySettings sky, SkyLighting lighting)
    {
        env.VolumetricFogEnabled = false;

        env.FogEnabled = true;
        env.FogLightColor = ToColor(lighting.HazeColor);
        env.FogLightEnergy = 1.0f;
        env.FogSunScatter = 0.2f;

        // SL's density_multiplier lives around 1e-4 (viewer default 0.0001) and distance_multiplier
        // around 1 (viewer default 0.8) -- values tuned for a per-fragment scattering integral over
        // metres, not for Godot's depth-fog density term, which has no equivalent physical
        // grounding to convert through. The scale below is chosen empirically to land in Godot's
        // usual 0.001-0.02 fog-density range across the viewer's own default sky, not derived from
        // the viewer's formula -- there is no such derivation before Phase E's real per-fragment
        // port.
        float density = sky.DensityMultiplier * sky.DistanceMultiplier * 60f;
        env.FogDensity = Mathf.Clamp(density, 0.0005f, 0.02f);
    }

    private void ApplyWater(ShaderMaterial? waterMaterial, WaterSettings water)
    {
        if (waterMaterial == null) return;

        // water.gdshader's `albedo` uniform is a vec4 (RGB + alpha); alpha is not part of
        // WaterSettings (SL doesn't expose one either -- FogDensity governs underwater opacity,
        // not surface transparency), so the shader's own default alpha is kept rather than guessed.
        // GetShaderParameter falls back to the shader's declared default (0.8) until the first
        // call here overrides it, and stays a Color across calls after that; the literal fallback
        // only guards a material whose shader failed to load.
        var currentVariant = waterMaterial.GetShaderParameter("albedo");
        float currentAlpha = currentVariant.VariantType == Variant.Type.Color
            ? currentVariant.AsColor().A
            : 0.8f;
        var color = ToColor(water.FogColor);
        waterMaterial.SetShaderParameter("albedo", new Color(color.R, color.G, color.B, currentAlpha));
    }
}
