using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SLNG.Core;

namespace SLNG.Assets;

/// <summary>
/// BUG-RENDER-49: decodes a Second Life / OpenSim glTF material asset (asset type 57, "material")
/// into a <see cref="PbrMaterialData"/>.
///
/// <para><b>Envelope.</b> The asset is LLSD, not glTF: a map <c>{ version, type, data }</c>
/// (llmaterialeditor.cpp:1305-1315 writes it, in BINARY LLSD; llgltfmateriallist.cpp:559-589 reads
/// it). <c>type</c> must be <c>"GLTF 2.0"</c> and <c>version</c> one of <c>"1.0"</c>, <c>"1.1"</c>
/// (LLGLTFMaterial::ACCEPTED_ASSET_VERSIONS, llgltfmaterial.cpp:38-40); <c>data</c> is the glTF JSON
/// as a string. The viewer's <c>LLSDSerialize::deserialize</c> accepts binary, XML and notation
/// (llsdserialize.cpp:150-240); so does this. LibreMetaverse 3.1.6's <c>AssetMaterial.Decode</c>
/// parses the bytes as bare glTF JSON and so fails every real asset -- a bare glTF document is still
/// accepted here as a last resort.</para>
///
/// <para><b>Material.</b> <c>LLGLTFMaterial::setFromModel</c> (llgltfmaterial.cpp:220-265) on
/// material 0: each texture slot's id is the <c>uri</c> of the image its texture names
/// (<c>gltf_get_texture_image</c>, llgltfmaterial_templates.h:33-53), with an optional
/// <c>KHR_texture_transform</c>; factors at their glTF defaults when absent; metallic, roughness
/// and the alpha cutoff clamped to 0..1; an alpha mode other than MASK/BLEND is OPAQUE.</para>
///
/// <para>Never throws: a malformed asset is null with the reason.</para>
/// </summary>
public static class GltfMaterialAsset
{
    public const string AssetTypeName = "GLTF 2.0";

    private static readonly string[] AcceptedVersions = { "1.0", "1.1" };

    /// <summary>Decodes one material asset. Null when it cannot be read, with <paramref name="error"/>.</summary>
    public static PbrMaterialData? Decode(ReadOnlySpan<byte> asset, out string error)
    {
        error = string.Empty;
        if (asset.Length == 0)
        {
            error = "empty asset";
            return null;
        }

        string? json;
        if (TryReadEnvelope(asset, out var envelope, out string envelopeError))
        {
            string type = envelope["type"].AsString();
            string version = envelope["version"].AsString();
            if (type != AssetTypeName)
            {
                error = $"envelope type '{type}', not '{AssetTypeName}'";
                return null;
            }
            if (Array.IndexOf(AcceptedVersions, version) < 0)
            {
                error = $"envelope version '{version}' is not accepted";
                return null;
            }
            if (envelope["data"].Kind != LlsdKind.String)
            {
                error = "envelope has no 'data' string";
                return null;
            }
            json = envelope["data"].AsString();
        }
        else if (LooksLikeJson(asset))
        {
            json = Encoding.UTF8.GetString(asset);
        }
        else
        {
            error = $"not an LLSD material envelope ({envelopeError})";
            return null;
        }

        return ParseGltf(json, out error);
    }

    // --- The LLSD envelope -------------------------------------------------------------------------

    private static bool TryReadEnvelope(ReadOnlySpan<byte> asset, out LlsdValue envelope, out string error)
    {
        envelope = LlsdValue.Undefined;
        error = string.Empty;

        if (LlsdBinary.HasHeader(asset))
            return IsEnvelope(LlsdBinary.TryParse(asset, out envelope, out error), envelope, ref error);

        int start = SkipWhitespace(asset);
        if (start < asset.Length && asset[start] == (byte)'<')
            return IsEnvelope(TryParseXml(asset, out envelope, out error), envelope, ref error);

        // Binary without its header: a map marker followed by a big-endian count.
        if (LlsdBinary.TryParse(asset, out var binary, out _) && binary.IsMap && binary["data"].IsDefined)
        {
            envelope = binary;
            return true;
        }

        var text = asset;
        var notationHeader = Encoding.ASCII.GetBytes("<? llsd/notation ?>");
        if (text.Length >= notationHeader.Length && Encoding.ASCII.GetString(text[..notationHeader.Length]).Equals("<? llsd/notation ?>", StringComparison.OrdinalIgnoreCase))
            text = text[notationHeader.Length..];
        if (LlsdNotation.TryParse(text, out var notation, out error) && notation.IsMap && notation["data"].IsDefined)
        {
            envelope = notation;
            return true;
        }
        return false;
    }

