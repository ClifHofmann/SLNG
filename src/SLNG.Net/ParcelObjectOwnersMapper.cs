using LibreMetaverse;
using LibreMetaverse.Packets;
using SLNG.Core;

namespace SLNG.Net;

/// <summary>One row of a <c>ParcelObjectOwnersReply</c> before it is cleaned up: the same fields whether the reply
/// came as a UDP packet or as an EventQueue message (FEAT-LAND-03).</summary>
internal readonly record struct OwnerRow(Guid OwnerId, bool IsGroupOwned, int Count, bool Online, DateTime? Newest);

/// <summary>Pure decoding of <c>ParcelObjectOwnersReply</c> into <see cref="ParcelObjectOwners"/> (FEAT-LAND-03).
/// Nothing here touches the network or a session, so it is unit-tested directly. Internal: LibreMetaverse types
/// appear in the signatures but never cross <c>SLNG.Net</c>'s public boundary.
///
/// <para><b>The wire.</b> <c>message_template.msg</c>:1192-1204: one <c>Data</c> block, <c>Variable</c>, per owner:
/// <c>OwnerID</c> LLUUID, <c>IsGroupOwned</c> BOOL, <c>Count</c> S32, <c>OnlineStatus</c> BOOL. Linden Lab's
/// simulators send it through the EventQueue (the template marks the UDP form <c>UDPDeprecated</c> and
/// <c>message.xml</c>:405 gives it an LLSD flavour) with an extra <c>DataExtended</c> block carrying a
/// <c>TimeStamp</c> per row; OpenSim sends the plain UDP packet. LibreMetaverse decodes only the EventQueue form
/// into an event, so <c>GridSession</c> also listens for the raw packet.</para></summary>
internal static class ParcelObjectOwnersMapper
{
    /// <summary>The rows of a UDP reply. A packet with no <c>Data</c> block yields none.</summary>
    internal static IReadOnlyList<OwnerRow> RowsFrom(ParcelObjectOwnersReplyPacket packet)
    {
        var data = packet.Data;
        if (data == null) return Array.Empty<OwnerRow>();

        var rows = new List<OwnerRow>(data.Length);
        foreach (var b in data)
            rows.Add(new OwnerRow(b.OwnerID.Guid, b.IsGroupOwned, b.Count, b.OnlineStatus, null));
        return rows;
    }

    /// <summary>The rows of an EventQueue reply, as LibreMetaverse raised them.</summary>
    internal static IReadOnlyList<OwnerRow> RowsFrom(IEnumerable<ParcelManager.ParcelPrimOwners>? owners)
    {
        var rows = new List<OwnerRow>();
        if (owners == null) return rows;
        foreach (var o in owners)
            rows.Add(new OwnerRow(o.OwnerID.Guid, o.IsGroupOwned, o.Count, o.OnlineStatus, NewestUtc(o.NewestPrim)));
        return rows;
    }

    /// <summary>A zero timestamp is "none sent" (LibreMetaverse leaves the field at its default when the
    /// <c>DataExtended</c> block is absent, and the viewer prints a date from 0 only because it has no better
    /// idea): null, not 1970.</summary>
    internal static DateTime? NewestUtc(DateTime stamp)
    {
        if (stamp <= DateTime.UnixEpoch) return null;
        return stamp.Kind switch
        {
            DateTimeKind.Local => stamp.ToUniversalTime(),
            DateTimeKind.Utc => stamp,
            _ => DateTime.SpecifyKind(stamp, DateTimeKind.Utc), // LibreMetaverse's Unix-seconds dates are UTC
        };
    }

    /// <summary>Cleans one reply's rows. A row with a nil owner is dropped, as the viewer does
    /// (<c>llfloaterland.cpp</c>:1636), but remembered: it is how Second Life answers an agent who may not see
    /// the list (<c>withheld</c>). A negative count, which a sim should never send, becomes 0. The same owner twice
    /// in one reply keeps the last row.</summary>
    internal static (List<ParcelObjectOwner> Owners, bool Withheld) Decode(IEnumerable<OwnerRow> rows)
    {
        var owners = new List<ParcelObjectOwner>();
        var index = new Dictionary<Guid, int>();
        bool withheld = false;

        foreach (var r in rows)
        {
            if (r.OwnerId == Guid.Empty)
            {
                withheld = true;
                continue;
            }

            var owner = new ParcelObjectOwner(r.OwnerId, r.IsGroupOwned, Math.Max(0, r.Count), r.Online, r.Newest);
            if (index.TryGetValue(r.OwnerId, out int at)) owners[at] = owner;
            else
            {
                index[r.OwnerId] = owners.Count;
                owners.Add(owner);
            }
        }
        return (owners, withheld);
    }

    /// <summary>Builds the record to raise for one more reply to the request for (<paramref name="regionHandle"/>,
    /// <paramref name="localId"/>). <paramref name="replace"/> is the first reply after a request: it starts the
    /// list over (the viewer's <c>mFirstReply</c>). A later reply is appended to <paramref name="held"/>, an owner
    /// who is already in it keeps his place and takes the newer row, so a reply that arrives twice (a grid that
    /// sends both forms, a retransmission) changes nothing.</summary>
    internal static ParcelObjectOwners Merge(
        ParcelObjectOwners? held, ulong regionHandle, int localId, IEnumerable<OwnerRow> rows, bool replace)
    {
        var (fresh, withheld) = Decode(rows);

        if (replace || held == null)
        {
            return new ParcelObjectOwners
            {
                RegionHandle = regionHandle,
                LocalId = localId,
                Owners = fresh,
                OwnersWithheld = withheld,
            };
        }

        var all = new List<ParcelObjectOwner>(held.Owners);
        var index = new Dictionary<Guid, int>();
        for (int i = 0; i < all.Count; i++) index[all[i].OwnerId] = i;
        foreach (var o in fresh)
        {
            if (index.TryGetValue(o.OwnerId, out int at)) all[at] = o;
            else
            {
                index[o.OwnerId] = all.Count;
                all.Add(o);
            }
        }

        return held with { Owners = all, OwnersWithheld = held.OwnersWithheld || withheld };
    }
}
