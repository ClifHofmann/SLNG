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

    public EnvironmentDriver()
    {
    }

    /// <summary>Replaces the active day cycle. Called once per region, from
    /// <c>GridSession.RegionEnvironmentReceived</c> (already marshalled onto the main thread by
    /// the caller).</summary>
    public void SetCycle(DayCycle cycle, EnvironmentSource source)
    {
        _cycle = cycle;
        _source = source;
    }

    // --- FEAT-ENV-02: local Windlight preset override ------------------------------------------
    // A preset the USER picked wins over what the region sent, for as long as it is set. Sky and
    // water are held separately on purpose: picking a sky preset must not silently throw away the
    // region's water (and vice versa), which is exactly what overriding a whole DayCycle would do.
    // Nothing else in the driver changes -- the override is substituted at the one point where the
    // cycle would otherwise have been evaluated.

    private SkySettings? _skyOverride;
    private WaterSettings? _waterOverride;

    /// <summary>Name of the active sky preset, or null when the region's own sky is in use.</summary>
    public string? SkyPresetName { get; private set; }

    /// <summary>Name of the active water preset, or null when the region's own water is in use.</summary>
    public string? WaterPresetName { get; private set; }
    
    /// <summary>True while any user preset is overriding the region.</summary>
    public bool HasPresetOverride => _skyOverride != null || _waterOverride != null;

    /// <summary>Which capability the region's own environment came from. Shown in the picker so
    /// "back to region" says what the user is going back TO.</summary>
    public EnvironmentSource RegionSource => _source;

    public void SetSkyPreset(SkySettings sky, string name)
    {
        _skyOverride = sky;
        SkyPresetName = name;
    }

    public void SetWaterPreset(WaterSettings water, string name)
    {
        _waterOverride = water;
        WaterPresetName = name;
    }

    /// <summary>Drops every preset and hands the scene back to the region's own environment. The
    /// region cycle was never discarded, so this takes effect on the next frame with no refetch.</summary>
    public void ClearPresets()
    {
        _skyOverride = null;
        _waterOverride = null;
        SkyPresetName = null;
        WaterPresetName = null;
    }

    /// <summary>Applies the current cycle, evaluated at <paramref name="utcNow"/>, to the scene.
    /// Safe to call every frame — it is four colour/scalar writes and no allocation beyond what
    /// <see cref="DayCycle.EvaluateSky"/> already does.</summary>
    public System.Numerics.Vector3 CalculatedSunDirection { get; private set; }

    /// <summary>Direction toward whichever celestial body is lighting the scene right now — the
    /// sun while it is above the horizon, the moon otherwise. This is what the
    /// <see cref="DirectionalLight3D"/> must be aimed with; <see cref="CalculatedSunDirection"/>
    /// stays the SUN for the sky dome's own sun disc, which has to keep tracking the sun even
    /// after it sets.</summary>
    public System.Numerics.Vector3 CalculatedLightDirection { get; private set; }

    /// <param name="sunDirectionSl">The region's actual sun direction in SL coordinates (Z up),
    /// FROM the region TOWARD the sun — <c>GridSession.SunDirection</c>. This is the SAME
    /// authority <c>Boot.UpdateSunFromRegion</c> already aims the light with; see the open
    /// question in the spec about EEP's own <c>sun_rotation</c> disagreeing with it, which is why
    /// this driver does not use the sky settings' rotation for direction, only for colour.</param>
    /// <summary>Per-texture fetch state. A class rather than four <c>ref Guid</c> fields because a
    /// FAILED fetch has to be able to release the latch it took, and that release happens inside
    /// an async continuation — a `ref` parameter cannot be captured by a lambda, an object can.
    /// Getting this wrong is what made the first attempt at this fix fail: see
    /// <see cref="FetchTextureOnce"/>.</summary>
    private sealed class TextureSlot
    {
        /// <summary>Set once the texture has actually been applied / skipped, so [EnvTex] reports
        /// each slot's outcome exactly once instead of on every retry tick.</summary>
        public bool AppliedLogged;
        public bool SkipLogged;

        public Guid Current;
        public ulong RetryAfterMs;
        public bool FailureLogged;
    }

    private readonly TextureSlot _cloudSlot = new();
    private readonly TextureSlot _sunSlot = new();
    private readonly TextureSlot _moonSlot = new();
    private readonly TextureSlot _waterNormalSlot = new();

    /// <summary>Backoff between attempts for one id. Comfortably longer than nothing but shorter
    /// than <c>AssetService</c>'s own 45s negative-failure cache, so the retry that finally lands
    /// does so promptly once that cache lets a real fetch through again.</summary>
    private const ulong TextureRetryDelayMs = 5000;

    public void Update(
        WorldEnvironment? worldEnvironment,
        DirectionalLight3D? sun,
        ShaderMaterial? waterMaterial,
        System.Numerics.Vector3 sunDirectionSl,
        DateTimeOffset utcNow,
        SLNG.Assets.AssetService? assetService,
        GpuCache? gpuCache,
        bool assetsReady)
    {
        if (worldEnvironment?.Environment is not { } env) return;

        // A user preset (FEAT-ENV-02) replaces the evaluated frame; it is a single fixed sky, so
        // there is nothing to evaluate against the clock.
        var sky = _skyOverride ?? _cycle.EvaluateSky(utcNow);
        var water = _waterOverride ?? _cycle.EvaluateWater(utcNow);

        UpdateCloudScroll(sky);

        // The viewer's own fallbacks for when the region leaves the id unset. Both are tagged
        // "// VIEWER" in indra_constants.cpp -- ids the client is expected to know, as opposed to
        // the "// On dataserver" ones authored per region.
        var defaultCloudTextureId = new Guid("1dc1368f-e8fe-f02d-a08d-9d9f11c1af6b");
        var defaultWaterNormalId = new Guid("822ded49-9a6c-f61c-cb89-6df54f42cdf4");

        FetchTextureOnce(
            _cloudSlot, sky.CloudTextureId != Guid.Empty ? sky.CloudTextureId : defaultCloudTextureId,
            assetService, gpuCache, assetsReady, "cloud",
            tex => { if (worldEnvironment.Environment.Sky?.SkyMaterial is ShaderMaterial m) m.SetShaderParameter("slng_cloud_texture", tex); });

        FetchTextureOnce(
            _sunSlot, sky.SunTextureId, assetService, gpuCache, assetsReady, "sun",
            tex => { if (worldEnvironment.Environment.Sky?.SkyMaterial is ShaderMaterial m) m.SetShaderParameter("slng_sun_texture", tex); });

        FetchTextureOnce(
            _moonSlot, sky.MoonTextureId, assetService, gpuCache, assetsReady, "moon",
            tex => { if (worldEnvironment.Environment.Sky?.SkyMaterial is ShaderMaterial m) m.SetShaderParameter("slng_moon_texture", tex); });

        if (waterMaterial != null)
        {
            FetchTextureOnce(
                _waterNormalSlot, water.NormalMapId != Guid.Empty ? water.NormalMapId : defaultWaterNormalId,
                assetService, gpuCache, assetsReady, "water normal",
                tex => waterMaterial.SetShaderParameter("slng_water_normal_map", tex));
        }

        // A preset carries its own sun position (converted from the legacy sun_angle/east_angle),
        // and picking "Midnight" has to move the sun, not just recolour the sky.
        if (_skyOverride != null ||
            _source == EnvironmentSource.ExtendedEnvironment || _source == EnvironmentSource.LegacyWindlight)
        {
            // If EEP or Legacy Windlight is active, the EEP explicitly overrides the sun position.
            // SunRotation rotates the X-axis (1, 0, 0) into the final SL sun direction.
            // SL uses LLVector3::x_axis * sunq;
            CalculatedSunDirection = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, sky.SunRotation);
            
            if (float.IsNaN(CalculatedSunDirection.X))
            {
                CalculatedSunDirection = System.Numerics.Vector3.UnitZ;
            }
        }
        else
        {
            // Fall back to the simulator time-based sun direction.
            if (float.IsNaN(sunDirectionSl.X) || sunDirectionSl.LengthSquared() <= 0.0001f)
            {
                sunDirectionSl = System.Numerics.Vector3.UnitZ; // fallback to noon if exactly 0 or NaN
            }
            CalculatedSunDirection = System.Numerics.Vector3.Normalize(sunDirectionSl);
        }

        // Which body actually lights the scene. Below the horizon it is the MOON, and it is not
        // simply the anti-sun -- EEP ships its own moon_rotation (all 21 keyframes of the region
        // this was found on carry one) and SkySettings has parsed it all along without anything
        // reading it.
        //
        // Aiming the scene light along the SUN at night pointed it up through the ground: terrain
        // normals face up, dot(N, L) went negative, so terrain rendered unlit while upright
        // objects still caught light from below. That is the "brightly lit tree over black
        // ground" look -- the light had the moon's COLOUR (ApplySun switches that) but the sun's
        // DIRECTION. sky.gdshader has always done this correctly for the sky dome; only the
        // DirectionalLight3D was left aimed at the wrong body.
        //
        var moonDirSl = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, sky.MoonRotation);
        bool moonUsable = !float.IsNaN(moonDirSl.X) && moonDirSl.LengthSquared() > 0.0001f;
        float moonZ = moonUsable ? moonDirSl.Z : -CalculatedSunDirection.Z;

        if (CalculatedSunDirection.Z > 0f)
        {
            CalculatedLightDirection = CalculatedSunDirection;
        }
        else
        {
            CalculatedLightDirection = System.Numerics.Vector3.Normalize(
                moonUsable && moonDirSl.Z > 0.01f ? moonDirSl : -CalculatedSunDirection);
        }

        float lightDirectionZ = CalculatedSunDirection.Z;
        var lighting = SkyLighting.Calculate(sky, lightDirectionZ, moonZ);

        LogDiagnostics(sky, lighting, lightDirectionZ);

        // FEAT-ENV-03: Boot.SetupEnvironment pins Linear only as the one-time startup default
        // (no sky has been evaluated yet); from here on the active sky decides every frame. A
        // legacy sky's own getTonemapMix() returns exactly 0 -- "legacy settings do not support
        // tonemaping" (llsettingssky.cpp:2062) -- so Linear (Godot's identity mapper) is the
        // real target, not an approximation of one. A sky carrying reflection_probe_ambiance
        // instead ships RenderTonemapMix 0.7 in Firestorm's own settings.xml; Godot has no
        // continuous mix between tonemap curves, so Aces (its closest HDR curve) stands in for
        // that partial blend until a custom post-pass can reproduce the mix itself.
        env.TonemapMode = sky.IsLegacy
            ? Godot.Environment.ToneMapper.Linear
            : Godot.Environment.ToneMapper.Aces;

        ApplySun(sun, sky, lighting, lightDirectionZ);
        ApplyAmbient(env, sky, lighting);
        ApplySkyDome(env, sky, lighting, lightDirectionZ);
        ApplyFog(env, sky, lighting);
        ApplyWater(waterMaterial, water);
        
        if (worldEnvironment.Environment.Sky?.SkyMaterial is ShaderMaterial skyMat2)
        {
            UpdateGlobalShaderParameters(sky, lighting, CalculatedSunDirection);
        }
    }

    /// <summary>Fetches one environment texture and hands it to <paramref name="apply"/> on the
    /// main thread, retrying on failure rather than giving up for the session.
    ///
    /// Two separate traps live here, and the first fix for this only cleared one of them.
    ///
    /// <para>1. Latch AFTER the guard, never before. <c>Boot._Process</c> calls this driver from
    /// the very first frame of the LOGIN SCREEN, long before <c>AssetService</c>/<c>GpuCache</c>
    /// exist, and the default day cycle already resolves to the viewer's default cloud and
    /// water-normal ids. Recording the id and then discovering there is no asset service means
    /// that when the region's real environment later names the SAME id, the change check says
    /// "unchanged" and the texture is never fetched at all.</para>
    ///
    /// <para>2. A latch must not survive a FAILED fetch. Guarding on "the services exist" is not
    /// the same as "a fetch can succeed": <c>AssetService</c> abandons a fetch immediately while
    /// the session is disconnected, and then remembers the id in a 45-second negative-failure
    /// cache. So during login the fetch still failed, still latched, and still never retried —
    /// the same dead end one step further along. Hence <paramref name="assetsReady"/> (don't try
    /// before the session can serve assets) and the backoff release below (a transient failure
    /// gets another go).</para>
    ///
    /// Water with no normal map is a flat mirror, which is how both rounds of this surfaced: "the
    /// waves are missing".</summary>
    private static void FetchTextureOnce(
        TextureSlot slot,
        Guid wantedId,
        SLNG.Assets.AssetService? assetService,
        GpuCache? gpuCache,
        bool assetsReady,
        string what,
        Action<Texture2D> apply)
    {
        // Report the early-outs, once per reason per slot. An environment texture that never
        // arrives has no failure mode of its own -- the water simply renders without its wave
        // normals and looks like a mirror, which is indistinguishable from "the shader is wrong".
        // This project has already lost a round to exactly that: the driver latched texture ids at
        // the login screen before AssetService existed, so the water normal and cloud textures
        // never loaded and nothing said so.
        if (wantedId == Guid.Empty)
        {
            if (!slot.SkipLogged) { slot.SkipLogged = true; Console.Error.WriteLine($"[EnvTex] {what}: no texture id in the environment"); }
            return;
        }
        if (wantedId == slot.Current) return;
        if (assetService == null || gpuCache == null || !assetsReady)
        {
            if (!slot.SkipLogged) { slot.SkipLogged = true; Console.Error.WriteLine($"[EnvTex] {what}: assets not ready yet ({wantedId}) -- will retry"); }
            return;
        }

        ulong now = Time.GetTicksMsec();
        if (now < slot.RetryAfterMs) return;
        slot.RetryAfterMs = now + TextureRetryDelayMs;

        slot.Current = wantedId;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var tex = await gpuCache.GetOrUploadTextureAsync(wantedId, assetService, generateMipmaps: true).ConfigureAwait(false);
            MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Refine, () =>
            {
                if (tex != null && !slot.AppliedLogged)
                {
                    slot.AppliedLogged = true;
                    Console.Error.WriteLine($"[EnvTex] {what}: applied {wantedId} ({tex.GetWidth()}x{tex.GetHeight()})");
                }
                if (tex == null)
                {
                    // Release the latch so the backoff above schedules another attempt instead of
                    // this id being written off for the session. Logged once per id: an environment
                    // texture that never arrives has no visible failure mode of its own (the sky or
                    // water just quietly renders without it), so it is worth saying under the
                    // errors-only console policy -- but only once, not every retry.
                    slot.Current = Guid.Empty;
                    if (!slot.FailureLogged)
                    {
                        slot.FailureLogged = true;
                        GD.PrintErr($"[ENV] {what} texture {wantedId} could not be fetched — retrying");
                    }
                    return;
                }
                apply(tex);
            });
        });
    }

    /// <summary>Set true to print one <c>[ENVDBG]</c> line whenever the derived lighting moves
    /// materially. Off by default and gated behind a constant on purpose: the console is kept to
    /// real errors only.
    ///
    /// This exists because tuning this driver against screenshots demonstrably does not work --
    /// three consecutive attempts at the night lighting each moved the symptom instead of fixing
    /// it. The numbers below are the ones that have to be compared against the real viewer's:
    /// what the light's energy actually ends up as, and which of the two bodies produced it.</summary>
    private const bool DiagnosticsEnabled = false;

    /// <summary>How much of the derived moon diffuse actually drives the scene's directional light.
    /// OUR calibration, explicitly not a ported value — recorded here with the evidence because it
    /// is the one number in this file that no viewer source line backs up.
    ///
    /// Measured on The Dangazi Forest at cycle position 0.052: the active keyframe carries
    /// <c>sunlight_color</c> 2.365 with the sun at −88.2° and the moon at +73.0°. Since the moon is
    /// up, the viewer's own <c>getLightDirection()</c> picks it, and <c>calculateLightSettings</c>
    /// then attenuates by only 1/0.956 through an optical depth of 0.101 — so SL's own formula also
    /// lands near 2.2, i.e. twice a Godot daylight sun. Firestorm nonetheless renders that moment
    /// with dark, evenly lit ground and no directional shaping.
    ///
    /// The resolution is that <c>calculateLightSettings</c> is DEAD CODE in the modern viewer:
    /// <c>getSunDiffuse</c>, <c>getMoonDiffuse</c> and <c>getLightDiffuse</c> have no consumer
    /// anywhere in <c>indra/newview</c>. Scene lighting comes from the deferred shader path
    /// instead, where night is effectively ambient-dominant — which is exactly what Firestorm's
    /// night looks like. Godot gives us one directional light and one global ambient, so that
    /// path cannot be reproduced 1:1 until the Phase E shader seam exists; until then this factor
    /// keeps a little directional shape at night without lighting midnight like noon.</summary>
    private const float MoonLightScale = 1.0f;

    private float _lastLoggedSunEnergy = float.NaN;
    private float _lastLoggedAmbEnergy = float.NaN;

