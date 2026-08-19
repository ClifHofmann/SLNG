using System.Numerics;
using LibreMetaverse.StructuredData;
using SLNG.Core;

namespace SLNG.Net;

/// <summary>Turns the environment LLSD the simulator sends into SLNG's engine- and
/// protocol-neutral environment model (FEAT-ENV-01 Phase B).
///
/// This class is the layer boundary. <c>OSD</c> is a LibreMetaverse type and must not appear above
/// <c>SLNG.Net</c> (AGENTS.md), so every public method here takes LLSD and returns
/// <c>SLNG.Core</c> records.
///
/// LibreMetaverse decodes the ExtEnvironment envelope but not the settings inside it — the
/// <c>day_cycle</c> field arrives as raw <c>OSD</c>. Everything below is therefore written against
/// the viewer's own source rather than against LibreMetaverse: key names from
/// <c>llsettingssky.cpp:72-113</c> / <c>llsettingswater.cpp:36-48</c>, the day-cycle shape from
/// <c>llsettingsdaycycle.cpp</c>, and every default from the viewer's <c>defaults()</c> tables.
///
/// Missing keys are never an error. A region sends what it sends; anything absent falls back to
/// the viewer's default, which is what the real viewer does and the only behaviour that renders a
/// sparse legacy Windlight setting correctly.</summary>
internal static class EnvironmentLlsdParser
{
    // The day-cycle document: "frames" is a map of name -> settings, "tracks" is an array of
    // tracks, each an array of { key_keyframe, key_name }. Track 0 is water, track 1 is
    // ground-level sky (llsettingsdaycycle.cpp:98-103, :205-260).
    private const string KeyFrames = "frames";
    private const string KeyTracks = "tracks";
    private const string KeyKeyframe = "key_keyframe";
    private const string KeyName = "key_name";
    private const string KeyType = "type";

    // SL nests the haze parameters under "legacy_haze" when they came from a legacy Windlight
    // setting, and puts them at the top level otherwise. The viewer's own getters check the
    // sub-map FIRST and fall through to the top level (llsettingssky.cpp:1383-1411), so a parser
    // that only reads one of the two silently renders half the regions with default haze.
    private const string KeyLegacyHaze = "legacy_haze";

    /// <summary>Parses an EEP day cycle, or a single sky/water settings document, into a
    /// <see cref="DayCycle"/>.
    ///
    /// A region may legitimately send any of the three: the LLSD's <c>type</c> key discriminates.
    /// A single sky or water document becomes a one-keyframe cycle, which evaluates to a fixed
    /// sky — the same result the viewer gets, without a special case downstream.</summary>
    public static DayCycle ParseDayCycle(OSD? llsd, int dayLengthSeconds, int dayOffsetSeconds)
    {
        if (llsd is not OSDMap map) return DayCycle.Default;

        string type = map.ContainsKey(KeyType) ? map[KeyType].AsString() : string.Empty;

        if (type == "sky")
        {
            return new DayCycle(
                new[] { new DayCycleFrame<SkySettings>(0f, ParseSky(map)) },
                new[] { new DayCycleFrame<WaterSettings>(0f, WaterSettings.Default) },
                dayLengthSeconds, dayOffsetSeconds);
        }

        if (type == "water")
        {
            return new DayCycle(
                new[] { new DayCycleFrame<SkySettings>(0f, SkySettings.Default) },
                new[] { new DayCycleFrame<WaterSettings>(0f, ParseWater(map)) },
                dayLengthSeconds, dayOffsetSeconds);
        }

        var frames = map.ContainsKey(KeyFrames) ? map[KeyFrames] as OSDMap : null;
        var tracks = map.ContainsKey(KeyTracks) ? map[KeyTracks] as OSDArray : null;
        if (frames == null || tracks == null) return DayCycle.Default;

        var waterFrames = ParseTrack(tracks, 0, frames, ParseWater, WaterSettings.Default);
        var skyFrames = ParseTrack(tracks, 1, frames, ParseSky, SkySettings.Default);

        return new DayCycle(skyFrames, waterFrames, dayLengthSeconds, dayOffsetSeconds);
    }

