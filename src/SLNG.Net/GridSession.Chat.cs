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

// GridSession, Chat part.
//
// Local chat, instant messages, friends, groups and group chat, name resolution and
// inventory offers.
//
// Split out of the single 10,042-line GridSession.cs -- a pure move, no member
// changed. The class is still one type; `partial` only spreads it over files that
// can be read, reviewed and merged independently.
public sealed partial class GridSession
{
    private void OnChatFromSimulator(object? sender, ChatEventArgs e)
    {
        // StartTyping/StopTyping are the "..." typing indicator other viewers show next to a
        // name -- they carry no message text at all. Forwarding them here unfiltered showed up
        // as a chat log line with a timestamp and sender name but nothing after the colon, once
        // per keystroke-session per person (live-tested: reported as "irgendwie fehlen hier im
        // chat texte" against a busy multi-avatar conversation, where every blank line lined up
        // exactly with the sender starting/stopping typing right before/after a real message).
        if (e.Type == ChatType.StartTyping || e.Type == ChatType.StopTyping) return;

        // Drop truly empty chat (an object emitting "" on channel 0 as a heartbeat/clear -- e.g. a
        // worn radio), same as the Linden/Firestorm nearby-chat handler which skips on
        // mText.empty(). Strictly IsNullOrEmpty, not whitespace, so a deliberate " " separator
        // line from a script still shows.
        if (string.IsNullOrEmpty(e.Message)) return;

        ChatMessageReceived?.Invoke(this, new ChatMessageEvent(
            e.FromName,
            e.Message,
            (byte)e.Type,
            e.SourceID.Guid,
            e.SourceType == ChatSourceType.Agent));
    }

    private void OnUUIDNameReply(object? sender, UUIDNameReplyEventArgs e)
    {
        var idsToRequest = new System.Collections.Generic.List<UUID>();
        foreach (var kvp in e.Names)
        {
            var id = kvp.Key.Guid;
            _nameCache[id] = kvp.Value;
            NameResolved?.Invoke(this, new NameResolvedEvent(id, kvp.Value));
            idsToRequest.Add(kvp.Key);
        }
        if (idsToRequest.Count > 0)
        {
            try { _client.Avatars.GetDisplayNamesAsync(idsToRequest); } catch { /* Ignore if not supported/disabled */ }
        }
    }

    private void OnDisplayNameUpdate(object? sender, DisplayNameUpdateEventArgs e)
    {
        var id = e.DisplayName.ID.Guid;
        string? displayName = e.DisplayName.DisplayName;
        if (!string.IsNullOrEmpty(displayName))
        {
            DisplayNameResolved?.Invoke(this, new NameResolvedEvent(id, displayName));
        }
    }

    private void OnGroupNamesReply(object? sender, GroupNamesEventArgs e)
    {
        foreach (var kvp in e.GroupNames)
        {
            var id = kvp.Key.Guid;
            var name = string.IsNullOrEmpty(kvp.Value) ? "(unknown group)" : kvp.Value;
            _nameCache[id] = name;
            NameResolved?.Invoke(this, new NameResolvedEvent(id, name));
        }
    }

    /// <summary>The sim's urgent-message channel -- covers rejections that otherwise fail
    /// completely silently, e.g. OpenSim's SceneGraph.UpdatePrimFlags sending "Object physics
    /// cancelled because it exceeds limits for physical prims" when a Physical toggle is denied
    /// (size/linkset physics-capacity limits) instead of an ObjectFlagUpdate ever coming back.</summary>
    private void OnAlertMessage(object? sender, AlertMessageEventArgs e)
    {
        AlertMessageReceived?.Invoke(this, new AlertMessageEvent(e.Message));
    }

    /// <summary>Looks up an already-resolved user/group name from the local cache. Returns
    /// false (with the raw id's string form) if it hasn't been fetched yet -- call
    /// <see cref="RequestAvatarName"/>/<see cref="RequestGroupName"/> and wait for
    /// <see cref="NameResolved"/> in that case.</summary>
    public bool TryGetCachedName(Guid id, out string name)
    {
        if (_nameCache.TryGetValue(id, out var cached))
        {
            name = cached;
            return true;
        }
        name = id.ToString();
        return false;
    }

    public void RequestAvatarName(Guid agentId)
    {
        if (agentId == Guid.Empty || _nameCache.ContainsKey(agentId) || !_client.Network.Connected) return;
        _client.Avatars.RequestAvatarName(new UUID(agentId));
    }