#pragma warning disable CS0162 // unreachable while DiagnosticsEnabled is the compile-time false
    private void LogDiagnostics(SkySettings sky, SkyLighting lighting, float lightDirectionZ)
    {
        if (!DiagnosticsEnabled) return;

        bool moonUp = lightDirectionZ < 0f;
        Split(moonUp ? lighting.MoonDiffuse : lighting.SunDiffuse, out _, out float sunEnergy);

        var amb = ClassicShadowRadiance(lighting);
        float ambEnergy = amb.X * 0.2126f + amb.Y * 0.7152f + amb.Z * 0.0722f;

        // Only on a material move, so a static sky logs once rather than 60x a second.
        if (MathF.Abs(sunEnergy - _lastLoggedSunEnergy) < 0.02f &&
            MathF.Abs(ambEnergy - _lastLoggedAmbEnergy) < 0.02f) return;
        _lastLoggedSunEnergy = sunEnergy;
        _lastLoggedAmbEnergy = ambEnergy;

        var lit = moonUp ? lighting.MoonDiffuse : lighting.SunDiffuse;

        GD.Print($"[ENVDBG] body={(moonUp ? "moon" : "sun")} sunZ={lightDirectionZ:F3} " +
                 $"lightZ={CalculatedLightDirection.Z:F3} " +
                 $"=> lightEnergy={sunEnergy:F3} ambEnergy={ambEnergy:F3} " +
                 $"ambRGB=({amb.X:F3},{amb.Y:F3},{amb.Z:F3})");
        GD.Print($"[ENVDBG]   derived diffuse=({lit.X:F3},{lit.Y:F3},{lit.Z:F3}) " +
                 $"rawSunCol=({sky.SunlightColor.X:F2},{sky.SunlightColor.Y:F2},{sky.SunlightColor.Z:F2}) " +
                 $"rawAmbient=({sky.AmbientColor.X:F2},{sky.AmbientColor.Y:F2},{sky.AmbientColor.Z:F2})");
        GD.Print($"[ENVDBG]   atten inputs: blueDensity=({sky.BlueDensity.X:F3},{sky.BlueDensity.Y:F3},{sky.BlueDensity.Z:F3}) " +
                 $"hazeDensity={sky.HazeDensity:F3} densityMult={sky.DensityMultiplier:F5} maxY={sky.MaxY:F0} " +
                 $"moonBright={sky.MoonBrightness:F2} cloudShadow={sky.CloudShadow:F2}");
    }