    /// <summary>Resolves one track's keyframe references against the document's frame table.
    ///
    /// Keyframes are sorted by position on the way out because <see cref="DayCycle"/>'s
    /// evaluation scans for the bracketing pair and relies on the order; the wire format makes no
    /// such promise. Unresolvable names are skipped rather than substituted, so a broken
    /// reference costs one keyframe instead of pinning the whole track to a default.</summary>
    private static List<DayCycleFrame<T>> ParseTrack<T>(
        OSDArray tracks,
        int trackIndex,
        OSDMap frames,
        Func<OSDMap, T> parse,
        T fallback)
    {
        var result = new List<DayCycleFrame<T>>();

        if (trackIndex < tracks.Count && tracks[trackIndex] is OSDArray track)
        {
            foreach (var entry in track)
            {
                if (entry is not OSDMap keyframe) continue;
                if (!keyframe.ContainsKey(KeyName)) continue;

                string name = keyframe[KeyName].AsString();
                if (!frames.ContainsKey(name) || frames[name] is not OSDMap settings) continue;

                float position = keyframe.ContainsKey(KeyKeyframe)
                    ? (float)keyframe[KeyKeyframe].AsReal()
                    : 0f;

                result.Add(new DayCycleFrame<T>(Math.Clamp(position, 0f, 1f), parse(settings)));
            }
        }

        if (result.Count == 0) result.Add(new DayCycleFrame<T>(0f, fallback));

        result.Sort((a, b) => a.Position.CompareTo(b.Position));
        return result;
    }

    /// <summary>Parses one sky settings document. Starts from the viewer's defaults and overrides
    /// only what is present, so a sparse document renders like the viewer's rather than black.</summary>
    public static SkySettings ParseSky(OSDMap map)
    {
        var d = SkySettings.Default;
        var haze = map.ContainsKey(KeyLegacyHaze) ? map[KeyLegacyHaze] as OSDMap : null;

        return new SkySettings
        {
            // Haze block: legacy_haze first, then top level (llsettingssky.cpp:1383-1411).
            AmbientColor = Color(map, haze, "ambient", d.AmbientColor),
            BlueDensity = Color(map, haze, "blue_density", d.BlueDensity),
            BlueHorizon = Color(map, haze, "blue_horizon", d.BlueHorizon),
            HazeDensity = Real(map, haze, "haze_density", d.HazeDensity),
            HazeHorizon = Real(map, haze, "haze_horizon", d.HazeHorizon),
            DensityMultiplier = Real(map, haze, "density_multiplier", d.DensityMultiplier),
            DistanceMultiplier = Real(map, haze, "distance_multiplier", d.DistanceMultiplier),

            MaxY = Real(map, null, "max_y", d.MaxY),
            Glow = Color(map, null, "glow", d.Glow),
            Gamma = Real(map, null, "gamma", d.Gamma),
            CloudShadow = Real(map, null, "cloud_shadow", d.CloudShadow),
            SunlightColor = Color(map, null, "sunlight_color", d.SunlightColor),

            SunRotation = Rotation(map, "sun_rotation", d.SunRotation),
            MoonRotation = Rotation(map, "moon_rotation", d.MoonRotation),
            MoonBrightness = Real(map, null, "moon_brightness", d.MoonBrightness),
            StarBrightness = Real(map, null, "star_brightness", d.StarBrightness),
            SunScale = Real(map, null, "sun_scale", d.SunScale),
            MoonScale = Real(map, null, "moon_scale", d.MoonScale),

            CloudColor = Color(map, null, "cloud_color", d.CloudColor),
            CloudPosDensity1 = Color(map, null, "cloud_pos_density1", d.CloudPosDensity1),
            CloudPosDensity2 = Color(map, null, "cloud_pos_density2", d.CloudPosDensity2),
            CloudScale = Real(map, null, "cloud_scale", d.CloudScale),
            CloudScrollRate = Vec2(map, "cloud_scroll_rate", d.CloudScrollRate),
            CloudVariance = Real(map, null, "cloud_variance", d.CloudVariance),
            CloudTextureId = Id(map, "cloud_id"),
        };
    }

