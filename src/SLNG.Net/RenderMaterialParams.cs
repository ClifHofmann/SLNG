namespace SLNG.Net;

/// <summary>
/// BUG-RENDER-49: an object's Render Material block -- ExtraParams type 0x80,
/// <c>PARAMS_RENDER_MATERIAL</c> (llprimitive.h:110) -- the per-face glTF PBR material ids.
///
/// <para><b>Why this exists.</b> Second Life and OpenSim send a face's PBR material id HERE, not in
/// the TextureEntry. LibreMetaverse 3.1.6 steps over the block (<c>SetExtraParamsFromBytes</c> has no
/// branch for it) and fills <c>TextureEntryFace.RenderMaterialID</c> only from optional TextureEntry
/// bytes that Agni never sends, so every PBR face rendered its legacy diffuse texture instead.</para>
///
/// <para><b>Wire layout</b> (<c>LLRenderMaterialParams::pack/unpack</c>, llprimitive.cpp:2336-2362;
/// OpenSim <c>PrimitiveBaseShape.ReadRenderMaterials</c>): <c>U8 count</c>, then <c>count</c> x
/// (<c>U8 te_idx</c>, 16-byte UUID in network order). The viewer caps it at 14 entries so the block
/// stays under 255 bytes.</para>
///
/// <para><b>Lookup</b> mirrors <c>LLRenderMaterialParams::getMaterial</c> (llprimitive.cpp:2418-2429):
/// an exact match on the face index, the FIRST entry wins, nothing for an unlisted face. There is no
/// "all faces" index -- 255 is just a face no prim has.</para>
///
/// <para><b>Short payloads.</b> Only whole entries are read: a payload that promises more entries
/// than it holds yields the ones that are there, and one without even the count byte yields none.
/// Never an invented id from bytes that are not there, and never an exception in a packet
/// handler.</para>
/// </summary>
internal sealed class RenderMaterialParams : IEquatable<RenderMaterialParams>
{
    /// <summary>ExtraParamType.RenderMaterial -- <c>LLNetworkData::PARAMS_RENDER_MATERIAL</c>.</summary>
    internal const ushort ParamType = 0x80;

    /// <summary>One entry on the wire: U8 face index + 16-byte UUID.</summary>
    internal const int EntrySize = 17;

    private readonly (byte Face, Guid MaterialId)[] _entries;

    private RenderMaterialParams((byte Face, Guid MaterialId)[] entries) => _entries = entries;

    /// <summary>The entries in wire order (duplicates kept, as the viewer keeps them).</summary>
    public IReadOnlyList<(byte Face, Guid MaterialId)> Entries => _entries;

    public int Count => _entries.Length;

    /// <summary>The id the block gives <paramref name="face"/>, if it names that face at all. A listed
    /// face with a null id is a firm "no material" (true, <see cref="Guid.Empty"/>).</summary>
    public bool TryGet(int face, out Guid materialId)
    {
        foreach (var (f, id) in _entries)
        {
            if (f == face)
            {
                materialId = id;
                return true;
            }
        }
        materialId = Guid.Empty;
        return false;
    }

    /// <summary>Reads one 0x80 payload (the bytes after the entry header).</summary>
    internal static RenderMaterialParams Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1) return new RenderMaterialParams(Array.Empty<(byte, Guid)>());

        int count = Math.Min(payload[0], (payload.Length - 1) / EntrySize);
        var entries = new (byte Face, Guid MaterialId)[count];
        for (int k = 0; k < count; k++)
        {
            var entry = payload.Slice(1 + k * EntrySize, EntrySize);
            // SL UUIDs are 16 bytes in network order -- the same order LibreMetaverse's UUID(byte[],
            // int) reads, which is Guid's big-endian form.
            entries[k] = (entry[0], new Guid(entry.Slice(1, 16), bigEndian: true));
        }
        return new RenderMaterialParams(entries);
    }

    /// <summary>The viewer's <c>LLRenderMaterialParams::operator==</c> (llprimitive.cpp:2364-2387):
    /// the same number of entries, and every face this one names gets the same id from the other.</summary>
    public bool Equals(RenderMaterialParams? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (other._entries.Length != _entries.Length) return false;
        foreach (var (face, _) in _entries)
        {
            TryGet(face, out var mine);
            other.TryGet(face, out var theirs);
            if (mine != theirs) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as RenderMaterialParams);

    /// <summary>Order-independent, so it agrees with <see cref="Equals(RenderMaterialParams?)"/>.</summary>
    public override int GetHashCode()
    {
        int h = _entries.Length;
        foreach (var (face, _) in _entries)
        {
            TryGet(face, out var id);
            h ^= HashCode.Combine(face, id);
        }
        return h;
    }

    public override string ToString() =>
        string.Join(' ', _entries.Select(e => $"{e.Face}:{e.MaterialId.ToString()[..8]}"));
}