#pragma warning restore CS0162

    private ulong _lastCloudScrollMs;
    private System.Numerics.Vector2 _cloudScrollDelta;

    /// <summary>Integrates the cloud drift over real time. Port of
    /// <c>LLEnvironment::update</c> (llenvironment.cpp:1671-1683):
    /// <code>delta += delta_t * cloud_scroll_rate / 100</code>
    /// Wall-clock elapsed, not the sim's day-cycle time, because that is what the viewer uses --
    /// and sim time can jump when the server re-syncs, which would teleport the clouds.
    ///
    /// A rate of exactly zero RESETS the accumulator rather than holding the clouds at their
    /// current offset; that is the viewer's own behaviour (<c>mCloudScrollDelta.setZero()</c>),
    /// not an approximation. Unbounded growth is fine: the shader reduces the value with
    /// <c>mod(..., 10000.0)</c>, exactly as the viewer's own cloud shaders do.</summary>
    private void UpdateCloudScroll(SkySettings sky)
    {
        ulong now = Time.GetTicksMsec();
        ulong last = _lastCloudScrollMs;
        _lastCloudScrollMs = now;
        if (last == 0 || now <= last) return; // first frame -- no elapsed span to integrate yet

        var rate = sky.CloudScrollRate;
        if (rate == System.Numerics.Vector2.Zero)
        {
            _cloudScrollDelta = System.Numerics.Vector2.Zero;
            return;
        }

        float deltaSeconds = (now - last) / 1000f;
        _cloudScrollDelta += deltaSeconds * rate / 100f;
    }

    private static Color ToColorFast(System.Numerics.Vector3 v)
    {
        float x = float.IsNaN(v.X) ? 0f : v.X;
        float y = float.IsNaN(v.Y) ? 0f : v.Y;
        float z = float.IsNaN(v.Z) ? 0f : v.Z;
        return new Color(x, y, z);
    }

    private static float SafeFloat(float v, float fallback = 0f) => float.IsNaN(v) || float.IsInfinity(v) ? fallback : v;

    /// <summary>The standard sRGB transfer function, matching the viewer's own
    /// <c>srgb_to_linear</c> (srgbF.glsl). Applied above 1.0 as well — the viewer's GLSL does the
    /// same, and that upper branch is exactly what keeps a bright sky bright while pulling a dim
    /// one down.</summary>
    private static float SrgbToLinear(float c)
    {
        if (c <= 0f) return 0f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>Inverse of <see cref="SrgbToLinear"/>. Needed because Godot treats
    /// <c>Light3D.LightColor</c> and <c>Environment.AmbientLightColor</c> as sRGB and runs
    /// <c>srgb_to_linear</c> on them before shading — measured, not assumed: a 0.5 grey light on a
    /// white albedo at NdotL 1 renders 0.498, which is <c>linear_to_srgb(srgb_to_linear(0.5))</c>,
    /// where an unconverted colour would render 0.735. Feeding a value through this first makes
    /// Godot's conversion cancel, so the shader receives the linear radiance we actually computed.</summary>
    private static float LinearToSrgb(float c)
    {
        if (c <= 0f) return 0f;
        return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
    }

    /// <summary>Like <see cref="Split"/>, but pre-compensates for the sRGB→linear conversion Godot
    /// applies to light colours. <c>v</c> is the LINEAR radiance the shader must end up with;
    /// energy carries the magnitude (Godot multiplies it in after the conversion, so it must stay
    /// out of the transfer function).</summary>
    private static void SplitLinear(System.Numerics.Vector3 v, out Color color, out float energy)
    {
        float max = MathF.Max(v.X, MathF.Max(v.Y, v.Z));
        if (max <= 0.0001f)
        {
            color = Colors.Black;
            energy = 0f;
            return;
        }
        color = new Color(
            LinearToSrgb(v.X / max),
            LinearToSrgb(v.Y / max),
            LinearToSrgb(v.Z / max));
        energy = max;
    }

    /// <summary>Last reported atmosphere signature, so [SkyAtmos] prints on change rather than
    /// per frame.</summary>
    private string _lastAtmosSig = "";

    /// <summary>The glow factor last pushed to the shaders, so [SkyAtmos] reports the value the
    /// haze is actually scaled by rather than recomputing it and risking a different answer.</summary>
    private float _lastSunMoonGlowFactor = 1f;

    /// <summary>
    /// Reports the numbers the haze is actually computed from, and what they work out to at real
    /// distances.
    ///
    /// <para>This exists because three consecutive attempts at FEAT-RENDER-08 changed the haze
    /// formula and none of them changed the picture, which is itself evidence: a term that is
    /// multiplied by something near zero looks identical no matter how it is written. Working the
    /// arithmetic by hand from a captured region (density_multiplier 0.00018, distance_multiplier
    /// 0.8, haze_density 0.7) gives <c>atten = 0.976</c> at 176 m — under 3 % haze across
    /// everything at that draw distance — while another capture (Lbsa Plaza) carried
    /// <c>1e-07 / 1e-04 / 0</c>, which is no haze at all to eleven decimal places.</para>
    ///
    /// <para>So the open question is not the formula, it is whether these values arrive correct.
    /// The <c>atten@</c> figures below are the same exponential the shader runs, printed at
    /// distances that matter, so the log answers "is there any haze to see" directly instead of
    /// inviting a fourth guess.</para>
    /// </summary>
    private void ReportAtmosphere(SkySettings sky)
    {
        float hazeDensity = SafeFloat(sky.HazeDensity);
        float densityMul = SafeFloat(sky.DensityMultiplier);
        float distanceMul = SafeFloat(sky.DistanceMultiplier);
        var blue = ToColorFast(sky.BlueDensity);

        // Red channel, matching the shader's `color * atten.r` composite.
        float combinedHazeR = MathF.Max(blue.R + hazeDensity, 1e-6f);
        float perMetre = combinedHazeR * densityMul * distanceMul;

        string sig = $"{hazeDensity:0.####}|{densityMul:0.#######}|{distanceMul:0.####}|{blue.R:0.###}|{SafeFloat(sky.MaxY):0.#}";
        if (sig == _lastAtmosSig) return;
        _lastAtmosSig = sig;

        Console.Error.WriteLine(
            $"[SkyAtmos] hazeDensity={hazeDensity:0.####} densityMul={densityMul:0.#######} " +
            $"distanceMul={distanceMul:0.####} blueDensity.r={blue.R:0.###} maxY={SafeFloat(sky.MaxY):0.#} " +
            $"hazeHorizon={SafeFloat(sky.HazeHorizon):0.####} cloudShadow={SafeFloat(sky.CloudShadow):0.###} " +
            $"| atten.r@176m={MathF.Exp(-perMetre * 176f):0.###} @1km={MathF.Exp(-perMetre * 1000f):0.###} " +
            $"@4km={MathF.Exp(-perMetre * 4000f):0.###}");

        // The inputs to the IN-SCATTER, and what they work out to. Logged because the extinction
        // half above was never the open question: for this region haze_weight is 0.96, so what a
        // distant pixel ends up being is almost entirely `additive`, and none of its terms were
        // visible anywhere. "The lighthouse is darker than Firestorm's" is a claim about this
        // number, and until it is printed there is nothing to compare against a reference pixel.
        var blueHorizon = ToColorFast(sky.BlueHorizon);
        var ambient = ToColorFast(sky.AmbientColor);
        var sunlight = ToColorFast(sky.SunlightColor);
        var glow = ToColorFast(sky.Glow);
        float glowFactor = SafeFloat(_lastSunMoonGlowFactor, 1f);
        float hazeHorizon = SafeFloat(sky.HazeHorizon);
        float cloudShadow = SafeFloat(sky.CloudShadow);

        float combinedHazeG = MathF.Max(blue.G + hazeDensity, 1e-6f);
        float combinedHazeB = MathF.Max(blue.B + hazeDensity, 1e-6f);
        float blueWeightR = blue.R / combinedHazeR;
        float hazeWeightR = hazeDensity / combinedHazeR;

        // haze_glow at both ends of the range the shader actually spans. This used to report the
        // 0.25 FLOOR as the away-from-sun value, which is not what the shader computes and
        // understated the in-scatter by about 7x for this region (floor 0.25 against a real 1.87).
        // The floor is added to the directional term, not substituted for it -- reporting it alone
        // made the haze look far too weak to explain what was on screen, which is the opposite of
        // what a diagnostic is for.
        //
        // Mirrors slng_atmospherics.gdshaderinc exactly: for a view direction at angle d to the
        // sun, haze_glow = pow(max(0.001, 1 - d*d) * glow.x, glow.z) + 0.25, all times the glow
        // factor. d=0 is perpendicular to the sun, d->1 is looking straight at it.
        float HazeGlow(float d)
            => (MathF.Pow(MathF.Max(0.001f, 1f - d * d) * glow.R, glow.B) + 0.25f) * glowFactor;

        float glowFloor = HazeGlow(0f);
        float glowNearSun = HazeGlow(0.98f);

        float Additive(float bh, float bw, float hw, float amb, float sun, float hazeGlow)
        {
            float tmpAmbient = amb + (1f - amb) * cloudShadow * 0.5f;
            float cs = sun * (1f - cloudShadow);
            return bh * bw * (cs + tmpAmbient) + hazeHorizon * hw * (cs * hazeGlow + tmpAmbient);
        }

        float addFloorR = Additive(blueHorizon.R, blueWeightR, hazeWeightR, ambient.R, sunlight.R, glowFloor);
        float addSunR = Additive(blueHorizon.R, blueWeightR, hazeWeightR, ambient.R, sunlight.R, glowNearSun);

        // The sunlight attenuation and the elevation it divides by. Logged because the divisor is
        // 1/max(1e-6, elevation) and therefore violent near the horizon -- the difference between
        // "warm haze" and "no warm haze at all" is a couple of hundredths of a unit vector, and
        // that is not something to judge from a screenshot.
        var sunDirGodot = LastSunDirectionGodot;
        var slSun = CalculatedSunDirection;
        float lightY = sunDirGodot.Y >= 0f ? sunDirGodot.Y : MathF.Max(0f, -sunDirGodot.Y);
        float lightAttenR = (blue.R + hazeDensity * 0.25f) * (densityMul * SafeFloat(sky.MaxY));
        float sunAtten = MathF.Exp(-lightAttenR / MathF.Max(1e-6f, lightY));

        Console.Error.WriteLine(
            $"[SkyAtmos] slSun=({slSun.X:0.###},{slSun.Y:0.###},{slSun.Z:0.###}) " +
            $"godotSun=({sunDirGodot.X:0.###},{sunDirGodot.Y:0.###},{sunDirGodot.Z:0.###}) " +
            $"elevation={MathF.Asin(Math.Clamp(sunDirGodot.Y, -1f, 1f)) * 180f / MathF.PI:0.##}deg " +
            $"sunUp={(sunDirGodot.Y >= 0f ? "yes" : "no")} activeLight.y={lightY:0.####} " +
            $"lightAtten.r={lightAttenR:0.####} -> sunlight scaled by {sunAtten:0.####}");

        Console.Error.WriteLine(
            $"[SkyAtmos] blueHorizon=({blueHorizon.R:0.###},{blueHorizon.G:0.###},{blueHorizon.B:0.###}) " +
            $"ambient=({ambient.R:0.###},{ambient.G:0.###},{ambient.B:0.###}) " +
            $"sunlight=({sunlight.R:0.###},{sunlight.G:0.###},{sunlight.B:0.###}) " +
            $"glow=({glow.R:0.###},{glow.G:0.####},{glow.B:0.###}) glowFactor={glowFactor:0.###} " +
            $"| blueWeight={blueWeightR:0.###} hazeWeight={hazeWeightR:0.###} " +
            $"| hazeGlow away={glowFloor:0.##} near={glowNearSun:0.##} " +
            $"| additive.r away-from-sun={addFloorR:0.###} near-sun={addSunR:0.###} " +
            $"(x2 -> {MathF.Min(1f, addFloorR * 2f):0.###} / {MathF.Min(1f, addSunR * 2f):0.###} before srgb_to_linear)");

        // Measure sky.gdshader's sky_col blending (sky_haze) at multiple elevations
        float MeasureSkyGradientR(float elevationDeg)
        {
            float ey = MathF.Sin(elevationDeg * MathF.PI / 180f);
            float viewY = MathF.Max(0.001f, ey);
            float relPosLen = SafeFloat(sky.MaxY) / viewY;
            float densityDist = relPosLen * densityMul;
            float t = MathF.Exp(-combinedHazeR * densityDist); // transmittance
            
            float lightYRaw = slSun.Z; // FIXED: slSun in SL space, Z is up (matches Godot sun_dir.y)
            float offAxisView = 1f / MathF.Max(1e-6f, MathF.Max(0f, ey) + lightYRaw);
            float slAtten = MathF.Exp(-lightAttenR * offAxisView);
            
            float cs = sunlight.R * slAtten * MathF.Max(0f, 1f - cloudShadow);
            float skyAmbBelow = ambient.R + MathF.Max(0f, 1f - ambient.R) * cloudShadow * 0.5f;
            
            float skyCol = (blueHorizon.R * blueWeightR) * (sunlight.R * slAtten + ambient.R)
                         + (hazeHorizon * hazeWeightR) * (sunlight.R * slAtten * glowFloor + ambient.R); // away-from-sun
            skyCol *= 1f - t;
            
            float skyBelow = (blueHorizon.R * blueWeightR) * (cs + skyAmbBelow)
                           + (hazeHorizon * hazeWeightR) * (cs * glowFloor + skyAmbBelow);
            
            float skyHaze1 = MathF.Sqrt(t);
            float skyHaze2 = MathF.Sqrt(skyHaze1); // second sqrt in shader
            float blend = 1f - skyHaze2;
            
            float result = skyCol + (skyBelow - skyCol) * blend;
            return result;
        }

        Console.Error.WriteLine(
            $"[SkyAtmos] sky.gdshader R-channel away-from-sun gradient: " +
            $"at 0.1deg={MeasureSkyGradientR(0.1f):0.###} " +
            $"at 1.0deg={MeasureSkyGradientR(1.0f):0.###} " +
            $"at 5.0deg={MeasureSkyGradientR(5.0f):0.###} " +
            $"at 20deg={MeasureSkyGradientR(20.0f):0.###} " +
            $"at 90deg={MeasureSkyGradientR(90.0f):0.###}");
    }

    /// <summary>The sun direction in Godot world space, as last pushed to
    /// <c>slng_sun_direction</c>. Read by the camera to derive the view-space copy the atmospherics
    /// seam needs.</summary>
    public static Godot.Vector3 LastSunDirectionGodot { get; private set; } = Godot.Vector3.Up;

    private void UpdateGlobalShaderParameters(SkySettings sky, SkyLighting lighting, System.Numerics.Vector3 sunDirectionSl)
    {
        RenderingServer.GlobalShaderParameterSet("slng_ambient", ToColorFast(sky.AmbientColor));
        RenderingServer.GlobalShaderParameterSet("slng_blue_density", ToColorFast(sky.BlueDensity));
        RenderingServer.GlobalShaderParameterSet("slng_blue_horizon", ToColorFast(sky.BlueHorizon));
        RenderingServer.GlobalShaderParameterSet("slng_haze_density", SafeFloat(sky.HazeDensity));
        RenderingServer.GlobalShaderParameterSet("slng_haze_horizon", SafeFloat(sky.HazeHorizon));
        RenderingServer.GlobalShaderParameterSet("slng_density_multiplier", SafeFloat(sky.DensityMultiplier));
        RenderingServer.GlobalShaderParameterSet("slng_distance_multiplier", SafeFloat(sky.DistanceMultiplier));
        RenderingServer.GlobalShaderParameterSet("slng_max_y", SafeFloat(sky.MaxY));
        RenderingServer.GlobalShaderParameterSet("slng_glow", ToColorFast(sky.Glow));
        RenderingServer.GlobalShaderParameterSet("slng_cloud_shadow", SafeFloat(sky.CloudShadow));
        RenderingServer.GlobalShaderParameterSet("slng_dome_offset", SafeFloat(sky.DomeOffset, 0.96f));

        // getSunMoonGlowFactor, llsettingssky.cpp:1317-1321. The sky and cloud shaders each gate
        // their halo with this; see sky.gdshader for why the two do it differently.
        bool sunIsUp = sunDirectionSl.Z >= 0f;
        bool moonIsUp = CalculatedLightDirection.Z >= 0f;
        float glowFactor = sunIsUp ? 1.0f
            : moonIsUp ? SafeFloat(sky.MoonBrightness) * 0.25f
            : 0.0f;
        RenderingServer.GlobalShaderParameterSet("slng_sun_moon_glow_factor", glowFactor);
        _lastSunMoonGlowFactor = glowFactor;

        // The sky/cloud shaders take the RAW settings, not SkyLighting's output. The viewer binds
        // SG_SKY's sunlight_color to psky->getSunlightColor() and moonlight_color to
        // getMoonlightColor() — which is getSunlightColor() again, since the moon and sun share a
        // colour in SL (llsettingsvo.cpp:782-785, llsettingssky.cpp:1681-1684). skyV.glsl and
        // cloudsV.glsl then each apply their own exp(-light_atten * off_axis).
        //
        // Publishing SunDiffuse here meant the dome attenuated an already-attenuated colour, and
        // that is the third time calculateLightSettings' output has been fed somewhere the viewer
        // feeds a raw setting. Its outputs drive the Godot DirectionalLight and the ambient energy
        // (ApplyLighting below) and nothing else — see ADR 0003.
        RenderingServer.GlobalShaderParameterSet("slng_sunlight_color", ToColorFast(sky.SunlightColor));
        RenderingServer.GlobalShaderParameterSet("slng_moonlight_color", ToColorFast(sky.SunlightColor));

        // The sun DISC is the one thing the viewer still colours with calculateLightSettings'
        // output: `mSun.setColor(psky->getSunDiffuse())` (llvosky.cpp:527), consumed by
        // LLDrawPoolWLSky::renderHeavenlyBodies through getInterpColor(). ADR 0003 dismissed
        // llvosky as "the pre-EEP sky object" and that is wrong — the deferred renderer still
        // draws its sun and moon billboards.
        //
        // It matters because SunDiffuse is atmospherically attenuated and the raw setting is not.
        // On The Dangazi Forest's parcel-6 sunset the raw sunlight_color is (2.43, 2.44, 2.46)
        // while SunDiffuse is (0.48, 0.16, 0.03) — the difference between a neutral white blob
        // and an orange-red setting sun.
        //
        // The moon disc is NOT tinted: llvosky.cpp:528 sets pure white, so the shader uses a
        // constant and needs nothing from here.
        RenderingServer.GlobalShaderParameterSet("slng_sun_disc_color", ToColorFast(lighting.SunDiffuse));
        RenderingServer.GlobalShaderParameterSet("slng_haze_color", ToColorFast(lighting.HazeColor));

        // star_brightness is an EEP setting on a 0..500 scale (validator range, llsettingssky.cpp:781),
        // not a 0..1 factor. The viewer converts it to an alpha before it ever reaches a shader:
        // `star_alpha = getStarBrightness() / 500` (lldrawpoolwlsky.cpp:230). Publishing the raw
        // value made stars up to 500x too bright -- a blazing starfield over a region whose own
        // frames ask for values between 0 and 500, where the real viewer showed almost none.
        RenderingServer.GlobalShaderParameterSet("slng_star_brightness", SafeFloat(sky.StarBrightness) / 500f);
        RenderingServer.GlobalShaderParameterSet("slng_sun_scale", SafeFloat(sky.SunScale, 1.0f));
        RenderingServer.GlobalShaderParameterSet("slng_moon_scale", SafeFloat(sky.MoonScale, 1.0f));
        RenderingServer.GlobalShaderParameterSet("slng_moon_brightness", SafeFloat(sky.MoonBrightness, 0.5f));
        
        RenderingServer.GlobalShaderParameterSet("slng_cloud_color", ToColorFast(sky.CloudColor));

        // Cloud drift. The shader reads slng_cloud_pos_density1.xy as its UV offset (already
        // mod 10000 there, like the viewer), so scrolling is entirely a matter of feeding it a
        // MOVING value -- which nothing did, leaving the clouds frozen. Applying the accumulator
        // is llsettingsvo.cpp:766-777; the X negation is SL-13084 (custom cloud textures flipped
        // horizontally to match the Clouds > Cloud Scroll preview), deliberate in the viewer and
        // kept in sync across three of its files.
        var cloudPos1 = sky.CloudPosDensity1;
        cloudPos1.X -= _cloudScrollDelta.X;
        cloudPos1.Y += _cloudScrollDelta.Y;
        RenderingServer.GlobalShaderParameterSet("slng_cloud_pos_density1", ToColorFast(cloudPos1));
        RenderingServer.GlobalShaderParameterSet("slng_cloud_pos_density2", ToColorFast(sky.CloudPosDensity2));
        RenderingServer.GlobalShaderParameterSet("slng_cloud_scale", SafeFloat(sky.CloudScale, 0.42f));
        RenderingServer.GlobalShaderParameterSet("slng_cloud_variance", SafeFloat(sky.CloudVariance, 1.0f));

        // Convert SL Z-up vector to Godot Y-up vector
        var toSun = new Godot.Vector3(CalculatedSunDirection.X, CalculatedSunDirection.Z, -CalculatedSunDirection.Y);
        if (toSun.LengthSquared() > 0.0001f) toSun = toSun.Normalized();
        else toSun = Godot.Vector3.Up;
        RenderingServer.GlobalShaderParameterSet("slng_sun_direction", toSun);
        // Handed to AvatarController, which needs it in VIEW space every frame. Published as a
        // plain static rather than read back with GlobalShaderParameterGet: that call logs a
        // RenderingServer error with a full C# backtrace whenever the parameter is not registered,
        // and at 60 fps that is not a diagnostic, it is a denial of service on the log -- 590 such
        // blocks in one session, next to 186 149 shader warnings, from exactly that mistake.
        // EnvironmentDriver stays the single writer either way.
        LastSunDirectionGodot = toSun;

        // Reported HERE, not at the top of this method. It used to run first and read
        // LastSunDirectionGodot ninety lines before that field was assigned, so it printed the
        // PREVIOUS call's sun -- which is how it came to claim activeLight.y=0 exactly, a value
        // clean enough that it nearly justified a change to the shader. A diagnostic that reads
        // its subject before the subject is written is worse than no diagnostic: it is a wrong
        // answer wearing a measurement's clothes.
        ReportAtmosphere(sky);
        
        // Use the region's actual moon rotation if usable, otherwise opposite the sun
        var moonDirSl = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, sky.MoonRotation);
        bool moonUsable = !float.IsNaN(moonDirSl.X) && moonDirSl.LengthSquared() > 0.0001f;
        Godot.Vector3 toMoon;
        if (moonUsable)
        {
            var gv = new Godot.Vector3(moonDirSl.X, moonDirSl.Z, -moonDirSl.Y);
            toMoon = gv.LengthSquared() > 0.0001f ? gv.Normalized() : -toSun;
        }
        else
        {
            toMoon = -toSun;
        }
        RenderingServer.GlobalShaderParameterSet("slng_moon_direction", toMoon);
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

    /// <summary>Aims the scene's one directional light. Below the horizon the MOON is the light
    /// source, and SL derives it as a separate value carrying the region's own moon_brightness
    /// (SkyLighting.MoonDiffuse). Feeding SunDiffuse around the clock lit midnight with the
    /// daytime sun colour -- terrain plainly readable at night where the real viewer has it in
    /// near-silhouette. sky.gdshader:141-143 already switches on exactly this test; only the
    /// driver, which drives the light that actually shades geometry, did not.</summary>
    private void ApplySun(DirectionalLight3D? sun, SkySettings sky, SkyLighting lighting, float lightDirectionZ)
    {
        if (sun == null) return;

        bool moonUp = lightDirectionZ <= 0f;
        var diffuse = moonUp ? lighting.MoonDiffuse * MoonLightScale : lighting.SunDiffuse;

        System.Numerics.Vector3 radiance;
        if (sky.IsLegacy)
        {
            // The sunlit endpoint MINUS the shadow endpoint, so that adding Godot's linear
            // ambient back reproduces the viewer's fully-lit pixel. See ClassicShadowRadiance
            // for why the two endpoints are what gets matched.
            var ambSrgb = ClassicAmbientSrgb(lighting);
            var litSrgb = ambSrgb + diffuse * (SkySunlightScale * ClassicSunlitScale);
            radiance = SrgbToLinearVec(litSrgb) * ClassicFinalScale - ClassicShadowRadiance(lighting);
            radiance = System.Numerics.Vector3.Max(radiance, System.Numerics.Vector3.Zero);
        }
        else
        {
            // FEAT-ENV-03: the classic_mode<1 branch (softenLightF.glsl:234-238,
            // atmosphericsFuncs.glsl:154-159) converts sun and ambient to linear separately and
            // sums them directly -- no 1.35 boost, no sRGB mix, no final_scale. That is exactly
            // Godot's own additive lighting model (ambient + NdotL * light), so unlike the
            // classic branch above this needs no endpoint-subtraction trick: the sun light can
            // simply carry its own linear radiance and let Godot's ambient add on top.
            radiance = SrgbToLinearVec(diffuse) * SkySunlightScale;
        }

        SplitLinear(radiance, out var color, out var energy);
        sun.LightColor = color;
        // At night, ensure directional moonlight has enough energy to cast crisp shadows
        // and specular water reflections, matching Firestorm
        if (moonUp)
        {
            energy = Mathf.Max(energy, 0.4f);
        }
        // Clamped rather than left open-ended so a sky with an extreme setting cannot blow the
        // exposure out past what the tonemapper (Linear, set in Boot.SetupEnvironment) can recover.
        sun.LightEnergy = Mathf.Clamp(energy, 0f, 3f);
    }

    // softenLightF.glsl:159-160 and :226-233, the classic_mode > 0 branch. The viewer boosts the
    // sunlight 1.35x, then mixes it into the ambient at 0.7 while scaling the ambient itself by
    // 0.9 -- and does that sum in sRGB, converting once afterwards. The frame is finally scaled
    // by 1.1 (:280-281).
    private const float ClassicSunlitScale = 1.35f * 0.7f;
    private const float ClassicAmbientScale = 0.9f;
    private const float ClassicFinalScale = 1.1f;

    // atmosphericsFuncs.glsl:163-164, applied OUTSIDE the classic_mode branch so they reach every
    // sky: RenderSkySunlightScale and RenderSkyAmbientScale.
    //
    // 1.0, NOT Linden's 1.5. These are gSavedSettings rather than sky settings, and Firestorm --
    // which is what we are compared against -- overrides both in its own settings.xml, with the
    // comment "fudge factor for matching with pre-PBR viewer". Checked against
    // FirestormViewer/phoenix-firestorm@master:indra/newview/app_settings/settings.xml, which also
    // confirms RenderSkyAutoAdjustLegacy = 0 (so classic mode is on, as ADR 0003 concluded).
    //
    // Shipping Linden's 1.5 here made the scene brighter at EVERY albedo without changing the
    // sun/ambient ratio at all -- the probe sphere read washed out rather than brighter, because
    // lifting both lights together only slides the pixel up the compressive part of the sRGB
    // encode. Contrast is the ratio; brightness is not contrast.
    private const float SkySunlightScale = 1.0f;
    private const float SkyAmbientScale = 1.0f;

    /// <summary><c>amblit</c> as the viewer hands it to a surface, still sRGB-valued:
    /// <c>pow(tmpAmbient, 0.9) * 0.57</c> (atmosphericsFuncs.glsl:125), scaled by the 0.9 the
    /// classic mix applies to it. In classic mode NOTHING converts this to linear and nothing
    /// greys it out — both of those live in the <c>classic_mode &lt; 1</c> branch
    /// (atmosphericsFuncs.glsl:153-158), which is not the branch these skies take.</summary>
    private static System.Numerics.Vector3 ClassicAmbientSrgb(SkyLighting lighting)
    {
        var amb = lighting.SunAmbient;
        return new System.Numerics.Vector3(
            MathF.Pow(MathF.Max(amb.X, 0f), 0.9f),
            MathF.Pow(MathF.Max(amb.Y, 0f), 0.9f),
            MathF.Pow(MathF.Max(amb.Z, 0f), 0.9f)) * (0.57f * SkyAmbientScale * ClassicAmbientScale);
    }

    /// <summary>The linear radiance a fully shadowed surface receives in the viewer's classic
    /// path: <c>srgb_to_linear(amblit * 0.9) * 1.1</c>.
    ///
    /// Godot sums its lights in LINEAR space; the viewer sums sun and ambient in sRGB and converts
    /// the sum once. Those cannot be made identical by rescaling, so what is matched instead are
    /// the two ENDPOINTS a viewer actually looks at — the fully shadowed pixel (here) and the
    /// fully sunlit one (ApplySun) — and the two interpolate slightly differently in between.
    /// Matching only the magnitudes was never the issue: a transfer function applied where the
    /// viewer applies none exaggerates every colour RATIO by the same 2.4 power, which is what
    /// turned a mildly blue ambient (R/B 0.77) into a strongly blue one (0.77^2.4 = 0.30) and a
    /// warm sun (R/B 0.58) into an orange one (0.27). Reported live as "shadow sides too blue,
    /// sun sides too red".</summary>
    private static System.Numerics.Vector3 ClassicShadowRadiance(SkyLighting lighting)
        => SrgbToLinearVec(ClassicAmbientSrgb(lighting)) * ClassicFinalScale;

    /// <summary>FEAT-ENV-03: the classic_mode&lt;1 ambient a MODERN sky reaches geometry with --
    /// <c>srgb_to_linear(pow(tmpAmbient,0.9)*0.57*sky_ambient_scale)</c>, then greyscaled by
    /// luminance (atmosphericsFuncs.glsl:154-157). Unlike <see cref="ClassicAmbientSrgb"/> this
    /// carries none of the classic branch's 0.9 ambient-mix weight -- that weight belongs to
    /// softenLightF's sRGB mix step (softenLightF.glsl:231), which the modern branch never
    /// performs; it sums already-linear ambient and sun directly instead (see <see
    /// cref="ApplySun"/>'s non-legacy branch).</summary>
    private static System.Numerics.Vector3 ModernAmbientLinear(SkyLighting lighting)
    {
        var amb = lighting.SunAmbient;
        var amblitSrgb = new System.Numerics.Vector3(
            MathF.Pow(MathF.Max(amb.X, 0f), 0.9f),
            MathF.Pow(MathF.Max(amb.Y, 0f), 0.9f),
            MathF.Pow(MathF.Max(amb.Z, 0f), 0.9f)) * (0.57f * SkyAmbientScale);

        var linear = SrgbToLinearVec(amblitSrgb);
        float grey = linear.X * 0.2126f + linear.Y * 0.7152f + linear.Z * 0.0722f;
        return new System.Numerics.Vector3(grey, grey, grey);
    }

    private static System.Numerics.Vector3 SrgbToLinearVec(System.Numerics.Vector3 v)
        => new(SrgbToLinear(v.X), SrgbToLinear(v.Y), SrgbToLinear(v.Z));

    private void ApplyAmbient(Godot.Environment env, SkySettings sky, SkyLighting lighting)
    {
        // Switching AmbientLightSource away from Sky is required, not cosmetic: Godot ignores
        // AmbientLightColor entirely while the source is Sky (it derives ambient from the sky
        // radiance instead), so writing the colour below would silently do nothing otherwise.
        env.AmbientLightSource = Godot.Environment.AmbientSource.Color;

        // SL never lights a surface with tmpAmbient directly. calcAtmosphericVars hands the
        // renderer `amblit = pow(tmpAmbient, 0.9) * 0.57` (atmosphericsFuncs.glsl:125) and only
        // that reaches geometry; SkyLighting.SunAmbient is the RAW tmpAmbient, one step earlier.
        // Passing it through unscaled overlights every surface by about 1.7x, and since ambient
        // has no direction the excess reads as flat, shadowless ground rather than as "too
        // bright". Measured on this region's sunset: raw ambient energy 0.84 against a derived
        // sun energy of 0.24, so the terrain was more than three-quarters ambient-lit and looked
        // midday-bright under a sun sitting on the horizon.
        // Deliberately NOT switched to MoonAmbient at night. That was tried and was wrong: the
        // viewer computes mMoonAmbient (its hardcoded scotopic (0.66, 0.66, 1.2) * 0.0125) and
        // then never reads it -- getMoonAmbient() has no caller anywhere outside the file that
        // defines it, and the same is true of getMoonDiffuse(). What actually reaches the
        // atmospherics is getAmbientColor(), the current frame's own raw setting
        // (llsettingsvo.cpp:729), day or night; calculateLightSettings even records it plainly as
        // `mTotalAmbient = ambient`. SL's nights are dark because a region's night KEYFRAME
        // carries a low ambient, not because the viewer substitutes one. Applying the scotopic
        // value (~0.008, i.e. effectively black) buried the whole scene.
        // The function that actually lights SURFACES is calcAtmosphericVarsLinear
        // (atmosphericsFuncs.glsl:147), not calcAtmosphericVars -- and the two extra steps it is
        // famous for,
        //     amblit = srgb_to_linear(amblit);
        //     amblit = vec3(dot(amblit, vec3(0.2126, 0.7152, 0.0722)));
        // sit INSIDE `if (classic_mode < 1)`. FEAT-ENV-03 gave that branch its own path
        // (ModernAmbientLinear) instead of applying it universally: on a LEGACY sky (no
        // reflection_probe_ambiance, canAutoAdjust true, classic mode on) the ambient must stay
        // sRGB-valued and coloured -- applying this conversion there too, on top of the one Godot
        // runs on AmbientLightColor, is what made shadow sides read blue: two 2.4 powers on a
        // mildly blue ambient. A MODERN sky takes exactly this branch instead.
        var amb = sky.IsLegacy ? ClassicShadowRadiance(lighting) : ModernAmbientLinear(lighting);
        float currentLum = amb.X * 0.2126f + amb.Y * 0.7152f + amb.Z * 0.0722f;
        if (currentLum < 0.02f)
        {
            float scale = 0.02f / MathF.Max(currentLum, 0.0001f);
            amb = new System.Numerics.Vector3(amb.X * scale, amb.Y * scale, amb.Z * scale);
        }

        SplitLinear(amb, out var color, out var energy);
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
    /// specifically, so it is retired rather than mapped.
    ///
    /// <para>This method is the SINGLE owner of both fog flags, and that had to be made true
    /// rather than merely intended. <c>GraphicsSettings.Apply</c> also wrote
    /// <c>VolumetricFogEnabled</c>, from a user preference that defaulted to on -- and since this
    /// runs every frame and that runs only when the preference page applies, the driver won every
    /// time. The setting was therefore not a second fog model competing with the seam; it was a
    /// checkbox that changed nothing, plus an F2 shortcut whose "is any post-FX on?" test was
    /// permanently true because of it. Retired in FEAT-RENDER-01 Phase 5; do not re-introduce a
    /// preference for either flag without first deciding what it would mean next to the
    /// per-fragment seam.</para></summary>
    private void ApplyFog(Godot.Environment env, SkySettings sky, SkyLighting lighting)
    {
        // Godot's built-in standard and volumetric fogs conflict with our procedural sky
        // and transparent water shaders, creating sharp horizons or failing to render.
        // Windlight / EEP atmospheric scattering is applied per-fragment in Phase 5 via
        // slng_atmospherics.gdshaderinc instead.
        env.FogEnabled = false;
        env.VolumetricFogEnabled = false;
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

        waterMaterial.SetShaderParameter("slng_water_fog_density", water.FogDensity);
        waterMaterial.SetShaderParameter("slng_water_fresnel_scale", water.FresnelScale);
        waterMaterial.SetShaderParameter("slng_water_fresnel_offset", water.FresnelOffset);
        waterMaterial.SetShaderParameter("slng_water_scale_above", water.ScaleAbove);
        waterMaterial.SetShaderParameter("slng_water_scale_below", water.ScaleBelow);
        waterMaterial.SetShaderParameter("slng_water_blur_multiplier", water.BlurMultiplier);
        waterMaterial.SetShaderParameter("slng_water_normal_scale", new Vector3(water.NormalScale.X, water.NormalScale.Y, water.NormalScale.Z));
        waterMaterial.SetShaderParameter("slng_water_wave1_dir", new Vector2(water.Wave1Direction.X, water.Wave1Direction.Y));
        waterMaterial.SetShaderParameter("slng_water_wave2_dir", new Vector2(water.Wave2Direction.X, water.Wave2Direction.Y));
    }
}
