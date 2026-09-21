using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using LibreMetaverse;
using LibreMetaverse.Imaging;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Microsoft.Extensions.Logging;
using SLNG.Core;

namespace SLNG.Net;

// GridSession, Profiles part.
//
// Resident profiles: properties, interests, picks, classifieds, mute list and the
// profile-adjacent actions (friendship, teleport offer, pay).
//
// Split out of the single 10,042-line GridSession.cs -- a pure move, no member
// changed. The class is still one type; `partial` only spreads it over files that
// can be read, reviewed and merged independently.
public sealed partial class GridSession
{
    // ---- FEAT-UI-13: avatar profile fetch + social actions --------------------------------

    /// <summary>Kicks off the full profile fetch for one avatar. A single AvatarPropertiesRequest
    /// makes the sim send Properties + Interests + Groups; Picks and Classifieds have their own
    /// request/reply pairs. Results arrive asynchronously on <see cref="AvatarPropertiesReceived"/>
    /// and its siblings — a network-thread event, marshal before touching the UI.</summary>
    public void RequestAvatarProfile(Guid agentId)
    {
        if (agentId == Guid.Empty || !_client.Network.Connected) return;
        var id = new UUID(agentId);
        _client.Avatars.RequestAvatarProperties(id);
        _client.Avatars.RequestAvatarPicks(id);
        _client.Avatars.RequestAvatarClassified(id);
    }

    /// <summary>Requests the full detail of one Pick (image, description, location). Result on
    /// <see cref="AvatarPickDetailReceived"/>.</summary>
    public void RequestAvatarPickInfo(Guid agentId, Guid pickId)
    {
        if (agentId == Guid.Empty || pickId == Guid.Empty || !_client.Network.Connected) return;
        _client.Avatars.RequestPickInfo(new UUID(agentId), new UUID(pickId));
    }

    /// <summary>Writes the logged-in agent's own "2nd Life" / "1st Life" profile pages
    /// (<c>AvatarPropertiesUpdate</c>, or the AgentProfile CAP where the sim has one). The whole
    /// struct is sent every time, so the caller must pass the CURRENT image ids back unchanged or
    /// they get cleared — picture editing is a separate (upload/pick) feature. On grids without a
    /// profile service this is a silent no-op.</summary>
    public void UpdateOwnProfile(string aboutText, string firstLifeText, string profileUrl,
        Guid profileImageId, Guid firstLifeImageId, bool allowPublish, bool maturePublish)
    {
        if (!_client.Network.Connected) return;
        _client.Self.UpdateProfile(new Avatar.AvatarProperties
        {
            AboutText = aboutText ?? string.Empty,
            FirstLifeText = firstLifeText ?? string.Empty,
            ProfileURL = profileUrl ?? string.Empty,
            ProfileImage = new UUID(profileImageId),
            FirstLifeImage = new UUID(firstLifeImageId),
            AllowPublish = allowPublish,
            MaturePublish = maturePublish,
        });
    }

    /// <summary>Writes the logged-in agent's own profile "Interests" free-text fields
    /// (<c>AvatarInterestsUpdate</c>). The skill / want-to bitmasks (the viewer's checkbox lists)
    /// are sent as 0 — this pass edits the text only.</summary>
    public void UpdateOwnInterests(string languages, string skills, string wantTo)
    {
        if (!_client.Network.Connected) return;
        _client.Self.UpdateInterests(new Avatar.Interests
        {
            LanguagesText = languages ?? string.Empty,
            SkillsText = skills ?? string.Empty,
            WantToText = wantTo ?? string.Empty,
            SkillsMask = 0,
            WantToMask = 0,
        });
    }

    /// <summary>Sends a friendship offer to another avatar.</summary>
    public void OfferFriendship(Guid agentId)
    {
        if (agentId == Guid.Empty || !_client.Network.Connected) return;
        _client.Friends.OfferFriendship(new UUID(agentId));
    }

    /// <summary>Offers the target avatar a teleport to our current location (a "lure").</summary>
    public void OfferTeleport(Guid agentId, string message = "Join me at my location.")
    {
        if (agentId == Guid.Empty || !_client.Network.Connected) return;
        _client.Self.SendTeleportLure(new UUID(agentId), message);
    }

    /// <summary>Pays L$ to another avatar. Returns whether it actually went out.</summary>
    /// <remarks>
    /// Returns a bool rather than void for the same reason <see cref="PayObject"/> does: the
    /// caller tells the user what happened, and "I sent it" printed after a refused send is worse
    /// than nothing. Refuses a non-positive amount and anything past SL's own per-transaction
    /// ceiling -- the free-entry field is where a digit too many gets typed.
    /// </remarks>
    public bool PayAvatar(Guid agentId, int amount)
    {
        if (agentId == Guid.Empty || !_client.Network.Connected) return false;
        if (amount <= 0 || amount > SLNG.Core.PaymentCheck.MaxAmount) return false;

        _client.Self.GiveAvatarMoney(new UUID(agentId), amount);
        return true;
    }

