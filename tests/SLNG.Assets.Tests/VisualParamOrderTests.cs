using System.Linq;
using LibreMetaverse;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// FEAT-AVATAR-01: pins the ORDER contract of the visual-parameter byte array, which is what
/// decides whether an avatar's shape is read correctly or scrambled.
///
/// Two different orderings exist in LibreMetaverse and they are NOT interchangeable:
///
/// * <see cref="VisualParams.Group0ParamIds"/> — only the params that are actually TRANSMITTED
///   (group 0 + group 3), ascending by numeric id. This is the wire order: byte[N] of the
///   AvatarAppearance packet's visual_param block is the N-th id in this array. SLNG's
///   <c>AvatarShapeService.ComputeEffectiveWeights</c> reads the array positionally against it.
///
/// * <see cref="VisualParams.Params"/> — EVERY param, including the group-1/2 ones that are never
///   transmitted (they are derived from a driver param). Also a SortedList, so also ascending by
///   id — but a strictly larger set.
///
/// <c>AppearanceManager.MakeAppearancePacket</c> builds the outgoing array by iterating
/// <c>VisualParams.Params</c> and taking the first 218, then copies that into
/// <c>MyVisualParameters</c>. If the first 218 of <c>Params</c> are not the same ids in the same
/// order as <c>Group0ParamIds</c>, then every consumer that indexes positionally — the simulator,
/// and SLNG's own shape service — reads each byte against the WRONG parameter.
///
/// These tests do not assert which behaviour is "right"; they record what the pinned LibreMetaverse
/// version actually does, so a future upgrade cannot change it silently.
/// </summary>
public class VisualParamOrderTests
{
    /// <summary>What <c>MakeAppearancePacket</c> allocates for an avatar with no Physics layer,
    /// and the length OpenSim actually relays back (measured live 2026-08-31: "218 params").
    /// Note this is SHORTER than <see cref="VisualParams.Group0ParamIds"/> — see
    /// <see cref="Group0ParamIds_is_longer_than_the_wire_array"/>.</summary>
    private const int WireParamCount = 218;

    [Fact]
    public void Group0ParamIds_is_ascending_by_id()
    {
        var ids = VisualParams.Group0ParamIds;

        Assert.NotNull(ids);
        Assert.NotEmpty(ids);
        // Ascending numeric id — the SL viewer's std::map<S32,…> iteration order, which both its
        // encoder and decoder use, and what LibreMetaverse's Avatar.DecodeVisualParams assumes.
        Assert.Equal(ids.OrderBy(i => i).ToArray(), ids);
    }

    /// <summary>
    /// Records that the transmitted-id table is LONGER than the array actually on the wire: 253
    /// ids vs the 218 bytes MakeAppearancePacket allocates and OpenSim relays. Both LibreMetaverse's
    /// decoder and SLNG's shape service stop at the shorter of the two, so the tail ids are simply
    /// never assigned — which is fine ONLY as long as the packet's bytes line up with the FRONT of
    /// this table. Pinned so a LibreMetaverse upgrade that changes either number is noticed.
    /// </summary>
    [Fact]
    public void Group0ParamIds_is_longer_than_the_wire_array()
    {
        Assert.True(VisualParams.Group0ParamIds.Length > WireParamCount,
            $"expected the id table to exceed the {WireParamCount}-byte wire array, " +
            $"got {VisualParams.Group0ParamIds.Length}");
    }

    [Fact]
    public void Params_is_a_strict_superset_of_the_transmitted_ids()
    {
        var transmitted = VisualParams.Group0ParamIds.ToHashSet();
        var all = VisualParams.Params.Keys.ToList();

        // Every transmitted id must exist in the full table…
        Assert.All(transmitted, id => Assert.Contains(id, VisualParams.Params.Keys));
        // …and the full table must be strictly larger (the derived group-1/2 params).
        Assert.True(all.Count > transmitted.Count,
            $"expected more total params than transmitted ones, got {all.Count} vs {transmitted.Count}");
    }

    /// <summary>
    /// THE BUG. <c>MakeAppearancePacket</c> fills the 218 wire slots from the first 218 entries of
    /// <c>VisualParams.Params</c> — but those are the first 218 of ALL params, not of the
    /// transmitted ones. This test records how far the two sequences actually diverge.
    ///
    /// A non-empty divergence means <c>AppearanceManager.MyVisualParameters</c> is NOT in
    /// Group0ParamIds order, and therefore must never be handed to
    /// <c>AvatarShapeService.ComputeEffectiveWeights</c> (or trusted as a shape) — it would assign
    /// each byte to the wrong parameter, which is what a torn/exploded avatar looks like.
    /// </summary>
    [Fact]
    public void First218_of_Params_diverges_from_the_transmitted_order()
    {
        var wire = VisualParams.Group0ParamIds;
        var makePacketOrder = VisualParams.Params.Keys.Take(WireParamCount).ToArray();

        int firstMismatch = -1;
        for (int i = 0; i < WireParamCount; i++)
        {
            if (wire[i] != makePacketOrder[i]) { firstMismatch = i; break; }
        }

        Assert.True(firstMismatch >= 0,
            "The two orderings now agree — MakeAppearancePacket's output would be wire-correct. " +
            "If a LibreMetaverse upgrade caused this, revisit FEAT-AVATAR-01: feeding " +
            "MyVisualParameters into the shape service may have become safe.");
    }
}
