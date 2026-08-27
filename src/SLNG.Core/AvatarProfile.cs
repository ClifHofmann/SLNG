namespace SLNG.Core;

/// <summary>
/// Engine- and protocol-neutral avatar profile data (FEAT-UI-13), produced by
/// <c>SLNG.Net.GridSession</c> from LibreMetaverse's <c>AvatarManager</c> replies. No
/// LibreMetaverse type crosses that boundary (AGENTS.md layering rule) — the raw
/// <c>Avatar.AvatarProperties</c> / <c>Avatar.Interests</c> / <c>AvatarGroup</c> structs are
/// converted here at the seam.
/// </summary>
/// <param name="AgentId">The avatar this profile belongs to.</param>
/// <param name="AboutText">The "2nd Life" free-text bio.</param>
/// <param name="FirstLifeText">The "1st Life" free-text bio.</param>
/// <param name="ProfileImageId">Texture id of the 2nd Life profile picture, or <see cref="Guid.Empty"/>.</param>
/// <param name="FirstLifeImageId">Texture id of the 1st Life picture, or <see cref="Guid.Empty"/>.</param>
/// <param name="PartnerId">The partner's agent id, or <see cref="Guid.Empty"/> if unpartnered.
/// The name still needs resolving via <c>GridSession.RequestAvatarName</c>/<c>NameResolved</c>.</param>
/// <param name="BornOn">Account creation date, as the server's own display string (e.g. "2007-05-14").</param>
/// <param name="CharterMember">Account tier / charter-member string as the server sends it
/// ("Resident", "Charter Member", …). Empty for most OpenSim grids.</param>
/// <param name="ProfileUrl">Optional web URL the resident set on their profile.</param>
/// <param name="AllowPublish">Resident opted the profile into web search.</param>
/// <param name="MaturePublish">Resident flagged the profile as mature.</param>
public record AvatarProfileProperties(
    Guid AgentId,
    string AboutText,
    string FirstLifeText,
    Guid ProfileImageId,
    Guid FirstLifeImageId,
    Guid PartnerId,
    string BornOn,
    string CharterMember,
    string ProfileUrl,
    bool AllowPublish,
    bool MaturePublish);

/// <summary>The "Interests" fields of a 2nd Life profile. All free text; the skill / want-to
/// bitmasks the wire also carries are viewer-defined label lists we don't model.</summary>
public record AvatarProfileInterests(
    Guid AgentId,
    string LanguagesText,
    string SkillsText,
    string WantToText);

/// <summary>One group listed on an avatar's profile.</summary>
/// <param name="InsigniaId">Group insignia texture id, or <see cref="Guid.Empty"/>.</param>
public record AvatarProfileGroup(Guid GroupId, string Name, Guid InsigniaId);

/// <summary>One of an avatar's profile "Picks" (favourite places) — the list-level summary.
/// The image, description and location come from a separate per-pick request
/// (<c>GridSession.RequestAvatarPickInfo</c> → <see cref="AvatarPickDetailEvent"/>).</summary>
public record AvatarPickInfo(Guid PickId, string Name);

/// <summary>The full detail of one Pick, from <c>PickInfoReply</c>. <paramref name="SimName"/>
/// plus the global position drive "Teleport"; global coordinates are kept as doubles because a
/// grid can put a region well past float precision.</summary>
public record AvatarPickDetail(
    Guid PickId,
    string Name,
    string Description,
    Guid SnapshotId,
    string SimName,
    double GlobalX,
    double GlobalY,
    double GlobalZ);

/// <summary>One of an avatar's profile "Classifieds" (paid ads). Summary only, same as
/// <see cref="AvatarPickInfo"/>.</summary>
public record AvatarClassifiedInfo(Guid ClassifiedId, string Name);

/// <summary>Outcome of a "Detach" on one inventory item.
///
/// The two failure-looking cases are deliberately distinguishable, because they used to be
/// indistinguishable on screen: an item can be listed as worn purely because a link to it
/// survives in the Current Outfit Folder while nothing is actually attached (a stale COF link,
/// e.g. after a crash or a failed attach). <c>DetachAttachmentIntoInv</c> is matched server-side
/// against live attachments, so for such an item it is a silent no-op — the classic "I click
/// Detach and nothing happens".</summary>
/// <param name="WasAttached">The item really was attached; a detach packet was sent for it.</param>
/// <param name="StaleLinksRemoved">Current-Outfit links moved to Trash because nothing was
/// actually attached.</param>
public record DetachResult(bool WasAttached, int StaleLinksRemoved);

/// <summary>2nd Life / 1st Life profile pages arrived. Identity data, not world-simulation
/// state, so intentionally not an <see cref="IWorldEvent"/> — UI subscribes on GridSession.</summary>
public record AvatarPropertiesEvent(AvatarProfileProperties Properties);

/// <summary>Profile "Interests" fields arrived.</summary>
public record AvatarInterestsEvent(AvatarProfileInterests Interests);

/// <summary>The avatar's listed groups arrived.</summary>
public record AvatarGroupsEvent(Guid AgentId, IReadOnlyList<AvatarProfileGroup> Groups);

/// <summary>The avatar's profile Picks list arrived.</summary>
public record AvatarPicksEvent(Guid AgentId, IReadOnlyList<AvatarPickInfo> Picks);

/// <summary>One Pick's full detail arrived (after <c>RequestAvatarPickInfo</c>).</summary>
public record AvatarPickDetailEvent(AvatarPickDetail Pick);

/// <summary>The avatar's profile Classifieds list arrived.</summary>
public record AvatarClassifiedsEvent(Guid AgentId, IReadOnlyList<AvatarClassifiedInfo> Classifieds);
