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
    /// THE BUG, with its measured severity. <c>MakeAppearancePacket</c> fills the 218 wire slots
    /// from the first 218 entries of <c>VisualParams.Params</c> — but those are the first 218 of
    /// ALL 672 params, not of the 253 transmitted ones. As soon as a never-transmitted group-1/2
    /// param sorts in among them, every subsequent slot shifts.
    ///
    /// Measured against the pinned LibreMetaverse 3.1.3: the two sequences agree for the first 23
    /// slots and diverge from index 23 onward — <b>195 of 218 values end up on the wrong
    /// parameter</b>. That is why every attempt to drive an appearance send through
    /// <c>AppearanceManager</c> corrupted the stored shape (2026-08-02 deformed, 2026-08-29 flat,
    /// 2026-08-31 torn rigged head), and why <c>Settings.Agent.SendAppearance</c> must stay off.
    ///
    /// It equally means <c>AppearanceManager.MyVisualParameters</c> must never be handed to
    /// <c>AvatarShapeService.ComputeEffectiveWeights</c> or trusted as a shape — see
    /// <c>GridSession.OnAppearanceSet</c>.
    ///
    /// If a future LibreMetaverse upgrade makes this test fail, the bug was fixed upstream and
    /// FEAT-AVATAR-01 can be reopened.
    /// </summary>
    [Fact]
    public void MakeAppearancePacket_order_disagrees_with_the_wire_order()
    {
        var wire = VisualParams.Group0ParamIds;
        var makePacketOrder = VisualParams.Params.Keys.Take(WireParamCount).ToArray();

        int firstMismatch = -1;
        int agreeing = 0;
        for (int i = 0; i < WireParamCount; i++)
        {
            if (wire[i] == makePacketOrder[i]) agreeing++;
            else if (firstMismatch < 0) firstMismatch = i;
        }

        Assert.True(firstMismatch >= 0,
            "The two orderings now AGREE — MakeAppearancePacket's output would be wire-correct. " +
            "If a LibreMetaverse upgrade caused this, reopen FEAT-AVATAR-01: SendAppearance and a " +
            "real wearable rebake may have become safe.");

        // Pin the measured severity so an upgrade that merely shifts it is noticed too.
        Assert.Equal(23, firstMismatch);
        Assert.Equal(23, agreeing);
    }
}