    private static bool IsEnvelope(bool parsed, LlsdValue value, ref string error)
    {
        if (!parsed) return false;
        if (!value.IsMap)
        {
            error = "envelope is not a map";
            return false;
        }
        return true;
    }

    private static int SkipWhitespace(ReadOnlySpan<byte> data)
    {
        int i = 0;
        while (i < data.Length && (data[i] == (byte)' ' || data[i] == (byte)'\t' || data[i] == (byte)'\r' || data[i] == (byte)'\n')) i++;
        return i;
    }

    private static bool LooksLikeJson(ReadOnlySpan<byte> data)
    {
        int i = SkipWhitespace(data);
        return i < data.Length && data[i] == (byte)'{' && data.IndexOf((byte)'"') > i;
    }

    /// <summary>XML LLSD, the top-level map only, string-ish values -- all an envelope holds. The
    /// viewer's <c>&lt;? llsd/xml ?&gt;</c> pseudo-header is not XML and is skipped.</summary>
    private static bool TryParseXml(ReadOnlySpan<byte> asset, out LlsdValue value, out string error)
    {
        value = LlsdValue.Undefined;
        error = string.Empty;
        try
        {
            string text = Encoding.UTF8.GetString(asset).TrimStart();
            if (text.StartsWith("<?", StringComparison.Ordinal) && !text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            {
                int end = text.IndexOf("?>", StringComparison.Ordinal);
                if (end > 0) text = text[(end + 2)..];
            }
            var root = XDocument.Parse(text).Root;
            var map = root?.Name.LocalName == "llsd" ? root.Element("map") : root?.Name.LocalName == "map" ? root : null;
            if (map == null)
            {
                error = "XML without an <llsd><map>";
                return false;
            }

            var entries = new Dictionary<string, LlsdValue>();
            string? key = null;
            foreach (var element in map.Elements())
            {
                if (element.Name.LocalName == "key")
                {
                    key = element.Value;
                    continue;
                }
                if (key == null) continue;
                entries[key] = element.Name.LocalName switch
                {
                    "string" or "uri" => LlsdValue.FromString(element.Value),
                    "integer" => LlsdValue.FromInteger(int.TryParse(element.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0),
                    "real" => LlsdValue.FromReal(double.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) ? r : 0),
                    "uuid" => LlsdValue.FromUuid(Guid.TryParse(element.Value, out var g) ? g : Guid.Empty),
                    _ => LlsdValue.Other(),
                };
                key = null;
            }
            value = LlsdValue.FromMap(entries);
            return true;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException or InvalidOperationException)
        {
            error = $"XML: {ex.Message}";
            return false;
        }
    }

    // --- The glTF document -------------------------------------------------------------------------

    private static PbrMaterialData? ParseGltf(string json, out string error)
    {
        error = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (!root.TryGetProperty("materials", out var materials) || materials.ValueKind != JsonValueKind.Array || materials.GetArrayLength() == 0)
            {
                error = "glTF without a material";
                return null;
            }
            var material = materials[0];

            Guid baseColor = Guid.Empty, metallicRoughness = Guid.Empty, normal = Guid.Empty, emissive = Guid.Empty;
            GltfTextureTransform? baseColorTransform = null;
            var baseColorFactor = Vector4.One;
            float metallic = 1f, roughness = 1f;

            if (material.TryGetProperty("pbrMetallicRoughness", out var pbr) && pbr.ValueKind == JsonValueKind.Object)
            {
                if (pbr.TryGetProperty("baseColorTexture", out var bct))
                {
                    baseColor = TextureId(root, bct);
                    baseColorTransform = Transform(bct);
                }
                if (pbr.TryGetProperty("metallicRoughnessTexture", out var mrt)) metallicRoughness = TextureId(root, mrt);
                if (pbr.TryGetProperty("baseColorFactor", out var bcf) && Floats(bcf, 4) is { } f4)
                    baseColorFactor = new Vector4(f4[0], f4[1], f4[2], f4[3]);
                metallic = Math.Clamp(Number(pbr, "metallicFactor", 1f), 0f, 1f);
                roughness = Math.Clamp(Number(pbr, "roughnessFactor", 1f), 0f, 1f);
            }
            if (material.TryGetProperty("normalTexture", out var nt)) normal = TextureId(root, nt);
            if (material.TryGetProperty("emissiveTexture", out var et)) emissive = TextureId(root, et);

            var emissiveFactor = Vector3.Zero;
            if (material.TryGetProperty("emissiveFactor", out var ef) && Floats(ef, 3) is { } f3)
                emissiveFactor = new Vector3(f3[0], f3[1], f3[2]);

            string alphaMode = material.TryGetProperty("alphaMode", out var am) && am.ValueKind == JsonValueKind.String ? am.GetString() ?? "" : "";
            var mode = alphaMode switch
            {
                "MASK" => PbrAlphaMode.Mask,
                "BLEND" => PbrAlphaMode.Blend,
                _ => PbrAlphaMode.Opaque,
            };
            float cutoff = Math.Clamp(Number(material, "alphaCutoff", 0.5f), 0f, 1f);
            bool doubleSided = material.TryGetProperty("doubleSided", out var ds) && ds.ValueKind == JsonValueKind.True;

            return new PbrMaterialData(baseColor, normal, metallicRoughness, emissive, baseColorFactor, metallic, roughness,
                emissiveFactor, mode, cutoff, doubleSided, baseColorTransform);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or IndexOutOfRangeException)
        {
            error = $"glTF JSON: {ex.Message}";
            return null;
        }
    }