    public void RequestGroupName(Guid groupId)
    {
        if (groupId == Guid.Empty || _nameCache.ContainsKey(groupId) || !_client.Network.Connected) return;
        _client.Groups.RequestGroupName(new UUID(groupId));
    }

    private void OnFriendOnline(object? sender, FriendInfoEventArgs e) =>
        FriendStatusChanged?.Invoke(this, new FriendStatusEvent(e.Friend.UUID.Guid, true));

    private void OnFriendOffline(object? sender, FriendInfoEventArgs e) =>
        FriendStatusChanged?.Invoke(this, new FriendStatusEvent(e.Friend.UUID.Guid, false));

    /// <summary>Snapshot of the logged-in agent's friends list. LibreMetaverse's FriendInfo
    /// usually already carries a resolved Name; when it doesn't, this falls back to the shared
    /// name cache (see <see cref="TryGetCachedName"/>) and kicks off a resolve via
    /// <see cref="RequestAvatarName"/> so a later call (e.g. after <see cref="NameResolved"/>
    /// fires) picks it up -- same pattern as every other UUID-keyed name in this class.</summary>
    public IReadOnlyList<FriendEntry> GetFriends()
    {
        var result = new List<FriendEntry>();
        foreach (var friend in _client.Friends.FriendList.Values)
        {
            var id = friend.UUID.Guid;
            string name = friend.Name;
            if (string.IsNullOrEmpty(name) && !TryGetCachedName(id, out name))
            {
                name = "";
                RequestAvatarName(id);
            }
            result.Add(new FriendEntry(id, name, friend.IsOnline));
        }
        return result;
    }

    // Self.IM carries every instant-message-shaped packet (friendship offers, teleport
    // requests, group notices, ...), not just plain 1:1 chat -- filter to MessageFromAgent so
    // Phase 1c's IM tabs only see actual conversation messages. The others get their own
    // dedicated flows later rather than being half-handled here.
    private void OnInstantMessage(object? sender, InstantMessageEventArgs e)
    {
        // A group invitation is its own dialog (3) and would otherwise fall through both branches
        // below and vanish -- which is exactly what "die Gruppeneinladung kam nicht an" was.
        //
        // Deliberately NOT via LibreMetaverse's own GroupManager.GroupInvitation event: that one
        // fires synchronously and then immediately sends accept-or-decline based on
        // GroupInvitationEventArgs.Accept, which defaults to FALSE (GroupManager.cs:1099-1125).
        // Subscribing to it while asking the user first would auto-DECLINE every invitation --
        // worse than not handling it at all. Leaving it unsubscribed makes that handler a no-op
        // (it early-returns when nothing is listening), so we answer on our own schedule instead.
        if (e.IM.Dialog == InstantMessageDialog.GroupInvitation)
        {
            GroupInvitationReceived?.Invoke(this, new GroupInvitationEvent(
                // llimprocessing.cpp:864 -- the group id travels in FromAgentID for an invite sent
                // by the group itself, and the reply is addressed to it (send_improved_im(group_id,
                // ..., transaction_id), llviewermessage.cpp:681). See the DTO for the aux-id gap.
                e.IM.FromAgentID.Guid,
                e.IM.IMSessionID.Guid,
                e.IM.FromAgentName ?? string.Empty,
                e.IM.Message ?? string.Empty,
                ParseGroupInvitationFee(e.IM.BinaryBucket)));
            return;
        }

        // An inventory offer is its own dialog too (4 from an avatar, 9 from an object) and would
        // otherwise fall through the MessageFromAgent guard at the bottom and vanish -- which is
        // exactly what "die Landmarke war erst nach Relog im Inventar" was (BUG-INV-04).
        //
        // Deliberately NOT via LibreMetaverse's InventoryManager.InventoryObjectOffered, for the
        // same reason GroupInvitation is handled by hand above: that event fires synchronously on
        // this thread and the very next line sends accept-or-decline from
        // InventoryObjectOfferedEventArgs.Accept, which its constructor sets to FALSE
        // (InventoryEventArgs.cs:49, InventoryManager.Handlers.cs:156-158). Subscribing while
        // asking the user first would auto-DECLINE every offer. Leaving it unsubscribed makes
        // LibreMetaverse's handler a no-op (the whole block is gated on the event being non-null),
        // so we decode the offer and answer it ourselves.
        if (e.IM.Dialog is InstantMessageDialog.InventoryOffered or InstantMessageDialog.TaskInventoryOffered)
        {
            bool fromTask = e.IM.Dialog == InstantMessageDialog.TaskInventoryOffered;
            if (!TryParseInventoryOfferBucket(e.IM.BinaryBucket, fromTask, out int assetType, out Guid itemId))
            {
                // llimprocessing.cpp:911-929 keeps showing the popup on a malformed bucket rather
                // than dropping the offer. We can't file what we can't identify, so drop it -- but
                // say so, because silence here is the bug this whole branch exists to fix.
                Console.Error.WriteLine(
                    $"[Inventory] Malformed inventory offer from {e.IM.FromAgentName} " +
                    $"(dialog {e.IM.Dialog}, bucket {e.IM.BinaryBucket?.Length ?? 0} bytes) -- dropped.");
                return;
            }

            InventoryOfferReceived?.Invoke(this, new InventoryOfferEvent(
                e.IM.IMSessionID.Guid,
                e.IM.FromAgentID.Guid,
                e.IM.FromAgentName ?? string.Empty,
                // The simulator puts the item name in the message body (llimprocessing.cpp:937
                // info->mDesc = message).
                e.IM.Message ?? string.Empty,
                itemId,
                assetType,
                fromTask));
            return;
        }

        // Group chat first, and NOT by inspecting the dialog byte: it arrives as
        // InstantMessageDialog.SessionSend, not MessageFromAgent, and its GroupIM flag is only set
        // on the first message of a session -- a later one carries just the session id. Both the
        // old `Dialog != MessageFromAgent` test and the old `|| e.IM.GroupIM` bail therefore
        // dropped group chat, twice over. LibreMetaverse's own AgentManager.IsGroupMessage is the
        // authoritative test (GroupIM || the session is a known group chat session), so use it
        // rather than re-deriving the rule here.
        if (_client.Self.IsGroupMessage(e.IM))
        {
            if (string.IsNullOrEmpty(e.IM.Message)) return; // typing/keep-alive, same as local chat
            // For group chat the session id IS the group id.
            GroupChatMessageReceived?.Invoke(this, new GroupChatMessageEvent(
                e.IM.IMSessionID.Guid, e.IM.FromAgentID.Guid, e.IM.FromAgentName, e.IM.Message));
            return;
        }

        if (e.IM.Dialog != InstantMessageDialog.MessageFromAgent) return;

        InstantMessageReceived?.Invoke(this, new InstantMessageEvent(
            e.IM.FromAgentID.Guid, e.IM.FromAgentName, e.IM.Message, e.IM.IMSessionID.Guid));
    }

