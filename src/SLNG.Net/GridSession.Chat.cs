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
            RequestDisplayNames(idsToRequest);
        }
    }

    /// <summary>Asks the grid for these agents' Display Names and raises
    /// <see cref="DisplayNameResolved"/> for each one that comes back.
    ///
    /// <para>The result has to be READ. This used to be a bare fire-and-forget
    /// <c>GetDisplayNamesAsync(ids)</c> whose Task was dropped on the floor, on the assumption
    /// that the names would arrive via <c>AvatarManager.DisplayNameUpdate</c>. They do not:
    /// that event is the grid's unsolicited "someone changed their name" push, while the reply
    /// to a lookup is the Task's own result. So nothing ever set a display name and every
    /// nametag showed the legacy name.</para>
    ///
    /// <para>Batched because the cap takes a list, and swallowing failures on purpose: Display
    /// Names are an optional capability and most OpenSim grids do not serve them, where the
    /// correct behaviour is simply to keep showing the legacy name.</para></summary>
    private void RequestDisplayNames(System.Collections.Generic.List<UUID> ids)
    {
        _ = Task.Run(async () =>
        {
            bool ok = false;
            try
            {
                var (success, names, _) = await _client.Avatars.GetDisplayNamesAsync(ids).ConfigureAwait(false);
                ok = success && names != null;
                if (ok)
                {
                    Volatile.Write(ref _displayNameFailingSince, 0);
                    Volatile.Write(ref _displayNameRetryDelaySeconds, 1);
                }
                if (Diag.Verbose)
                    Console.Error.WriteLine(
                        $"[DisplayName] asked for {ids.Count}: success={success} names={names?.Length ?? 0} " +
                        $"default={names?.Count(x => x?.IsDefaultDisplayName == true) ?? 0} " +
                        $"capability={_client.Avatars.DisplayNamesAvailable()}");
                if (!ok) return;
                var answeredAt = DateTime.UtcNow;
                foreach (var n in names!)
                {
                    if (n == null) continue;

                    // IsDefaultDisplayName means the resident never set one and the grid is
                    // echoing the legacy name back. Raising it would make the nametag show the
                    // same text twice, so treat it as "no display name" -- and remember exactly
                    // that, so the next login does not ask about them again.
                    bool hasOwn = !n.IsDefaultDisplayName && !string.IsNullOrEmpty(n.DisplayName);
                    _displayNameCache.Set(n.ID.Guid, hasOwn ? n.DisplayName : null, answeredAt);
                    if (!hasOwn) continue;
                    DisplayNameResolved?.Invoke(this, new NameResolvedEvent(n.ID.Guid, n.DisplayName!));
                }
            }
            catch (Exception ex)
            {
                // Optional capability -- absent on most OpenSim grids, so not an error by default.
                if (Diag.Verbose)
                    Console.Error.WriteLine($"[DisplayName] lookup threw {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                // A failed lookup is REMEMBERED and retried on its own clock (and the moment the
                // region's capabilities are ready), instead of waiting for the next update of that
                // avatar. The first call for an agent routinely lands before the capability
                // handshake has finished -- at login it always does -- and a standing avatar sends
                // an update only when something about it changes, so "retry on the next update"
                // left nametags on the legacy name for as long as it took someone to move.
                // The ids stay claimed meanwhile, so repeated updates do not queue duplicates.
                if (ok)
                {
                    foreach (var id in ids) _displayNamesWanted.TryRemove(id.Guid, out _);
                }
                else
                {
                    foreach (var id in ids)
                    {
                        _displayNamesRequested.TryAdd(id.Guid, 0);
                        _displayNamesWanted[id.Guid] = 0;
                    }

                    NoteDisplayNameFailure();
                    ScheduleDisplayNameRetry();
                }
            }
        });
    }

    /// <summary>The cap answers at most this many ids per request (LibreMetaverse's own limit).</summary>
    private const int DisplayNameBatchMax = 90;

    /// <summary>The retry delay doubles from one second up to this, so a grid that never answers
    /// costs a request every few seconds and not a storm.</summary>
    private const int DisplayNameRetryMaxDelaySeconds = 15;

    /// <summary>Agents whose lookup failed and who still have no answer.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _displayNamesWanted = new();

    private int _displayNameRetryScheduled;
    private int _displayNameRetryDelaySeconds = 1;

    /// <summary>Retry the wanted names after a growing delay. One timer at a time, however many
    /// lookups fail.</summary>
    private void ScheduleDisplayNameRetry()
    {
        if (Interlocked.Exchange(ref _displayNameRetryScheduled, 1) == 1) return;

        int delay = Volatile.Read(ref _displayNameRetryDelaySeconds);
        Volatile.Write(ref _displayNameRetryDelaySeconds, Math.Min(delay * 2, DisplayNameRetryMaxDelaySeconds));

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
            Volatile.Write(ref _displayNameRetryScheduled, 0);
            RetryWantedDisplayNames();
        });
    }

    /// <summary>Asks again for every name still wanted, in batches. Called by the retry timer and
    /// by <c>RegionCapabilitiesReady</c>, which is the moment the first attempt of a login can
    /// finally succeed.</summary>
    internal void RetryWantedDisplayNames()
    {
        if (!_client.Network.Connected) return;

        var batch = new System.Collections.Generic.List<UUID>();
        foreach (var id in _displayNamesWanted.Keys)
        {
            if (batch.Count >= DisplayNameBatchMax) break;
            if (_displayNamesWanted.TryRemove(id, out _)) batch.Add(new UUID(id));
        }

        // A failure puts them back and re-arms the timer; more than one batch's worth left over
        // gets its own turn.
        if (batch.Count > 0) RequestDisplayNames(batch);
        if (!_displayNamesWanted.IsEmpty) ScheduleDisplayNameRetry();
    }

    /// <summary>How long lookups have to keep failing, without one success in between, before it
    /// is worth telling the user. The first lookup for an avatar routinely lands before the
    /// region's capability handshake and the retry (timer, or caps-ready) succeeds seconds
    /// later, so a failure on its own says nothing; a failure that persists does.</summary>
    private const double DisplayNameFailureGraceSeconds = 30;

    /// <summary>Stopwatch timestamp of the first failure in the current unbroken run of failures;
    /// 0 when the last lookup succeeded (or none has failed yet).</summary>
    private long _displayNameFailingSince;

    /// <summary>Called for every failed lookup. Says something once per session, and only when the
    /// failure has lasted <see cref="DisplayNameFailureGraceSeconds"/> with the capability PRESENT:
    /// the grid offers Display Names and answering keeps failing, which is a fault. Without the
    /// capability it is the normal state of an OpenSim grid, and stays a diagnostic line.
    /// (Earlier this printed on the very first failed attempt, as an error, and then was hidden
    /// entirely -- the first was noise on a healthy login, the second would have hidden the one
    /// case where nametags really were stuck on legacy names.)</summary>
    private void NoteDisplayNameFailure()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long since = Interlocked.CompareExchange(ref _displayNameFailingSince, now, 0);
        if (since == 0) since = now;
        if (System.Diagnostics.Stopwatch.GetElapsedTime(since, now).TotalSeconds < DisplayNameFailureGraceSeconds) return;
        if (_displayNameFailureLogged) return;

        bool capability = _client.Avatars.DisplayNamesAvailable();
        if (!capability && !Diag.Verbose) return;

        _displayNameFailureLogged = true;
        Console.Error.WriteLine(capability
            ? "[DisplayName] lookups have been failing for 30 s although the grid offers Display Names -- " +
              "nametags stay on the legacy name."
            : "[DisplayName] the grid offers no Display Names capability -- nametags show the legacy name.");
    }

    /// <summary>Requests one agent's Display Name, deduped against the ids already asked for.
    /// Avatars in view never reach <see cref="OnUUIDNameReply"/> -- their legacy name rides along
    /// on the ObjectUpdate -- so without this nothing would ever ask for theirs.</summary>
    public void RequestDisplayName(Guid agentId)
    {
        if (agentId == Guid.Empty || !_client.Network.Connected) return;
        if (!_displayNamesRequested.TryAdd(agentId, 0)) return;

        // What we remember from earlier sessions is on screen immediately, with no round trip. An
        // answer we still trust ends there; an old one is shown now AND asked for again below, so
        // a name that changed while we were away corrects itself within a moment.
        var freshness = _displayNameCache.Lookup(agentId, DateTime.UtcNow, out var remembered);
        if (freshness != DisplayNameCache.Freshness.Miss && remembered.Length > 0)
            DisplayNameResolved?.Invoke(this, new NameResolvedEvent(agentId, remembered));
        if (freshness == DisplayNameCache.Freshness.Fresh) return;

        RequestDisplayNames(new System.Collections.Generic.List<UUID> { new UUID(agentId) });
    }

    /// <summary>Display Names learned so far, this session and earlier ones. Always present, so
    /// lookups are answered from it whether or not a file was ever opened.</summary>
    private readonly DisplayNameCache _displayNameCache = new();

    private string? _displayNameCachePath;

    /// <summary>
    /// Loads the on-disk Display Name cache and replays what it holds to anyone already listening.
    /// Called by <see cref="OnLoginResponseOpenCaches"/> once per login, with the directory
    /// <see cref="UseCacheDirectories"/> recorded, same as <see cref="OpenInventoryCache"/>. Keyed
    /// by agent id so two accounts, or one name on two grids, never read each other's file.
    ///
    /// <para>The login response is processed before the region is entered, so normally no avatar
    /// has asked yet and every later <see cref="RequestDisplayName"/> is answered from the loaded
    /// cache. The replay covers any that asked earlier: they get their remembered name now instead
    /// of after the next round trip.</para>
    /// </summary>
    private void OpenDisplayNameCache(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return; // no cache wanted (tests, tools)

        var agent = _client.Self.AgentID;
        if (agent == UUID.Zero)
        {
            // Silent until BUG-INV-12, which is how a cache opened before the login went unnoticed.
            Console.Error.WriteLine("[DisplayName] no agent id yet -- the name cache opens only once a login has succeeded; not opening it");
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            _displayNameCachePath = System.IO.Path.Combine(directory, $"{agent.Guid:N}.names.json");

            if (File.Exists(_displayNameCachePath))
                _displayNameCache.Absorb(DisplayNameCache.FromJson(File.ReadAllText(_displayNameCachePath)));

            // Only the agents already known to be in view: replaying the whole file would fill the
            // world with names for people who are not here.
            int replayed = 0;
            foreach (var (id, name) in _displayNameCache.NamedEntries())
            {
                if (!_displayNamesRequested.ContainsKey(id)) continue;
                DisplayNameResolved?.Invoke(this, new NameResolvedEvent(id, name));
                replayed++;
            }

            if (Diag.Verbose)
                Console.Error.WriteLine($"[DisplayName] cache: {_displayNameCache.Count} remembered, {replayed} replayed to avatars already in view");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DisplayName] could not open the name cache in {directory}: {ex.Message}");
            _displayNameCachePath = null;
        }
    }

    /// <summary>Writes the Display Name cache. Call on quit and on logout, next to
    /// <see cref="SaveInventoryCache"/>; a session the grid ends saves it by itself (BUG-INV-13).
    /// Written to a side file and moved into place, so a crash mid-write leaves the previous file
    /// intact instead of a half-written one.</summary>
    public void SaveDisplayNameCache()
    {
        if (_displayNameCachePath == null) return;

        // The two writers are the quit on the main thread and a grid-ended session on a library
        // thread; they share the side file.
        lock (_displayNameCacheSaveGate)
        {
            try
            {
                string temp = _displayNameCachePath + ".tmp";
                File.WriteAllText(temp, _displayNameCache.ToJson());
                File.Move(temp, _displayNameCachePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DisplayName] could not save the name cache {_displayNameCachePath}: {ex.Message}");
            }
        }
    }

    private readonly object _displayNameCacheSaveGate = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _displayNamesRequested = new();
    private volatile bool _displayNameFailureLogged;

    internal void OnDisplayNameUpdate(object? sender, DisplayNameUpdateEventArgs e)
    {
        var id = e.DisplayName.ID.Guid;
        string? displayName = e.DisplayName.DisplayName;
        if (string.IsNullOrEmpty(displayName)) return;

        _displayNameCache.Set(id, displayName, DateTime.UtcNow);

        // Repaints the nametag (and, in WorldSimulation, replaces the name it remembers).
        DisplayNameResolved?.Invoke(this, new NameResolvedEvent(id, displayName));

        // The reference viewer also says so ("[OLD] ([SLID]) is now known as [NEW].",
        // llviewerdisplayname.cpp:206). The username is what stays the same across the change, so
        // it stands in for the old name when the grid did not send one.
        string userName = e.DisplayName.UserName ?? string.Empty;
        string oldName = string.IsNullOrEmpty(e.OldDisplayName) ? userName : e.OldDisplayName;
        DisplayNameChanged?.Invoke(this, new DisplayNameChangedEvent(id, oldName, displayName, userName));
    }

    private void OnGroupNamesReply(object? sender, GroupNamesEventArgs e)
    {
        foreach (var kvp in e.GroupNames)
        {
            var id = kvp.Key.Guid;
            // An empty answer must not replace a name we already know, and is stored as the placeholder only
            // for the object-owner displays; group chat treats it as "unknown" (GroupChatSessionLogic.IsRealName).
            if (!GroupChatSessionLogic.IsRealName(kvp.Value) && HasCachedName(id)) continue;
            var name = string.IsNullOrEmpty(kvp.Value) ? GroupChatSessionLogic.UnknownGroupName : kvp.Value;
            _nameCache[id] = name;
            if (_pendingGroupNameRequests.TryRemove(id, out _) && Diag.Verbose)
                Console.Error.WriteLine($"[GroupChat] name reply for {id}: \"{kvp.Value}\"");
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

        // FEAT-UI-34: NotificationId and ExtraParams used to be thrown away here, which is why a
        // restart notice was only ever one line of text. The LLSD stays in this method -- the rest
        // of the app gets a neutral RegionRestartEvent.
        if (!RegionRestartAlert.IsRestartNotification(e.NotificationId)) return;

        var extra = e.ExtraParams;
        if (RegionRestartAlert.TryCreate(
                e.NotificationId,
                extra != null && extra.TryGetValue("NAME", out var name) ? name.AsString() : null,
                extra != null && extra.TryGetValue("MINUTES", out var minutes) ? minutes.AsInteger() : null,
                extra != null && extra.TryGetValue("SECONDS", out var seconds) ? seconds.AsInteger() : null,
                out var restart))
        {
            RegionRestartReceived?.Invoke(this, restart);
        }
    }

    /// <summary>Looks up an already-resolved user/group name from the local cache. Returns
    /// false (with the raw id's string form) if it hasn't been fetched yet -- call
    /// <see cref="RequestAvatarName"/>/<see cref="RequestGroupName"/> and wait for
    /// <see cref="NameResolved"/> in that case.</summary>
    public bool TryGetCachedName(Guid id, out string name)
    {
        if (_nameCache.TryGetValue(id, out var cached) && GroupChatSessionLogic.IsRealName(cached))
        {
            name = cached;
            return true;
        }
        if (_client.Friends.FriendList.TryGetValue(new UUID(id), out var friend) && GroupChatSessionLogic.IsRealName(friend.Name))
        {
            _nameCache[id] = friend.Name;
            name = friend.Name;
            return true;
        }
        if (_nameCache.TryGetValue(id, out var placeholder))
        {
            name = placeholder;
            return false;
        }
        name = id.ToString();
        return false;
    }

    /// <summary>The agent's Display Name when one is known and is really their own (a resident who never set
    /// one has none). Answers from the same cache the nametags use, so it is instant and never blocks; a
    /// miss means "not asked yet or not answered yet" -- call <see cref="RequestDisplayName"/> and wait for
    /// <see cref="DisplayNameResolved"/>. An answer past its freshness is still returned: a slightly old
    /// name beats flashing the legacy name while the refresh is on its way.</summary>
    public bool TryGetDisplayName(Guid id, out string name)
    {
        _displayNameCache.Lookup(id, DateTime.UtcNow, out name);
        return name.Length > 0;
    }

    /// <summary>True once the grid has answered for this agent -- with a Display Name or with "none of their own" --
    /// even when the answer is old. <see cref="TryGetDisplayName"/> cannot tell "none" from "not answered yet"; a
    /// caller that wants to wait for the answer needs this. A grid that serves no Display Names never answers.</summary>
    public bool HasDisplayNameAnswer(Guid id) =>
        _displayNameCache.Lookup(id, DateTime.UtcNow, out _) != DisplayNameCache.Freshness.Miss;

    /// <summary>Whether the grid's Display Names capability is known to be there. False right after login, before the
    /// region's capability handshake -- so "false" alone does not say a grid has none; a caller deciding whether to wait
    /// for an answer should allow for that (an OpenSim grid keeps it false for good).</summary>
    public bool DisplayNamesAvailable => _client.Avatars.DisplayNamesAvailable();

    private readonly SessionLineGate _sessionLineGate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid> _conferencePeers = new();

    private bool IsDuplicateSessionLine(InstantMessage im)
    {
        lock (_sessionLineGate)
            return _sessionLineGate.IsDuplicate(im.IMSessionID.Guid, im.FromAgentID.Guid, im.Message ?? "", DateTime.UtcNow.Ticks);
    }

    /// <summary>The session name a message carries in its binary bucket (a UTF-8 string, NUL-terminated), or
    /// empty when there is none or it is not text.</summary>
    internal static string DecodeSessionName(byte[]? bucket) => GroupChatSessionLogic.DecodeSessionName(bucket);

    /// <summary>Sends a line into an ad-hoc conference session: <c>SessionSend</c> with the session id, addressed
    /// to the session's other participant as the viewer does (<c>LLIMModel::sendMessage</c>,
    /// llimview.cpp:2200-2225). The line comes back through the session like any other and is shown then.
    /// Returns false when nothing was sent.</summary>
    public bool SendConferenceMessage(Guid sessionId, string message)
    {
        if (sessionId == Guid.Empty || string.IsNullOrEmpty(message) || !_client.Network.Connected) return false;

        Guid to = _conferencePeers.TryGetValue(sessionId, out var peer) ? peer : sessionId;
        if (Diag.Verbose)
            Console.Error.WriteLine($"[Conference] send session={sessionId} to={to} len={message.Length}");
        SendSessionPacket(to, sessionId, message, InstantMessageDialog.SessionSend);
        return true;
    }

    /// <summary>One line or leave of an ad-hoc session, packed as the viewer packs it (<c>pack_instant_message</c>): the
    /// region id of the region we stand in and the one-byte empty binary bucket. Without those two the grid accepted the
    /// packet and relayed it to nobody (BUG-NET-32: others saw nothing, and no echo came back); with them it goes through.</summary>
    private void SendSessionPacket(Guid to, Guid sessionId, string message, InstantMessageDialog dialog)
    {
        _client.Self.InstantMessage(
            _client.Self.Name,
            new UUID(to),
            message,
            new UUID(sessionId),
            dialog,
            InstantMessageOnline.Online,
            _client.Self.SimPosition,
            _client.Network.CurrentSim?.ID ?? UUID.Zero,
            new byte[] { 0 });
    }

    /// <summary>Leaves every conference we are in. Called before logging out: the grid keeps a conference's members past
    /// their logout, and the next login then meets a session it half-remembers (BUG-NET-32).</summary>
    public void LeaveAllConferences()
    {
        foreach (var id in _conferenceSessions.Keys.ToArray()) LeaveConference(id);
    }

    /// <summary>Leaves an ad-hoc conference: <c>SessionDrop</c> (IM_SESSION_LEAVE) with the session id, addressed to the
    /// session's other participant like a line is (<c>LLIMModel::sendLeaveSession</c>, llimview.cpp:1910). Without it the
    /// grid keeps us a member of the session for good, and a later invitation to the same people (the session id is the
    /// same) finds a ghost membership it cannot accept -- BUG-NET-32: accept answers 500 and our lines are dropped.</summary>
    public bool LeaveConference(Guid sessionId)
    {
        if (sessionId == Guid.Empty) return false;

        bool sent = _client.Network.Connected;
        if (sent)
        {
            Guid to = _conferencePeers.TryGetValue(sessionId, out var peer) ? peer : sessionId;
            if (Diag.Verbose) Console.Error.WriteLine($"[Conference] leave session={sessionId} to={to}");
            SendSessionPacket(to, sessionId, string.Empty, InstantMessageDialog.SessionDrop);
        }
        _client.Self.GroupChatSessions.TryRemove(new UUID(sessionId), out _);
        _conferencePeers.TryRemove(sessionId, out _);
        _conferenceSessions.TryRemove(sessionId, out _);
        _conferenceSpeakers.TryRemove(sessionId, out _);
        return sent;
    }

    /// <summary>A group's name: from the membership list first (it already carries it, and is there before
    /// any name reply), then the shared name cache. False, with the id's text, when neither knows it yet.</summary>
    private bool IsGroupMember(Guid id) => _groups?.Any(g => g.Id == id) == true;

    public bool TryGetGroupName(Guid groupId, out string name)
    {
        var member = _groups?.FirstOrDefault(g => g.Id == groupId);
        if (member != null && GroupChatSessionLogic.IsRealName(member.Name))
        {
            name = member.Name;
            return true;
        }
        if (TryGetCachedName(groupId, out name) && GroupChatSessionLogic.IsRealName(name)) return true;
        name = groupId.ToString();
        return false;
    }

    /// <summary>True when the shared name cache holds a NAME for this id; an entry with an empty name, or the
    /// "(unknown group)" placeholder a blank name reply leaves behind, is not one (BUG-UI-23: it used to be, and
    /// kept a group with no known name from ever being asked for again).</summary>
    private bool HasCachedName(Guid id)
    {
        if (_nameCache.TryGetValue(id, out var cached) && GroupChatSessionLogic.IsRealName(cached))
            return true;
        if (_client.Friends.FriendList.TryGetValue(new UUID(id), out var friend) && GroupChatSessionLogic.IsRealName(friend.Name))
        {
            _nameCache[id] = friend.Name;
            return true;
        }
        return false;
    }

    /// <summary>Remembers a group's name learnt from somewhere other than a name reply, unless a real one is known
    /// already, and tells the listeners (<see cref="NameResolved"/>) so a tab titled with the id is renamed.</summary>
    private void RememberGroupName(Guid groupId, string name)
    {
        if (groupId == Guid.Empty || !GroupChatSessionLogic.IsRealName(name) || HasCachedName(groupId)) return;
        _nameCache[groupId] = name;
        NameResolved?.Invoke(this, new NameResolvedEvent(groupId, name));
    }

    private readonly GroupNameRequestGate _groupNameRequestGate = new();
    private readonly ConcurrentDictionary<Guid, byte> _pendingGroupNameRequests = new();
    private readonly ConcurrentDictionary<Guid, byte> _loggedGroupSessions = new();

    /// <summary>Asks the grid for a group's name, at most once per <see cref="GroupNameRequestGate.MinInterval"/>.
    /// The answer lands in the name cache and raises <see cref="NameResolved"/>, which re-titles the tab.</summary>
    private void RequestUnknownGroupName(Guid groupId)
    {
        if (!_groupNameRequestGate.ShouldRequest(groupId, DateTime.UtcNow.Ticks)) return;
        _pendingGroupNameRequests[groupId] = 0;
        RequestGroupName(groupId);
    }

    /// <summary>BUG-UI-23 diagnostic, <c>--diag</c> only: one line the first time a group session is seen. Says
    /// whether the membership list knew it as a group, where its name came from, and whether the user had it muted.
    /// A report of "wrong tab title" or "muted group still shows" on Second Life cannot be reproduced on OpenSim,
    /// so this line is how the next live session says which path it took.</summary>
    private void LogNewGroupSession(
        InstantMessage im, GroupNameSource source, string name, bool muted,
        bool membershipLoaded = true, bool inMembership = false)
    {
        if (!Diag.Verbose || !_loggedGroupSessions.TryAdd(im.IMSessionID.Guid, 0)) return;
        string nameSource = muted ? "skipped(muted)"
            : source == GroupNameSource.None ? "none(name requested)"
            : source.ToString().ToLowerInvariant();
        Console.Error.WriteLine(
            $"[GroupChat] new session {im.IMSessionID.Guid} dialog={im.Dialog} groupFlag={im.GroupIM}" +
            $" membership={(!membershipLoaded ? "not-loaded" : inMembership ? "member" : "not-in-list")}" +
            $" nameSource={nameSource} name=\"{name}\" muted={(muted ? "yes" : "no")}");
    }

    public void RequestAvatarName(Guid agentId)
    {
        if (agentId == Guid.Empty) return;
        if (HasCachedName(agentId))
        {
            if (TryGetCachedName(agentId, out var cached))
                NameResolved?.Invoke(this, new NameResolvedEvent(agentId, cached));
            return;
        }
        if (!_client.Network.Connected) return;
        _client.Avatars.RequestAvatarName(new UUID(agentId));
    }

    public void RequestGroupName(Guid groupId)
    {
        if (groupId == Guid.Empty) return;
        if (HasCachedName(groupId))
        {
            if (TryGetCachedName(groupId, out var cached))
                NameResolved?.Invoke(this, new NameResolvedEvent(groupId, cached));
            return;
        }
        if (!_client.Network.Connected) return;
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
            else if (!string.IsNullOrEmpty(name))
            {
                _nameCache[id] = name;
            }
            // LibreMetaverse names the rights by who HOLDS them: TheirFriendRights is what the friend may do with
            // us (what we granted), MyFriendRights what we may do with them (what they granted). Checked against
            // FriendInfo.CanSeeMeOnline ("the friend can see if I am online") and the way its ChangeUserRights
            // handler fills them.
            var grantedToMe = (FriendPermissions)(int)friend.MyFriendRights;
            _rightsGrantedToUs.TryAdd(id, grantedToMe); // a friend added since login: the first sight is the baseline
            result.Add(new FriendEntry(id, name, friend.IsOnline,
                GrantedByMe: (FriendPermissions)(int)friend.TheirFriendRights,
                GrantedToMe: grantedToMe));
        }
        return result;
    }

    // Self.IM carries every instant-message-shaped packet (friendship offers, teleport
    // requests, group notices, ...), not just plain 1:1 chat -- filter to MessageFromAgent so
    // Phase 1c's IM tabs only see actual conversation messages. The others get their own
    // dedicated flows later rather than being half-handled here.
    private void OnInstantMessage(object? sender, InstantMessageEventArgs e)
    {
        Console.WriteLine($"[Chat-Packet] Dialog={(int)e.IM.Dialog} ({e.IM.Dialog}), From={e.IM.FromAgentID} ({e.IM.FromAgentName}), Session={e.IM.IMSessionID}, Group={e.IM.GroupIM}, Text='{e.IM.Message}'");

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

        // A teleport offer (22 / 25) and a teleport request (26) are dialogs of their own and fell
        // through the MessageFromAgent guard at the bottom just like the two above -- "ich kann
        // kein TP request annehmen" (BUG-NET-27). Hand-decoded, see TryDecodeTeleportIm; there is
        // no LibreMetaverse event for either to subscribe to, let alone one that answers for us.
        if (TryDecodeTeleportIm(
                e.IM.Dialog, e.IM.IMSessionID.Guid, e.IM.FromAgentID.Guid,
                e.IM.FromAgentName, e.IM.Message, e.IM.BinaryBucket) is { } teleport)
        {
            TeleportOfferReceived?.Invoke(this, teleport);
            return;
        }

        // A friendship offer (38) and the answers to our own (39 / 40) are dialogs of their own and
        // fell through the MessageFromAgent guard at the bottom like the ones above (BUG-NET-28).
        // Deliberately NOT via LibreMetaverse's FriendsManager.FriendshipOffered: it is only raised
        // while somebody listens, and we decode by hand like the group invitation. Its own Self.IM
        // handler still records an acceptance (39) in FriendList, which is why 39 only has to tell
        // the UI to re-read it.
        if (TryDecodeFriendshipOffer(
                e.IM.Dialog, e.IM.IMSessionID.Guid, e.IM.FromAgentID.Guid, e.IM.FromAgentName, e.IM.Message,
                online: e.IM.Offline == InstantMessageOnline.Online) is { } friendship)
        {
            FriendshipOfferReceived?.Invoke(this, friendship);
            return;
        }

        if (TryDecodeFriendshipAnswer(e.IM.Dialog, e.IM.FromAgentID.Guid, e.IM.FromAgentName) is { } answer)
        {
            if (answer.Accepted)
            {
                FriendListChanged?.Invoke(this, EventArgs.Empty);
                RequestFriendOnlineStatus(answer.FromId); // LibreMetaverse asks once, possibly too early
            }
            FriendshipAnswered?.Invoke(this, answer);
            return;
        }

        // A group NOTICE carries the group flag too, and used to fall into the group-chat branch below: it opened a
        // chat tab titled with an id when the group was not in the list (BUG-UI-25). It is a notification.
        // In Second Life, the group ID is packed into the binary bucket header (llimprocessing.cpp:702-728).
        if (e.IM.Dialog is InstantMessageDialog.GroupNotice or InstantMessageDialog.GroupNoticeRequested)
        {
            Guid groupId = Guid.Empty;
            bool hasInventory = false;
            int assetType = 0;
            string itemName = string.Empty;

            if (TryParseGroupNoticeBucket(e.IM.BinaryBucket, out var bucketGroupId, out hasInventory, out assetType, out itemName))
            {
                groupId = bucketGroupId;
            }

            Guid session = e.IM.IMSessionID.Guid, from = e.IM.FromAgentID.Guid;
            if (groupId == Guid.Empty)
            {
                groupId = IsGroupMember(session) ? session : (IsGroupMember(from) ? from : session);
            }

            string groupName = string.Empty;
            if (groupId != Guid.Empty)
            {
                if (TryGetGroupName(groupId, out var resolvedName))
                {
                    groupName = resolvedName;
                }
                else
                {
                    RequestUnknownGroupName(groupId);
                }
            }

            string text = e.IM.Message ?? string.Empty;
            int bar = text.IndexOf('|');
            string subject = bar < 0 ? string.Empty : text[..bar].Trim();
            string body = bar < 0 ? text : text[(bar + 1)..].Trim();

            GroupNoticeReceived?.Invoke(this, new GroupNoticeEvent(
                groupId, e.IM.FromAgentName ?? string.Empty,
                subject, body, groupName, hasInventory, assetType, itemName));
            return;
        }

        // "Somebody is typing": its own dialog (41/42) with the text "typing".
        // The reference viewer and Firestorm process this by sender ID (llimprocessing.cpp:649
        // gIMMgr->processIMTypingStart(from_id, dialog)) and ignore the packet's session ID on the wire,
        // which may be UUID.Zero, the sender/recipient ID, or the peer-to-peer XOR (BUG-UI-36).
        // Handled ahead of group/session classification so it is never dropped by group mute or session gates.
        if (e.IM.Dialog is InstantMessageDialog.StartTyping or InstantMessageDialog.StopTyping)
        {
            Guid typist = e.IM.FromAgentID.Guid;
            Guid self = _client.Self.AgentID.Guid;
            Guid imSession = e.IM.IMSessionID.Guid;
            bool isConferenceOrGroup = e.IM.GroupIM
                || (imSession != Guid.Empty && imSession != typist && imSession != self
                    && !SessionIds.IsPeerToPeer(imSession, self, typist)
                    && (_conferenceSessions.ContainsKey(imSession) || _client.Self.GroupChatSessions.ContainsKey(e.IM.IMSessionID)));

            if (typist != Guid.Empty && typist != self && !isConferenceOrGroup)
            {
                Console.WriteLine($"[Chat] Peer typing indicator from {typist} ({e.IM.FromAgentName}): typing={e.IM.Dialog == InstantMessageDialog.StartTyping}");
                InstantMessageTyping?.Invoke(this, new InstantMessageTypingEvent(
                    typist, e.IM.FromAgentName ?? string.Empty, e.IM.Dialog == InstantMessageDialog.StartTyping));
            }
            return;
        }

        // Group chat first, and NOT by inspecting the dialog byte: it arrives as
        // InstantMessageDialog.SessionSend, not MessageFromAgent, and its GroupIM flag is only set
        // on the first message of a session -- a later one carries just the session id. Both the
        // old `Dialog != MessageFromAgent` test and the old `|| e.IM.GroupIM` bail therefore
        // dropped group chat, twice over.
        // ...but a session is not always a group: a resident can IM from an ad-hoc conference session
        // (IM_SESSION_SEND with a session id that is no group of ours). Treating that as group chat opened a
        // tab named after the session's UUID, and a reply into it was answered with "You are the only
        // participant in this IM session". Only a session that is one of our groups is group chat; the rest
        // falls through to be shown as the speaker's IM.
        // BUG-UI-23: and a session that IS one of our groups is group chat even when LibreMetaverse's own
        // GroupChatSessions table does not know it (see GroupChatSessionLogic.IsGroupSession).
        Guid sessionId = e.IM.IMSessionID.Guid;
        bool hasText = !string.IsNullOrEmpty(e.IM.Message);

        // FEAT-UI-54 / BUG-UI-23: a group whose chat the user switched off is dropped before anything sees it,
        // decided by the session id alone and ahead of every classification below -- the invitation that
        // opens the session (LibreMetaverse has already joined it by now), a UDP line, and a stray line after
        // we left all end here, so none of them can open a tab, count as unread, be logged or notify.
        // The typing indicators are their own dialogs and are dropped further down anyway.
        bool muted = GroupChatSessionLogic.IsSessionLineDialog(e.IM.Dialog) && GroupChatIgnored?.Invoke(sessionId) == true;
        if (GroupChatSessionLogic.ShouldConsumeAsIgnored(e.IM.Dialog, hasText, muted))
        {
            LogNewGroupSession(e.IM, GroupNameSource.None, string.Empty, muted: true);
            TryConsumeIgnoredGroupChat(sessionId);
            return;
        }

        var membership = _groups;
        bool inMembership = membership != null && membership.Any(g => g.Id == sessionId);
        if (GroupChatSessionLogic.IsGroupSession(
                e.IM.Dialog, e.IM.GroupIM, _client.Self.IsGroupMessage(e.IM), membership != null, inMembership))
        {
            if (!hasText) return; // typing/keep-alive, same as local chat
            // Our own line comes back through the session and is the ONLY copy: unlike a 1:1 IM the viewer does
            // not echo a session line locally (llimview.cpp, LLIMModel::sendMessage echoes only IM_NOTHING_SPECIAL).
            if (IsDuplicateSessionLine(e.IM)) return;
            // For group chat the session id IS the group id.
            // The name: the membership list, then the message's binary bucket (the viewer's session name,
            // llimview.cpp:3172-3178 -- the speaker's name, FromAgentName, is never the group's), then the shared
            // name cache. When nobody knows it a request goes out and the tab is re-titled when the answer arrives.
            string bucketName = DecodeSessionName(e.IM.BinaryBucket);
            var member = membership?.FirstOrDefault(g => g.Id == sessionId);
            _nameCache.TryGetValue(sessionId, out var cachedName);
            var (groupName, nameSource) = GroupChatSessionLogic.ChooseName(member?.Name, bucketName, cachedName);
            if (nameSource == GroupNameSource.Bucket) RememberGroupName(sessionId, groupName);
            if (nameSource == GroupNameSource.None) RequestUnknownGroupName(sessionId);
            LogNewGroupSession(e.IM, nameSource, groupName, muted: false, membership != null, inMembership);
            GroupChatMessageReceived?.Invoke(this, new GroupChatMessageEvent(
                sessionId, e.IM.FromAgentID.Guid, e.IM.FromAgentName, e.IM.Message, groupName));
            return;
        }

        // An ad-hoc conference: several people, no group. Its own session, answered in the same session.
        // The invitation itself ("X was invited") is a session line too, whatever dialog it carries: the viewer files it
        // into the conference (llimview.cpp:4274, addMessage with the session id), it does not make it a 1:1 IM. LibreMetaverse
        // has just registered the session for it (GroupChatSessions) and accepted it, which is what tells it from a plain IM.
        // The viewer drops an invitation from ourselves (llimview.cpp:4270).
        Guid imFrom = e.IM.FromAgentID.Guid, imSelf = _client.Self.AgentID.Guid;
        bool lmvKnowsSession = _client.Self.GroupChatSessions.ContainsKey(e.IM.IMSessionID);
        bool invitationLine = e.IM.Dialog == InstantMessageDialog.MessageFromAgent && imFrom != Guid.Empty
            && !SessionIds.IsPeerToPeer(sessionId, imSelf, imFrom) && lmvKnowsSession;
        if (invitationLine && imFrom == imSelf) return;

        if (e.IM.Dialog == InstantMessageDialog.SessionSend || invitationLine)
        {
            if (string.IsNullOrEmpty(e.IM.Message) || IsDuplicateSessionLine(e.IM)) return;

            Guid from = imFrom;
            // Who to address replies to: the viewer sends a session line to the session's "other participant",
            // which for an invited session is whoever invited us (llimview.cpp addMessage -> target_id).
            _conferenceSessions.TryAdd(sessionId, 0);
            if (from != Guid.Empty && from != imSelf) _conferencePeers.TryAdd(sessionId, from);
            if (from != Guid.Empty && _conferenceSpeakers.GetOrAdd(sessionId, _ => new()).TryAdd(from, 0))
                ConferenceMembersChanged?.Invoke(this, new ConferenceMembersChangedEvent(sessionId));

            ConferenceChatMessageReceived?.Invoke(this, new ConferenceChatMessageEvent(
                sessionId, DecodeSessionName(e.IM.BinaryBucket), from, e.IM.FromAgentName, e.IM.Message));
            return;
        }

        if (e.IM.Dialog != InstantMessageDialog.MessageFromAgent) return;

        InstantMessageReceived?.Invoke(this, new InstantMessageEvent(
            e.IM.FromAgentID.Guid, e.IM.FromAgentName, e.IM.Message, e.IM.IMSessionID.Guid));
    }

    private readonly ConcurrentDictionary<Guid, byte> _conferenceSessions = new();

    // Who has spoken in each conference. LibreMetaverse's own member list (GroupChatSessions) fills only from the
    // session's agent-list updates, which a conference we are invited into may never send for the people already
    // in it; everybody who spoke is certainly a member, so the list is the union of the two.
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, byte>> _conferenceSpeakers = new();

    /// <summary>The members of an ad-hoc conference as far as we know: what LibreMetaverse's session table holds, everybody
    /// who spoke, and ourselves. Order is unspecified -- the caller sorts by the name it shows.</summary>
    public IReadOnlyList<Guid> GetConferenceMembers(Guid sessionId)
    {
        var members = new HashSet<Guid>();
        if (_client.Self.GroupChatSessions.TryGetValue(new UUID(sessionId), out var tracked))
        {
            lock (tracked)
                foreach (var m in tracked)
                    if (m.AvatarKey != UUID.Zero) members.Add(m.AvatarKey.Guid);
        }
        if (_conferenceSpeakers.TryGetValue(sessionId, out var speakers))
            foreach (var id in speakers.Keys) members.Add(id);
        if (_conferenceSessions.ContainsKey(sessionId) && _client.Self.AgentID != UUID.Zero)
            members.Add(_client.Self.AgentID.Guid);
        return members.ToList();
    }

    private void OnChatSessionMemberAdded(object? sender, ChatSessionMemberAddedEventArgs e)
    {
        if (_conferenceSessions.ContainsKey(e.SessionID.Guid))
            ConferenceMembersChanged?.Invoke(this, new ConferenceMembersChangedEvent(e.SessionID.Guid));
    }

    private void OnChatSessionMemberLeft(object? sender, ChatSessionMemberLeftEventArgs e)
    {
        Guid session = e.SessionID.Guid;
        if (!_conferenceSessions.ContainsKey(session)) return;
        if (_conferenceSpeakers.TryGetValue(session, out var speakers)) speakers.TryRemove(e.AgentID.Guid, out _);
        ConferenceMembersChanged?.Invoke(this, new ConferenceMembersChangedEvent(session));
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
            // Only a real name: an empty one in the cache looks like "known" to every request that checks for
            // the key, and the group is then never asked for (BUG-UI-21).
            if (!string.IsNullOrWhiteSpace(g.Name)) _nameCache[g.ID.Guid] = g.Name;
            list.Add(GroupProfileMapper.ToEntry(g));
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        _groups = list;
        GroupsUpdated?.Invoke(this, new GroupsUpdatedEvent(list));
    }

    /// <summary>FEAT-UI-29: the group the agent is currently wearing the tag of, or
    /// <see cref="Guid.Empty"/> for "none". Updated from the simulator's own AgentDataUpdate
    /// (LibreMetaverse's <c>AgentDataReply</c>), never guessed from the last
    /// <see cref="ActivateGroup"/> call -- the sim can refuse, and it also sets this at login to
    /// whatever the account already had active.</summary>
    public Guid ActiveGroupId => _client.Self.ActiveGroup.Guid;

    /// <summary>Raised when the active group changes, including the first AgentDataUpdate after
    /// login. Fires on a LibreMetaverse network thread -- marshal before touching the UI.</summary>
    public event EventHandler<ActiveGroupChangedEvent>? ActiveGroupChanged;

    /// <summary>Sets the agent's active group -- the group tag shown over the avatar, and the one
    /// the SIMULATOR evaluates group permissions against.
    ///
    /// <para>That second part is why this is not merely cosmetic. A group-editable object's
    /// <c>FLAGS_OBJECT_MODIFY</c> bit is computed per agent against the agent's ACTIVE group
    /// (<c>LLPermissions::allowOperationBy</c> takes one group, not a membership list), so with no
    /// group active the sim reports no group rights and the viewer correctly shows none. Without
    /// this call SLNG could never exercise group permissions at all -- see FEAT-SEC-04.</para>
    ///
    /// <para><see cref="Guid.Empty"/> clears the tag, which is what SL's "(none)" entry does.
    /// The result arrives on <see cref="ActiveGroupChanged"/>; nothing is assumed to have worked
    /// until it does.</para></summary>
    public void ActivateGroup(Guid groupId)
    {
        if (!_client.Network.Connected) return;
        _client.Groups.ActivateGroup(new LibreMetaverse.UUID(groupId));
    }

    private void OnAgentDataReply(object? sender, AgentDataReplyEventArgs e)
    {
        ActiveGroupChanged?.Invoke(this, new ActiveGroupChangedEvent(
            e.ActiveGroupID.Guid, e.GroupName ?? string.Empty, e.GroupTitle ?? string.Empty));
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

    internal static bool TryParseGroupNoticeBucket(
        byte[]? bucket, out Guid groupId, out bool hasInventory, out int assetType, out string itemName)
    {
        groupId = Guid.Empty;
        hasInventory = false;
        assetType = 0;
        itemName = string.Empty;
        if (bucket == null || bucket.Length < 18) return false;

        hasInventory = bucket[0] != 0;
        assetType = bucket[1];
        groupId = new UUID(bucket, 2).Guid;

        if (bucket.Length > 18)
        {
            int nameLen = 0;
            while (18 + nameLen < bucket.Length && bucket[18 + nameLen] != 0)
                nameLen++;
            if (nameLen > 0)
                itemName = System.Text.Encoding.UTF8.GetString(bucket, 18, nameLen).Trim();
        }

        return groupId != Guid.Empty;
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
        // The grid's own name for the session it just let us into -- for a group, the group's. A free name source
        // (BUG-UI-23), taken only for a session that is one of our groups: a conference's name is not a group's.
        if (e.Success && _groups?.Any(g => g.Id == e.SessionID.Guid) == true)
            RememberGroupName(e.SessionID.Guid, e.SessionName ?? string.Empty);

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
