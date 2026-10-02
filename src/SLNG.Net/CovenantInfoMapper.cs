using System.Text;
using LibreMetaverse.Packets;
using SLNG.Core;

namespace SLNG.Net;

/// <summary>Pure mapping from an <c>EstateCovenantReply</c> to the neutral <see cref="CovenantInfo"/>
/// (FEAT-LAND-05). Nothing here touches the network, so it is unit-tested directly. Internal: the
/// LibreMetaverse packet class never crosses <c>SLNG.Net</c>'s public boundary.
///
/// <para>The message (<c>message_template.msg</c>:4706-4718), one <c>Data</c> block:
/// <c>CovenantID</c> LLUUID, <c>CovenantTimestamp</c> U32, <c>EstateName</c> Variable 1,
/// <c>EstateOwnerID</c> LLUUID. Read by <c>process_covenant_reply</c>
/// (<c>llviewermessage.cpp</c>:6782).</para></summary>
internal static class CovenantInfoMapper
{
    internal static CovenantInfo From(EstateCovenantReplyPacket packet, ulong regionHandle) =>
        From(
            regionHandle,
            packet.Data.CovenantID.Guid,
            packet.Data.CovenantTimestamp,
            packet.Data.EstateName,
            packet.Data.EstateOwnerID.Guid);

    /// <summary>Builds the header record. A nil <paramref name="covenantId"/> is "no covenant set"
    /// (the viewer then prints its fixed no-covenant sentence and fetches nothing,
    /// <c>llviewermessage.cpp</c>:6836-6870); otherwise the text is still to come. A
    /// <paramref name="timestamp"/> of 0 is "never" (<c>llviewermessage.cpp</c>:6813), not 1970.</summary>
    internal static CovenantInfo From(
        ulong regionHandle, Guid covenantId, uint timestamp, byte[]? estateName, Guid estateOwnerId)
    {
        bool hasCovenant = covenantId != Guid.Empty;
        return new CovenantInfo
        {
            RegionHandle = regionHandle,
            EstateName = DecodeName(estateName),
            EstateOwnerId = estateOwnerId,
            CovenantId = hasCovenant ? covenantId : null,
            TimestampUtc = timestamp == 0 ? null : DateTime.UnixEpoch.AddSeconds(timestamp),
            TextState = hasCovenant ? CovenantTextState.Loading : CovenantTextState.None,
            Text = null,
        };
    }

    // A "Variable 1" string carries its terminating NUL on the wire.
    private static string DecodeName(byte[]? bytes) =>
        bytes == null ? string.Empty : Encoding.UTF8.GetString(bytes).TrimEnd('\0');
}