    /// <summary>Sends a 1:1 instant message.</summary>
    public void SendInstantMessage(Guid targetAgentId, string message)
    {
        if (_client.Network.Connected)
            _client.Self.InstantMessage(new UUID(targetAgentId), message);
    }

    // ---- M5-3 Phase 2: groups + group chat -------------------------------------------------

    /// <summary>Asks the sim for the agent's group memberships. The answer arrives asynchronously
    /// on <see cref="GroupsUpdated"/> (a network-thread event — marshal before touching the UI);
    /// <see cref="GetGroups"/> then returns it without another round trip.</summary>
    public void RequestGroups()
    {
        if (_client.Network.Connected) _client.Groups.RequestCurrentGroups();
    }

    /// <summary>Snapshot of the agent's group memberships, or empty until the first
    /// <see cref="GroupsUpdated"/> has landed. Sorted by name so the UI needs no opinion.</summary>
    public IReadOnlyList<GroupEntry> GetGroups()
    {
        var snapshot = _groups;
        return snapshot ?? (IReadOnlyList<GroupEntry>)Array.Empty<GroupEntry>();
    }

    /// <summary>Last group list received, replaced wholesale by <see cref="OnCurrentGroups"/>.
    /// Read from the Godot main thread and written from a network thread, so it is swapped as a
    /// single reference rather than mutated in place — the same buffer-and-publish discipline
    /// AGENTS.md requires for world state.</summary>
    private volatile IReadOnlyList<GroupEntry>? _groups;

