namespace SLNG.Core;

/// <summary>One row of the Land-Info Objects tab's "Object Owners" list: who owns objects on the
/// parcel and how many (FEAT-LAND-03). Decoded from <c>ParcelObjectOwnersReply</c>
/// (<c>message_template.msg</c>:1192; read by <c>LLPanelLandObjects::processParcelObjectOwnersReply</c>,
/// <c>llfloaterland.cpp</c>:1594).</summary>
/// <param name="OwnerId">The owner's id: an avatar's, or a group's when <paramref name="IsGroupOwned"/>.
/// Never nil (the viewer drops nil rows, :1636).</param>
/// <param name="IsGroupOwned">True when the owner is a group (objects deeded to it).</param>
/// <param name="Count">The sim's "Count" for this owner (the viewer's "Count" column). Never negative.</param>
/// <param name="Online">The sim's <c>OnlineStatus</c>. Do NOT show it as a fact: Linden Lab's simulators no
/// longer fill it and OpenSim hard-codes true, so it carries no information on either grid. The viewer
/// draws it as an online/offline icon regardless.</param>
/// <param name="NewestUtc">When this owner's most recent object was rezzed ("Most Recent"), UTC. Null when
/// the sim sent none: that value rides in an extra <c>DataExtended</c> block that only Second Life sends
/// (and omits for an agent without rights or a parcel without objects).</param>
public sealed record ParcelObjectOwner(Guid OwnerId, bool IsGroupOwned, int Count, bool Online, DateTime? NewestUtc = null);