    /// <summary>Asks the sim to (re)send the account mute list, so <see cref="IsAvatarMuted"/>
    /// reflects reality. Cheap; safe to call once after login.</summary>
    public void RequestMuteList()
    {
        if (_client.Network.Connected) _client.Self.RequestMuteList();
    }

    /// <summary>Adds or removes a local mute-list entry for an avatar (the "Block" action). The
    /// mute list is per-account server state that LibreMetaverse round-trips.</summary>
    public void SetAvatarMuted(Guid agentId, string avatarName, bool muted)
    {
        if (agentId == Guid.Empty || !_client.Network.Connected) return;
        var id = new UUID(agentId);
        if (muted)
            _client.Self.UpdateMuteListEntry(MuteType.Resident, id, avatarName ?? string.Empty);
        else
            _client.Self.RemoveMuteListEntry(id, avatarName ?? string.Empty);
    }

    /// <summary>Whether an avatar is currently on the synced mute list. Best-effort: only as
    /// current as the last <see cref="RequestMuteList"/> / mute edit.</summary>
    public bool IsAvatarMuted(Guid agentId)
    {
        var id = new UUID(agentId);
        foreach (var entry in _client.Self.MuteList.Values)
            if (entry.ID == id) return true;
        return false;
    }

    private void OnAvatarPropertiesReply(object? sender, AvatarPropertiesReplyEventArgs e)
    {
        var p = e.Properties;
        AvatarPropertiesReceived?.Invoke(this, new AvatarPropertiesEvent(new AvatarProfileProperties(
            e.AvatarID.Guid,
            p.AboutText ?? string.Empty,
            p.FirstLifeText ?? string.Empty,
            p.ProfileImage.Guid,
            p.FirstLifeImage.Guid,
            p.Partner.Guid,
            p.BornOn ?? string.Empty,
            p.CharterMember ?? string.Empty,
            p.ProfileURL ?? string.Empty,
            p.AllowPublish,
            p.MaturePublish)));
    }

    private void OnAvatarInterestsReply(object? sender, AvatarInterestsReplyEventArgs e)
    {
        var i = e.Interests;
        AvatarInterestsReceived?.Invoke(this, new AvatarInterestsEvent(new AvatarProfileInterests(
            e.AvatarID.Guid,
            i.LanguagesText ?? string.Empty,
            i.SkillsText ?? string.Empty,
            i.WantToText ?? string.Empty)));
    }

    private void OnAvatarGroupsReply(object? sender, AvatarGroupsReplyEventArgs e)
    {
        var groups = new List<AvatarProfileGroup>();
        foreach (var g in e.Groups)
            groups.Add(new AvatarProfileGroup(g.GroupID.Guid, g.GroupName ?? string.Empty, g.GroupInsigniaID.Guid));
        AvatarGroupsReceived?.Invoke(this, new AvatarGroupsEvent(e.AvatarID.Guid, groups));
    }

    private void OnAvatarPicksReply(object? sender, AvatarPicksReplyEventArgs e)
    {
        var picks = new List<AvatarPickInfo>();
        foreach (var kvp in e.Picks)
            picks.Add(new AvatarPickInfo(kvp.Key.Guid, kvp.Value ?? string.Empty));
        AvatarPicksReceived?.Invoke(this, new AvatarPicksEvent(e.AvatarID.Guid, picks));
    }

    private void OnPickInfoReply(object? sender, PickInfoReplyEventArgs e)
    {
        var p = e.Pick;
        AvatarPickDetailReceived?.Invoke(this, new AvatarPickDetailEvent(new AvatarPickDetail(
            e.PickID.Guid,
            p.Name ?? string.Empty,
            p.Desc ?? string.Empty,
            p.SnapshotID.Guid,
            p.SimName ?? string.Empty,
            p.PosGlobal.X,
            p.PosGlobal.Y,
            p.PosGlobal.Z)));
    }

    private void OnAvatarClassifiedReply(object? sender, AvatarClassifiedReplyEventArgs e)
    {
        var ads = new List<AvatarClassifiedInfo>();
        foreach (var kvp in e.Classifieds)
            ads.Add(new AvatarClassifiedInfo(kvp.Key.Guid, kvp.Value ?? string.Empty));
        AvatarClassifiedsReceived?.Invoke(this, new AvatarClassifiedsEvent(e.AvatarID.Guid, ads));
    }
}