    /// <summary>texture info -> textures[index].source -> images[source].uri, as a UUID.</summary>
    private static Guid TextureId(JsonElement root, JsonElement textureInfo)
    {
        if (textureInfo.ValueKind != JsonValueKind.Object || !textureInfo.TryGetProperty("index", out var indexElement)
            || !indexElement.TryGetInt32(out int index))
            return Guid.Empty;
        if (!root.TryGetProperty("textures", out var textures) || textures.ValueKind != JsonValueKind.Array
            || index < 0 || index >= textures.GetArrayLength())
            return Guid.Empty;
        if (!textures[index].TryGetProperty("source", out var sourceElement) || !sourceElement.TryGetInt32(out int source))
            return Guid.Empty;
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array
            || source < 0 || source >= images.GetArrayLength())
            return Guid.Empty;
        return images[source].TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String
               && Guid.TryParse(uri.GetString(), out var id)
            ? id
            : Guid.Empty;
    }

    private static GltfTextureTransform? Transform(JsonElement textureInfo)
    {
        if (textureInfo.ValueKind != JsonValueKind.Object
            || !textureInfo.TryGetProperty("extensions", out var extensions) || extensions.ValueKind != JsonValueKind.Object
            || !extensions.TryGetProperty("KHR_texture_transform", out var t) || t.ValueKind != JsonValueKind.Object)
            return null;
        var offset = t.TryGetProperty("offset", out var o) && Floats(o, 2) is { } fo ? new Vector2(fo[0], fo[1]) : Vector2.Zero;
        var scale = t.TryGetProperty("scale", out var s) && Floats(s, 2) is { } fs ? new Vector2(fs[0], fs[1]) : Vector2.One;
        float rotation = Number(t, "rotation", 0f);
        return new GltfTextureTransform(offset, scale, rotation);
    }

    private static float Number(JsonElement obj, string name, float fallback) =>
        obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? (float)e.GetDouble() : fallback;

    private static float[]? Floats(JsonElement array, int count)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() < count) return null;
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            if (array[i].ValueKind != JsonValueKind.Number) return null;
            values[i] = (float)array[i].GetDouble();
        }
        return values;
    }
}