    /// <summary>Parses one water settings document.
    ///
    /// Legacy Windlight spells the same fields in camelCase (<c>waterFogColor</c> vs
    /// <c>water_fog_color</c>, llsettingswater.cpp:50+), so each lookup tries both. Unlike the
    /// sky's <c>legacy_haze</c> this is a flat alias, not a nested map.</summary>
    public static WaterSettings ParseWater(OSDMap map)
    {
        var d = WaterSettings.Default;

        return new WaterSettings
        {
            FogColor = ColorAlias(map, "water_fog_color", "waterFogColor", d.FogColor),
            FogDensity = RealAlias(map, "water_fog_density", "waterFogDensity", d.FogDensity),
            UnderwaterFogMod = RealAlias(map, "underwater_fog_mod", "underWaterFogMod", d.UnderwaterFogMod),
            FresnelOffset = RealAlias(map, "fresnel_offset", "fresnelOffset", d.FresnelOffset),
            FresnelScale = RealAlias(map, "fresnel_scale", "fresnelScale", d.FresnelScale),
            BlurMultiplier = RealAlias(map, "blur_multiplier", "blurMultiplier", d.BlurMultiplier),
            NormalScale = ColorAlias(map, "normal_scale", "normScale", d.NormalScale),
            ScaleAbove = RealAlias(map, "scale_above", "scaleAbove", d.ScaleAbove),
            ScaleBelow = RealAlias(map, "scale_below", "scaleBelow", d.ScaleBelow),
            Wave1Direction = Vec2Alias(map, "wave1_direction", "wave1Dir", d.Wave1Direction),
            Wave2Direction = Vec2Alias(map, "wave2_direction", "wave2Dir", d.Wave2Direction),
            NormalMapId = Id(map, "normal_map"),
        };
    }

    // --- LLSD readers -----------------------------------------------------------------------
    // All of them take a default and return it on absence or on a type mismatch. LLSD is
    // schema-free and grids disagree about it; a parser that throws on a surprise would fail the
    // whole environment over one odd key.

    private static float Real(OSDMap map, OSDMap? preferred, string key, float fallback)
    {
        if (preferred != null && preferred.ContainsKey(key)) return (float)preferred[key].AsReal();
        return map.ContainsKey(key) ? (float)map[key].AsReal() : fallback;
    }

    private static float RealAlias(OSDMap map, string key, string legacyKey, float fallback)
    {
        if (map.ContainsKey(key)) return (float)map[key].AsReal();
        return map.ContainsKey(legacyKey) ? (float)map[legacyKey].AsReal() : fallback;
    }

    private static Vector3 Color(OSDMap map, OSDMap? preferred, string key, Vector3 fallback)
    {
        if (preferred != null && preferred.ContainsKey(key)) return ToVector3(preferred[key], fallback);
        return map.ContainsKey(key) ? ToVector3(map[key], fallback) : fallback;
    }

    private static Vector3 ColorAlias(OSDMap map, string key, string legacyKey, Vector3 fallback)
    {
        if (map.ContainsKey(key)) return ToVector3(map[key], fallback);
        return map.ContainsKey(legacyKey) ? ToVector3(map[legacyKey], fallback) : fallback;
    }

    private static Vector2 Vec2(OSDMap map, string key, Vector2 fallback)
        => map.ContainsKey(key) ? ToVector2(map[key], fallback) : fallback;

    private static Vector2 Vec2Alias(OSDMap map, string key, string legacyKey, Vector2 fallback)
    {
        if (map.ContainsKey(key)) return ToVector2(map[key], fallback);
        return map.ContainsKey(legacyKey) ? ToVector2(map[legacyKey], fallback) : fallback;
    }

    private static Guid Id(OSDMap map, string key)
        => map.ContainsKey(key) ? map[key].AsUUID().Guid : Guid.Empty;

    /// <summary>Reads a colour or vector. SL writes these as an LLSD array; the alpha of a
    /// four-component colour is dropped because none of these quantities has one — SL stores
    /// <c>LLColor4</c> and reads back <c>LLColor3</c> throughout
    /// (<c>LLColor3(settings[SETTING_BLUE_HORIZON])</c>).</summary>
    private static Vector3 ToVector3(OSD osd, Vector3 fallback)
    {
        if (osd is OSDArray a && a.Count >= 3)
            return new Vector3((float)a[0].AsReal(), (float)a[1].AsReal(), (float)a[2].AsReal());

        // Some grids write a scalar where a colour is expected; smearing it across the channels is
        // what the viewer's own smear() does and beats discarding the value.
        if (osd.Type is OSDType.Real or OSDType.Integer) return new Vector3((float)osd.AsReal());

        return fallback;
    }

    private static Vector2 ToVector2(OSD osd, Vector2 fallback)
        => osd is OSDArray a && a.Count >= 2
            ? new Vector2((float)a[0].AsReal(), (float)a[1].AsReal())
            : fallback;

    /// <summary>Reads a rotation. SL writes quaternions as a 4-element array in x, y, z, w
    /// order.</summary>
    private static Quaternion Rotation(OSDMap map, string key, Quaternion fallback)
    {
        if (!map.ContainsKey(key) || map[key] is not OSDArray a || a.Count < 4) return fallback;

        return new Quaternion(
            (float)a[0].AsReal(), (float)a[1].AsReal(), (float)a[2].AsReal(), (float)a[3].AsReal());
    }
}