    private void OnCurrentGroups(object? sender, CurrentGroupsEventArgs e)
    {
        var list = new List<GroupEntry>(e.Groups.Count);
        foreach (var g in e.Groups.Values)
        {
            // Cache the name too: group chat lines and object owners resolve through the same
            // shared name cache, and a membership reply is a free source for it.
            _nameCache[g.ID.Guid] = g.Name ?? string.Empty;
            list.Add(new GroupEntry(
                g.ID.Guid, g.Name ?? string.Empty, g.MemberTitle ?? string.Empty,
                g.InsigniaID.Guid, g.AcceptNotices));
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        _groups = list;
        GroupsUpdated?.Invoke(this, new GroupsUpdatedEvent(list));
    }

    /// <summary>Joins a group's chat session. Required before <see cref="SendGroupMessage"/> can
    /// deliver anything — LibreMetaverse refuses to send into a session it has not joined. Result
    /// arrives on <see cref="GroupChatJoined"/>.</summary>
    public void JoinGroupChat(Guid groupId)
    {
        if (groupId == Guid.Empty || !_client.Network.Connected) return;
        _client.Self.RequestJoinGroupChat(new UUID(groupId));
    }

    /// <summary>Leaves a group's chat session (closing its tab), so the sim stops delivering it.</summary>
    public void LeaveGroupChat(Guid groupId)
    {
        if (groupId == Guid.Empty || !_client.Network.Connected) return;
        _client.Self.RequestLeaveGroupChat(new UUID(groupId));
    }

    /// <summary>Sends a message to a group chat session. No-op unless the session was joined
    /// first (see <see cref="JoinGroupChat"/>) — LibreMetaverse logs an error and drops it.</summary>
    public void SendGroupMessage(Guid groupId, string message)
    {
        if (groupId == Guid.Empty || string.IsNullOrEmpty(message) || !_client.Network.Connected) return;
        _client.Self.InstantMessageGroup(new UUID(groupId), message);
    }

    /// <summary>Membership fee out of a group invitation's binary bucket. The viewer reads that
    /// bucket as <c>{ S32 membership_fee; LLUUID role_id; }</c> and rejects an invitation whose
    /// bucket is not exactly that size (llimprocessing.cpp:846-857); the S32 is network byte
    /// order. A wrong size here means an unparseable bucket, not a free group, but the invitation
    /// itself is still worth showing — so this reports 0 rather than dropping it, and the fee is
    /// only ever displayed.</summary>
    private static int ParseGroupInvitationFee(byte[]? bucket)
    {
        const int ExpectedSize = 4 + 16; // S32 membership_fee + UUID role_id
        if (bucket == null || bucket.Length != ExpectedSize) return 0;
        return (bucket[0] << 24) | (bucket[1] << 16) | (bucket[2] << 8) | bucket[3];
    }

    /// <summary>Asset type and item id out of an inventory offer's binary bucket (BUG-INV-04).
    /// The two offer kinds pack it differently and the viewer size-checks each
    /// (llimprocessing.cpp:895-929, mirrored by LibreMetaverse's own
    /// InventoryManager.Handlers.cs:109-123):
    /// <list type="bullet">
    /// <item>agent offer — 17 bytes: <c>[0]</c> asset type, <c>[1..17]</c> the item id. The
    /// simulator has already copied the item into the agent's inventory at this point.</item>
    /// <item>object offer — 1 byte: asset type only. Nothing exists yet, so there is no id.</item>
    /// </list>
    /// Returns false for any other size — an offer we cannot file.</summary>
    internal static bool TryParseInventoryOfferBucket(
        byte[]? bucket, bool fromTask, out int assetType, out Guid itemId)
    {
        assetType = 0;
        itemId = Guid.Empty;
        if (bucket == null) return false;

        if (fromTask)
        {
            if (bucket.Length != 1) return false;
            assetType = bucket[0];
            return true;
        }

        const int AgentBucketSize = 1 + 16; // asset type + item id
        if (bucket.Length != AgentBucketSize) return false;
        assetType = bucket[0];
        itemId = new UUID(bucket, 1).Guid;
        return true;
    }

    /// <summary>Accepts or declines a pending inventory offer (BUG-INV-04). Both answers are sent
    /// — the simulator holds the offer open until one arrives, which is why an unanswered offer
    /// only surfaced after a relog.
    ///
    /// <para>The reply is an <c>ImprovedInstantMessage</c> back to the giver whose dialog is the
    /// offer's own +1 to accept and +2 to decline (llviewermessage.cpp:1604-1630 — "the math for
    /// the dialog works"), carrying the destination folder id in its binary bucket: the default
    /// folder for the asset type on accept, Trash on decline
    /// (llviewermessage.cpp:1936-1957). The offer's IM session id is the transaction id and must
    /// be echoed back or the simulator cannot match the answer to the offer.</para>
    ///
    /// <para>On accept the item is also fetched into the local inventory store. For an agent offer
    /// the simulator copied it in before the offer was even sent (llviewermessage.cpp:1714-1717),
    /// so without this the item exists server-side but no local view knows about it until the
    /// whole folder is fetched again — i.e. after a relog.</para>
    ///
    /// <para><b>Declining is not just a message.</b> Because the item is already in inventory, the
    /// decline IM only tells the giver; moving the item out is the <i>viewer's</i> job, and the
    /// reference viewer does it itself — <c>LLDiscardAgentOffer::done</c> calls
    /// <c>LLInventoryModel::removeObject</c>, which is <c>changeItemParent(item, Trash)</c>
    /// (llviewermessage.cpp:1160-1173, llinventorymodel.cpp:4277-4292, :4333-4348). Without that
    /// move a declined gift stays exactly where the grid put it, which on SL is the default folder
    /// for its type — reported live: declined, "es liegt trotzdem unter Objekte". OpenSim happens
    /// to trash it server-side as well (InventoryTransferModule.cs:348-390) and re-trashing an
    /// already-trashed item is a no-op there, so the same code is right on both grids.</para>
    /// </summary>
    /// <returns>The folder the item ended up in — the default folder for its type on accept, Trash
    /// on decline — so a UI can refresh exactly that one; or null when nothing was sent.</returns>
    public Guid? RespondToInventoryOffer(
        Guid offerId, Guid fromId, int assetType, Guid itemId, bool fromTask, bool accept)
    {
        if (fromId == Guid.Empty || !_client.Network.Connected) return null;

        var offerDialog = fromTask
            ? InstantMessageDialog.TaskInventoryOffered
            : InstantMessageDialog.InventoryOffered;
        var replyDialog = (InstantMessageDialog)((byte)offerDialog + (accept ? 1 : 2));

        var destination = accept
            ? _client.Inventory.FindFolderForType((AssetType)assetType)
            : _client.Inventory.FindFolderForType(FolderType.Trash);

        _client.Self.InstantMessage(
            _client.Self.Name,
            new UUID(fromId),
            string.Empty,
            new UUID(offerId),
            replyDialog,
            InstantMessageOnline.Offline,
            _client.Self.SimPosition,
            UUID.Zero,
            // Decline carries an empty bucket (llviewermessage.cpp:1629).
            accept ? destination.GetBytes() : Array.Empty<byte>());

        // Both answers need local work, and only an agent offer has an id to do it with: an
        // object's item does not exist until the accept above is processed, and arrives by the
        // usual BulkUpdateInventory route.
        if (itemId != Guid.Empty)
        {
            if (accept)
            {
                // Pull the offered item into LibreMetaverse's store so the inventory UI can see it
                // without a relog.
                _client.Inventory.RequestFetchInventory(new UUID(itemId), _client.Self.AgentID);
            }
            else
            {
                // ...and move a declined one out, because the grid already filed it. See the
                // remarks above: this is LLDiscardAgentOffer, not an extra courtesy.
                // AssetType.Folder (8) is how a whole offered folder announces itself
                // (llassettype.h:69 AT_CATEGORY; InventoryTransferModule.cs:182).
                _ = MoveToTrashAsync(itemId, isFolder: assetType == (int)AssetType.Folder);
            }
        }

        return destination == UUID.Zero ? null : destination.Guid;
    }

    /// <summary>The folder an item of this SL asset type is filed into by default — the viewer's
    /// <c>findCategoryUUIDForType(assetTypeToFolderType(type))</c> (llimprocessing.cpp:935). Null
    /// before login or when the grid has no such folder. Used to refresh the folder a declined
    /// gift has just been moved out of (BUG-INV-04).</summary>
    public Guid? DefaultFolderForAssetType(int assetType)
    {
        var id = _client.Inventory.FindFolderForType((AssetType)assetType);
        return id == UUID.Zero ? null : id.Guid;
    }

    /// <summary>Accepts or declines a pending group invitation. Both answers are sent — declining
    /// silently is not the same thing to the server as never answering.</summary>
    public void RespondToGroupInvitation(Guid groupId, Guid sessionId, bool accept)
    {
        if (groupId == Guid.Empty || !_client.Network.Connected) return;
        _client.Self.GroupInviteRespond(new UUID(groupId), new UUID(sessionId), accept);
        // Membership only changes server-side after the accept lands; re-ask so the Groups tab
        // catches up without needing a relog.
        if (accept) _client.Groups.RequestCurrentGroups();
    }

    private void OnGroupChatJoined(object? sender, GroupChatJoinedEventArgs e)
    {
        GroupChatJoined?.Invoke(this, new GroupChatJoinedEvent(
            e.SessionID.Guid, e.SessionName ?? string.Empty, e.Success));
    }

    /// <summary>Sends a local chat message.</summary>
    public void SendChat(string message, int channel = 0, ChatType type = ChatType.Normal)
    {
        if (_client.Network.Connected)
        {
            _client.Self.Chat(message, channel, type);
        }
    }
}
