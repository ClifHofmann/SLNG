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

        ApplySun(sun, lighting, lightDirectionZ);
        ApplyAmbient(env, lighting);
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
        if (wantedId == Guid.Empty || wantedId == slot.Current) return;
        if (assetService == null || gpuCache == null || !assetsReady) return;

        ulong now = Time.GetTicksMsec();
        if (now < slot.RetryAfterMs) return;
        slot.RetryAfterMs = now + TextureRetryDelayMs;

        slot.Current = wantedId;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var tex = await gpuCache.GetOrUploadTextureAsync(wantedId, assetService, generateMipmaps: true).ConfigureAwait(false);
            MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Refine, () =>
            {
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

        var amb = lighting.SunAmbient;
        amb = new System.Numerics.Vector3(
            MathF.Pow(MathF.Max(amb.X, 0f), 0.9f),
            MathF.Pow(MathF.Max(amb.Y, 0f), 0.9f),
            MathF.Pow(MathF.Max(amb.Z, 0f), 0.9f)) * 0.57f;
        amb = new System.Numerics.Vector3(SrgbToLinear(amb.X), SrgbToLinear(amb.Y), SrgbToLinear(amb.Z));
        float ambEnergy = amb.X * 0.2126f + amb.Y * 0.7152f + amb.Z * 0.0722f;

        // Only on a material move, so a static sky logs once rather than 60x a second.
        if (MathF.Abs(sunEnergy - _lastLoggedSunEnergy) < 0.02f &&
            MathF.Abs(ambEnergy - _lastLoggedAmbEnergy) < 0.02f) return;
        _lastLoggedSunEnergy = sunEnergy;
        _lastLoggedAmbEnergy = ambEnergy;

        var lit = moonUp ? lighting.MoonDiffuse : lighting.SunDiffuse;

        GD.Print($"[ENVDBG] body={(moonUp ? "moon" : "sun")} sunZ={lightDirectionZ:F3} " +
                 $"lightZ={CalculatedLightDirection.Z:F3} " +
                 $"=> lightEnergy={sunEnergy:F3} ambEnergy={ambEnergy:F3}");
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

    /// <summary>Last reported atmosphere signature, so [SkyAtmos] prints on change rather than
    /// per frame.</summary>
    private string _lastAtmosSig = "";

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
    }

    private void UpdateGlobalShaderParameters(SkySettings sky, SkyLighting lighting, System.Numerics.Vector3 sunDirectionSl)
    {
        ReportAtmosphere(sky);

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
    private void ApplySun(DirectionalLight3D? sun, SkyLighting lighting, float lightDirectionZ)
    {
        if (sun == null) return;

        bool moonUp = lightDirectionZ <= 0f;
        var diffuse = moonUp ? lighting.MoonDiffuse * MoonLightScale : lighting.SunDiffuse;
        Split(diffuse, out var color, out var energy);
        sun.LightColor = color;
        // At night, ensure directional moonlight has enough energy to cast crisp shadows
        // and specular water reflections, matching Firestorm
        if (moonUp)
        {
            energy = Mathf.Max(energy, 0.4f);
        }
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
        var amb = lighting.SunAmbient;
        amb = new System.Numerics.Vector3(
            MathF.Pow(MathF.Max(amb.X, 0f), 0.9f),
            MathF.Pow(MathF.Max(amb.Y, 0f), 0.9f),
            MathF.Pow(MathF.Max(amb.Z, 0f), 0.9f)) * 0.57f;

        // The function that actually lights SURFACES is calcAtmosphericVarsLinear
        // (atmosphericsFuncs.glsl:147), not calcAtmosphericVars -- and it does two more things to
        // amblit before any geometry sees it:
        //     amblit = srgb_to_linear(amblit);
        //     amblit = vec3(dot(amblit, vec3(0.2126, 0.7152, 0.0722)));
        // The sRGB->linear step is the one that matters here. It is not a uniform dimming: it
        // pushes values below 1 down hard (0.37 -> 0.11) while lifting values above 1, i.e. it
        // stretches contrast. That is what lets the real viewer hold a bright twilight sky over
        // near-black ground at the same time -- the pairing that made our night look wrong even
        // after the ambient scale was corrected. Every one of this region's 21 keyframes carries a
        // high ambient (0.54..1.20), so without this step night could not be dark no matter what.
        // Then the luminance dot greys it: SL's ambient tints nothing, it only sets a level.
        amb = new System.Numerics.Vector3(SrgbToLinear(amb.X), SrgbToLinear(amb.Y), SrgbToLinear(amb.Z));
        float lum = amb.X * 0.2126f + amb.Y * 0.7152f + amb.Z * 0.0722f;
        // Keep a readable baseline ambient level so night scenes are not pitch-black, matching Firestorm
        lum = MathF.Max(lum, 0.08f);
        amb = new System.Numerics.Vector3(lum, lum, lum);

        Split(amb, out var color, out var energy);
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
