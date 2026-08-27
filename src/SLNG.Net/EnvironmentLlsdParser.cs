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
    /// <param name="legacyWindlight">True when this document came from the legacy
    /// <c>EnvironmentSettings</c> capability. Only the caller knows which capability answered, and
    /// a couple of fields are encoded differently — see <see cref="ParseSky"/>.</param>
    public static DayCycle ParseDayCycle(OSD? llsd, int dayLengthSeconds, int dayOffsetSeconds, bool legacyWindlight = false)
    {
        if (llsd is not OSDMap map) return DayCycle.Default;

        string type = map.ContainsKey(KeyType) ? map[KeyType].AsString() : string.Empty;

        if (type == "sky")
        {
            return new DayCycle(
                new[] { new DayCycleFrame<SkySettings>(0f, ParseSky(map, legacyWindlight)) },
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
        var skyFrames = ParseTrack(tracks, 1, frames, m => ParseSky(m, legacyWindlight), SkySettings.Default);

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
    /// <param name="legacyWindlight">True when the document came from the old
    /// <c>EnvironmentSettings</c> capability rather than <c>ExtEnvironment</c>. A few fields are
    /// encoded differently there and the viewer converts them on load
    /// (<c>translateLegacySettings</c>) rather than at the point of use, so the flag has to reach
    /// the parser. Note this is NOT the same question as whether the haze block is nested — real
    /// EEP documents nest their haze under <c>legacy_haze</c> too, so that is no discriminator.</param>
    public static SkySettings ParseSky(OSDMap map, bool legacyWindlight = false)
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
            DomeOffset = Real(map, null, "dome_offset", d.DomeOffset),
            Gamma = Real(map, null, "gamma", d.Gamma),
            CloudShadow = Real(map, null, "cloud_shadow", d.CloudShadow),
            SunlightColor = Color(map, null, "sunlight_color", d.SunlightColor),

            SunRotation = SunRotation(map, legacyWindlight, d.SunRotation),
            MoonRotation = MoonRotation(map, legacyWindlight, d.MoonRotation),
            MoonBrightness = Real(map, null, "moon_brightness", d.MoonBrightness),
            StarBrightness = StarBrightness(map, legacyWindlight, d.StarBrightness),
            SunScale = Real(map, null, "sun_scale", d.SunScale),
            MoonScale = Real(map, null, "moon_scale", d.MoonScale),

            CloudColor = Color(map, null, "cloud_color", d.CloudColor),
            CloudPosDensity1 = Color(map, null, "cloud_pos_density1", d.CloudPosDensity1),
            CloudPosDensity2 = Color(map, null, "cloud_pos_density2", d.CloudPosDensity2),
            CloudScale = Real(map, null, "cloud_scale", d.CloudScale),
            CloudScrollRate = CloudScroll(map, legacyWindlight, d.CloudScrollRate),
            CloudVariance = Real(map, null, "cloud_variance", d.CloudVariance),
            CloudTextureId = Id(map, "cloud_id"),
            SunTextureId = Id(map, "sun_id"),
            MoonTextureId = Id(map, "moon_id"),
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
            NormalMapId = IdAlias(map, "normal_map", "normalMap"),
        };
    }

    // --- LLSD readers -----------------------------------------------------------------------
    // All of them take a default and return it on absence or on a type mismatch. LLSD is
    // schema-free and grids disagree about it; a parser that throws on a surprise would fail the
    // whole environment over one odd key.

    private static float Real(OSDMap map, OSDMap? preferred, string key, float fallback)
    {
        if (preferred != null && preferred.ContainsKey(key)) return ToReal(preferred[key], fallback);
        return map.ContainsKey(key) ? ToReal(map[key], fallback) : fallback;
    }

    private static float RealAlias(OSDMap map, string key, string legacyKey, float fallback)
    {
        if (map.ContainsKey(key)) return ToReal(map[key], fallback);
        return map.ContainsKey(legacyKey) ? ToReal(map[legacyKey], fallback) : fallback;
    }

    /// <summary>Reads a scalar, accepting the legacy Windlight encoding that wraps one in an
    /// array.
    ///
    /// A legacy setting stores every scalar as <c>[value, 0, 0, 1]</c> — the LLColor4 the old
    /// UI edited it with — while EEP stores a bare Real. The viewer's own conversion reads
    /// element 0 of each of them by hand (<c>legacy[SETTING_CLOUD_SCALE][0].asReal()</c>,
    /// llsettingssky.cpp:1017-1058, and the same in <c>translateLegacyHazeSettings</c>).
    /// <c>OSD.AsReal()</c> on an array returns 0, so reading these without unwrapping does not
    /// fail loudly — it silently renders every legacy region and every legacy preset with
    /// cloud scale, cloud shadow, gamma, max_y and the whole haze block at zero.</summary>
    private static float ToReal(OSD osd, float fallback)
    {
        if (osd is OSDArray a) return a.Count >= 1 ? (float)a[0].AsReal() : fallback;
        return (float)osd.AsReal();
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

    /// <summary>Cloud drift rate, with the legacy Windlight conversion applied when the document
    /// came from the old capability.
    ///
    /// Legacy Windlight stores this OFFSET BY +10 — a 0..20 encoding of a −10..+10 range — and
    /// pairs it with <c>enable_cloud_scroll</c>, a two-boolean array that zeroes each axis
    /// independently. The viewer applies both in <c>translateLegacySettings</c>
    /// (llsettingssky.cpp:1024-1036). An EEP document instead carries an already-signed value in
    /// −50..50 (validator, llsettingssky.cpp:750-753) and must NOT be shifted.
    ///
    /// Reading a legacy value raw leaves the rate near +10 on both axes instead of near zero,
    /// i.e. clouds racing across the sky at roughly twenty times the intended speed — and
    /// silently ignores a region that deliberately switched drift off.</summary>
    private static Vector2 CloudScroll(OSDMap map, bool legacyWindlight, Vector2 fallback)
    {
        const string key = "cloud_scroll_rate";
        if (!map.ContainsKey(key)) return fallback;

        var rate = ToVector2(map[key], fallback);
        if (!legacyWindlight) return rate;

        rate -= new Vector2(10f, 10f);

        if (map.ContainsKey("enable_cloud_scroll") &&
            map["enable_cloud_scroll"] is OSDArray enabled && enabled.Count >= 2)
        {
            if (!enabled[0].AsBoolean()) rate.X = 0f;
            if (!enabled[1].AsBoolean()) rate.Y = 0f;
        }

        return rate;
    }

    private static Vector2 Vec2Alias(OSDMap map, string key, string legacyKey, Vector2 fallback)
    {
        if (map.ContainsKey(key)) return ToVector2(map[key], fallback);
        return map.ContainsKey(legacyKey) ? ToVector2(map[legacyKey], fallback) : fallback;
    }

    private static Guid Id(OSDMap map, string key)
        => map.ContainsKey(key) ? map[key].AsUUID().Guid : Guid.Empty;

    /// <summary>An asset id under either spelling. Legacy Windlight water spells the normal map
    /// <c>normalMap</c> (llsettingswater.cpp:SETTING_LEGACY_NORMAL_MAP), EEP <c>normal_map</c>.</summary>
    private static Guid IdAlias(OSDMap map, string key, string legacyKey)
        => map.ContainsKey(key) ? map[key].AsUUID().Guid : Id(map, legacyKey);

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

    /// <summary>Sun orientation, converting the legacy Windlight sun position when the document
    /// carries one.
    ///
    /// A legacy setting has no <c>sun_rotation</c> at all: it stores <c>sun_angle</c> (altitude,
    /// radians) and <c>east_angle</c> (azimuth, radians, CLOCKWISE — hence the negation) and the
    /// viewer builds the quaternion from them on load (<c>translateLegacySettings</c>,
    /// llsettingssky.cpp:1099-1112). Without this a legacy sky renders with an identity rotation,
    /// i.e. the sun pinned on the horizon due east regardless of what the preset says.</summary>
    private static Quaternion SunRotation(OSDMap map, bool legacyWindlight, Quaternion fallback)
    {
        if (map.ContainsKey("sun_rotation")) return Rotation(map, "sun_rotation", fallback);
        if (!legacyWindlight || !HasLegacySunAngles(map)) return fallback;

        return AzimuthAltitudeToQuaternion(
            -(float)map["east_angle"].AsReal(), (float)map["sun_angle"].AsReal());
    }

    /// <summary>Moon orientation. Legacy Windlight put the moon diametrically opposite the sun
    /// (llsettingssky.cpp:1107).</summary>
    private static Quaternion MoonRotation(OSDMap map, bool legacyWindlight, Quaternion fallback)
    {
        if (map.ContainsKey("moon_rotation")) return Rotation(map, "moon_rotation", fallback);
        if (!legacyWindlight || !HasLegacySunAngles(map)) return fallback;

        return AzimuthAltitudeToQuaternion(
            -(float)map["east_angle"].AsReal() + MathF.PI, -(float)map["sun_angle"].AsReal());
    }

    private static bool HasLegacySunAngles(OSDMap map)
        => map.ContainsKey("east_angle") && map.ContainsKey("sun_angle");

    /// <summary>Star brightness, on the EEP scale.
    ///
    /// Legacy Windlight stores this in 0..2; EEP in 0..500. The viewer multiplies by 250 on
    /// conversion (llsettingssky.cpp:1063).</summary>
    private static float StarBrightness(OSDMap map, bool legacyWindlight, float fallback)
    {
        if (!map.ContainsKey("star_brightness")) return fallback;

        float value = ToReal(map["star_brightness"], fallback);
        return legacyWindlight ? value * 250f : value;
    }

    /// <summary>The viewer's <c>convert_azimuth_and_altitude_to_quat</c>
    /// (llsettingssky.cpp:48-70): the rotation that takes the +X axis onto the direction given by
    /// azimuth/altitude. Everything downstream reads the sun direction back out as
    /// <c>x_axis * rotation</c>, so this must stay the same convention.</summary>
    private static Quaternion AzimuthAltitudeToQuaternion(float azimuth, float altitude)
    {
        var dir = new Vector3(
            MathF.Cos(azimuth) * MathF.Cos(altitude),
            MathF.Sin(azimuth) * MathF.Cos(altitude),
            MathF.Sin(altitude));

        var axis = Vector3.Cross(Vector3.UnitX, dir);
        // Sun exactly on the +X axis (or exactly opposite): the cross product degenerates and
        // normalizing it would produce NaN, which would poison the sun direction for the whole
        // frame. Identity is the correct answer for the first case; +Z is an arbitrary but valid
        // axis for the second.
        if (axis.LengthSquared() < 1e-12f)
            return dir.X >= 0f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);

        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.UnitX, dir), -1f, 1f));
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
    }
}
