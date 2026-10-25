using System;
using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>One worn rigged item as <see cref="WornAlphaOrder"/> reads it.</summary>
/// <param name="WearsBake">One of its faces shows a Bakes-on-Mesh channel (a mesh body or head).</param>
/// <param name="AttachmentPoint">The SL attachment point its linkset is worn on (a link child reports its
/// root's); 0 for the parts of an animated object, which are not worn.</param>
/// <param name="RootLocalId">The local id of its linkset's root.</param>
/// <param name="LocalId">Its own local id.</param>
public readonly record struct WornAlphaItem(bool WearsBake, byte AttachmentPoint, uint RootLocalId, uint LocalId);

/// <summary>
/// BUG-RENDER-50: the order in which one avatar's worn rigged alpha is drawn, and the Godot sorting
/// offsets that produce it.
///
/// <para><b>The viewer's order.</b> Rigged alpha is not depth-sorted at all. It is drawn in a first pass
/// of its own (lldrawpoolalpha.cpp:203-205), ordered by <c>CompareRenderOrder</c>
/// (llspatialpartition.h:240-250): avatar, then <c>mRenderOrder</c> DESCENDING. <c>mRenderOrder</c> is
/// handed out by walking <c>mAttachmentPoints</c> -- a <c>std::map&lt;S32, ...&gt;</c>, so ascending
/// point id (llvoavatar.h:1046) -- and each point's attached objects in attach order
/// (llvoavatar.cpp:3046-3112). Descending therefore means: the HIGHEST attachment point first, and
/// within a point the most recently attached first. pipeline.cpp:3689 and :3735 collect and sort the
/// groups. A link child is drawn with its root (one spatial group per worn linkset).</para>
///
/// <para><b>What SLNG keeps of its own.</b> A mesh that wears a bake (skin: body, head) goes first, as
/// before (BUG-RENDER-38): the innermost layer. Attach order is not known here, so the linkset root's
/// local id stands in for it (a later rez gets a higher id), descending like the viewer's.</para>
///
/// <para><b>The offsets.</b> Every worn rigged instance of an avatar sits at the skeleton with the same
/// box, so Godot's transparent sort sees one depth for all of them and orders them by
/// <c>SortingOffset</c> alone (a larger offset draws later). Rank r owns the slot
/// [r·<see cref="RankStep"/>, (r+1)·<see cref="RankStep"/>): its blended surfaces at
/// <see cref="SurfaceOffset"/> (one <see cref="SurfaceStep"/> per face, SL's eight faces at most), its
/// alpha depth pass at <see cref="DepthOffset"/>, after every surface of its own and before the next
/// item. Ranks stop at <see cref="MaxRank"/>, so the whole avatar stays inside the 5.6 cm the earlier
/// tie-break used: far below any distance at which two avatars' order is visible.</para>
/// </summary>
public static class WornAlphaOrder
{
    /// <summary>One item's slot, 0.1 mm.</summary>
    public const float RankStep = 1e-4f;

    /// <summary>One blended surface within an item's slot, 10 µm.</summary>
    public const float SurfaceStep = 1e-5f;

    /// <summary>Surface slots per item: an SL object has eight faces at most.</summary>
    public const int SurfaceSlots = 8;

    /// <summary>Where an item's depth pass sits within its slot: after all eight surface slots.</summary>
    public const float DepthSlot = (SurfaceSlots + 1) * SurfaceStep;

    /// <summary>The last rank that gets a slot of its own; every later item shares it.</summary>
    public const int MaxRank = 559;

    /// <summary>Negative when <paramref name="a"/> draws before <paramref name="b"/>.</summary>
    public static int Compare(in WornAlphaItem a, in WornAlphaItem b)
    {
        if (a.WearsBake != b.WearsBake) return a.WearsBake ? -1 : 1;
        if (a.AttachmentPoint != b.AttachmentPoint) return b.AttachmentPoint.CompareTo(a.AttachmentPoint);
        if (a.RootLocalId != b.RootLocalId) return b.RootLocalId.CompareTo(a.RootLocalId);
        return b.LocalId.CompareTo(a.LocalId);
    }

    /// <summary>The draw rank of each item (0 draws first), in input order. Equal items keep their input
    /// order; ranks past <see cref="MaxRank"/> are clamped to it.</summary>
    public static int[] Rank(IReadOnlyList<WornAlphaItem> items)
    {
        var order = new int[items.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (x, y) =>
        {
            int c = Compare(items[x], items[y]);
            return c != 0 ? c : x.CompareTo(y);
        });
        var ranks = new int[items.Count];
        for (int position = 0; position < order.Length; position++)
            ranks[order[position]] = Math.Min(position, MaxRank);
        return ranks;
    }

    /// <summary>The start of rank <paramref name="rank"/>'s slot.</summary>
    public static float BaseOffset(int rank) => Math.Clamp(rank, 0, MaxRank) * RankStep;

    /// <summary>A blended surface of the item at <paramref name="baseOffset"/>, in face order.</summary>
    public static float SurfaceOffset(float baseOffset, int surface) =>
        baseOffset + Math.Clamp(surface + 1, 1, SurfaceSlots) * SurfaceStep;

    /// <summary>The item's alpha depth pass: after every one of its surfaces, before the next item.</summary>
    public static float DepthOffset(float baseOffset) => baseOffset + DepthSlot;
}
