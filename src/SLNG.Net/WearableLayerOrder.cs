namespace SLNG.Net;

/// <summary>
/// FEAT-AVATAR-01: orders same-type wearables the way Second Life stacks them, which is not the
/// order LibreMetaverse hands them over in.
///
/// <para><b>Why this matters.</b> Several wearables of one type stack as layers, and where they
/// overlap the topmost wins. Measured in-world 2026-08-31: five worn Tattoo layers, two of them
/// <i>fully opaque</i> head skins. Which face the avatar ends up with is decided purely by this
/// order — and LibreMetaverse's <c>GetWearables()</c> put the one Firestorm shows at the bottom, so
/// the bake came out as a mix of two different faces.</para>
///
/// <para><b>The rule</b>, from <c>llappearancemgr.cpp</c>. The viewer keeps the layer position in
/// the Current Outfit Folder <i>link's description</i>:
/// <code>
/// char ORDER_NUMBER_SEPARATOR('@');
/// order_num &lt;&lt; ORDER_NUMBER_SEPARATOR &lt;&lt; type * 100 + i;   // build_order_string
/// </code>
/// so a Tattoo (type 20) carries "@2000", "@2001", … <c>WearablesOrderComparator</c> then sorts by
/// that string, requiring it to be exactly as long as <c>build_order_string(type, 0)</c> and to
/// start with '@'; entries failing that sink below the valid ones and are ordered by name.</para>
///
/// <para>Index 0 is the bottom layer: <c>LLTexLayerTemplate::render</c> draws the wearable cache
/// front to back, and <c>gatherAlphaMasks</c> spells it out — <c>U32 i = num_wearables - 1; // For
/// rendering morph masks, we only want to use the top wearable</c>. So this order is fed to the
/// baker as-is, last on top.</para>
/// </summary>
internal static class WearableLayerOrder
{
    private const char Separator = '@';

    /// <summary>The description the viewer would write for layer <paramref name="index"/> of
    /// <paramref name="wearableType"/> — <c>build_order_string</c>.</summary>
    internal static string BuildOrderString(int wearableType, int index)
        => $"{Separator}{wearableType * 100 + index}";

    /// <summary>True when a description is a usable ordering token for this wearable type: it must
    /// start with '@' and be exactly the length the type's own token has. The length check is what
    /// stops a Tattoo's "@2000" from being compared against, say, a three-digit token belonging to
    /// a different type.</summary>
    internal static bool IsValidOrderString(string? description, int wearableType)
        => description is { Length: > 0 }
           && description.Length == BuildOrderString(wearableType, 0).Length
           && description[0] == Separator;

    /// <summary>Orders one wearable type's layers bottom-first, exactly as
    /// <c>WearablesOrderComparator</c> does: valid ordering tokens first and sorted by ordinal
    /// string comparison, then everything else by name.
    ///
    /// <para>Ordinal, not culture-aware — the viewer compares <c>std::string</c>s, and a
    /// locale-sensitive comparison would reorder these differently on some machines.</para></summary>
    internal static IEnumerable<T> Sort<T>(
        IEnumerable<T> items, int wearableType, Func<T, string?> description, Func<T, string> name)
    {
        return items
            .Select(item => (Item: item, Desc: description(item), Name: name(item)))
            .OrderBy(e => IsValidOrderString(e.Desc, wearableType) ? 0 : 1)
            .ThenBy(e => IsValidOrderString(e.Desc, wearableType) ? e.Desc : string.Empty, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => e.Item);
    }
}
