using System.Numerics;
using System.Xml.Linq;

namespace SLNG.Assets;

/// <summary>One <c>&lt;volume_morph&gt;</c> of a visual param: how much a unit of the param's weight scales and moves a
/// collision-volume joint (<c>LLPolyVolumeMorphInfo</c>, llpolymorph.cpp:295-308).</summary>
public readonly record struct VolumeMorph(string Volume, Vector3 Scale, Vector3 Position);

/// <summary>
/// BUG-AVATAR-15: the shape's collision-volume morphs, from <c>avatar_lad.xml</c>. The collision volumes (LEFT_PEC,
/// RIGHT_PEC, BELLY, BUTT, PELVIS, ...) are the joints fitted mesh bodies and clothes are rigged to, and the shape
/// sliders (Breast Size, Gravity, Cleavage, Butt Size, Body Fat, ...) resize them through <c>volume_morph</c> entries
/// that the viewer applies in <c>LLPolyMorphTarget::apply</c> (llpolymorph.cpp:642-650). LibreMetaverse's generated
/// <c>VisualParams</c> carries none of it (only the skeletal distortions), so it is read from the XML the client
/// already ships, once, like the per-param sex tags.
/// </summary>
public static class VolumeMorphTable
{
    private static Dictionary<int, VolumeMorph[]>? _cached;

    /// <summary>Param id -> its volume morphs; only params that have any. Cached after the first successful parse;
    /// a null or wrong directory returns an empty table without caching it.</summary>
    public static IReadOnlyDictionary<int, VolumeMorph[]> Load(string? charDir)
    {
        if (_cached != null) return _cached;
        if (charDir == null) return new Dictionary<int, VolumeMorph[]>();
        try
        {
            string path = Path.Combine(charDir, "avatar_lad.xml");
            if (!File.Exists(path)) return new Dictionary<int, VolumeMorph[]>();
            return _cached = Parse(XDocument.Load(path));
        }
        catch
        {
            return new Dictionary<int, VolumeMorph[]>();
        }
    }

    public static Dictionary<int, VolumeMorph[]> Parse(XDocument doc)
    {
        var table = new Dictionary<int, VolumeMorph[]>();
        foreach (var param in doc.Descendants("param"))
        {
            if (!int.TryParse((string?)param.Attribute("id"), out int id)) continue;
            var morphs = param.Elements("param_morph").Elements("volume_morph")
                .Select(e => new { Name = (string?)e.Attribute("name"), Element = e })
                .Where(x => !string.IsNullOrEmpty(x.Name))
                .Select(x => new VolumeMorph(x.Name!, Vector((string?)x.Element.Attribute("scale")), Vector((string?)x.Element.Attribute("pos"))))
                .ToArray();
            if (morphs.Length > 0) table[id] = morphs;
        }
        return table;
    }

    /// <summary>"x y z", the way <c>getFastAttributeVector3</c> reads it; a missing or short attribute is zero.</summary>
    private static Vector3 Vector(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Vector3.Zero;
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        float F(int i) => i < parts.Length && float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;
        return new Vector3(F(0), F(1), F(2));
    }
}
