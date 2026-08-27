using LibreMetaverse.StructuredData;
using SLNG.Core;

namespace SLNG.Net;

/// <summary>Parses a legacy Windlight preset file — one of the <c>.xml</c> settings the viewer
/// ships in <c>app_settings/windlight/skies</c> and <c>.../water</c>, and the same format users
/// have traded between viewers since 2009 — into SLNG's engine-neutral environment model
/// (FEAT-ENV-02).
///
/// This is the layer boundary for preset FILES, the way <see cref="EnvironmentLlsdParser"/> is the
/// boundary for what the SIMULATOR sends: <c>OSD</c> is a LibreMetaverse type and must not appear
/// above <c>SLNG.Net</c> (AGENTS.md), so the app hands in text and gets back <c>SLNG.Core</c>
/// records.
///
/// A preset file is exactly the payload the legacy <c>EnvironmentSettings</c> capability carries,
/// so it reuses the same parse path with <c>legacyWindlight: true</c> — which is what applies the
/// conversions that separate the two encodings: array-wrapped scalars, the +10 cloud-scroll
/// offset, star brightness ×250, and <c>sun_angle</c>/<c>east_angle</c> instead of a sun
/// quaternion.</summary>
public static class WindlightPresetParser
{
    /// <summary>Parses a legacy sky preset. Returns null when the text is not LLSD at all;
    /// a preset that merely omits keys is fine and inherits the viewer's defaults.</summary>
    public static SkySettings? ParseSky(string llsdXml)
        => Deserialize(llsdXml) is { } map ? EnvironmentLlsdParser.ParseSky(map, legacyWindlight: true) : null;

    /// <summary>Parses a legacy water preset. Returns null when the text is not LLSD.</summary>
    public static WaterSettings? ParseWater(string llsdXml)
        => Deserialize(llsdXml) is { } map ? EnvironmentLlsdParser.ParseWater(map) : null;

    private static OSDMap? Deserialize(string llsdXml)
    {
        if (string.IsNullOrWhiteSpace(llsdXml)) return null;

        try
        {
            return OSDParser.DeserializeLLSDXml(llsdXml) as OSDMap;
        }
        catch (Exception)
        {
            // A malformed or truncated preset must cost one entry in the picker, not the app.
            return null;
        }
    }
}
