using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using SLNG.Core;

namespace SLNG.Net;

// GridSession, BriefProfiles part (FEAT-UI-39).
//
// What a nearby-people list needs per avatar beyond name and position: the logged-in user's private
// note on them, the "$" / "$$" payment-info status and the account age. All of it lives in the
// avatar's profile, so it is fetched once per avatar and cached.
//
// Two transports, one result:
//   * the AgentProfile capability (Second Life, and any grid that offers it) -- one HTTP GET returns
//     everything, notes included;
//   * the legacy UDP pair (OpenSim without the cap) -- AvatarPropertiesRequest answers with the
//     properties (flags + born-on) and AvatarNotesRequest with the note, as two SEPARATE replies.
// Both end in MergeBriefProfile, which merges field by field so that neither reply can wipe what
// the other one filled.
public sealed partial class GridSession
{
    /// <summary>How long a complete profile is trusted before a plain request asks the grid again.
    /// A note can be edited in another viewer, so "forever" would be wrong; Firestorm re-asks on a
    /// similar scale.</summary>
    private static readonly TimeSpan BriefProfileFreshFor = TimeSpan.FromMinutes(10);

    /// <summary>After a failed AgentProfile fetch, how long before the same avatar is tried again.</summary>
    private static readonly TimeSpan BriefProfileFailureBackoff = TimeSpan.FromSeconds(60);

    /// <summary>After a UDP request pair, how long before the same avatar is asked again -- the UDP
    /// path has no completion signal, so this is all that stops a per-frame caller from flooding.</summary>
    private static readonly TimeSpan BriefProfileUdpResendBackoff = TimeSpan.FromSeconds(30);

    /// <summary>At most this many AgentProfile fetches run at once; the rest wait their turn.</summary>
    private const int BriefProfileMaxInFlight = 3;

    /// <summary>When the back-off map reaches this size, expired entries are dropped on the next write.</summary>
    private const int BriefProfileBackoffPruneAt = 4096;

    /// <summary>A cached profile and when it was last written.</summary>
    private sealed class BriefProfileEntry
    {
        public BriefProfileEntry(AvatarBriefProfile profile, DateTime updatedUtc)
        {
            Profile = profile;
            UpdatedUtc = updatedUtc;
        }

        public AvatarBriefProfile Profile { get; }
        public DateTime UpdatedUtc { get; }
    }

    // A class (not a record) on purpose: ConcurrentDictionary.TryUpdate compares the old value with
    // the default equality, which for a class is reference identity -- exactly the compare-and-swap
    // MergeBriefProfile wants.
    private readonly ConcurrentDictionary<Guid, BriefProfileEntry> _briefProfiles = new();
    private readonly ConcurrentDictionary<Guid, byte> _briefInFlight = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _briefRetryAfter = new();
    private readonly SemaphoreSlim _briefGate = new(BriefProfileMaxInFlight, BriefProfileMaxInFlight);

    /// <summary>Raised on a NETWORK thread whenever the cached entry for an avatar changes, including
    /// after <see cref="SetAvatarNote"/>. Never raised for a merge that changes nothing. The argument
    /// is the entry as it was right after that merge; marshal to the main thread before touching UI.</summary>
    public event EventHandler<AvatarBriefProfile>? BriefProfileUpdated;

    /// <summary>The cached note / payment status / account age for an avatar. Reads the cache only:
    /// never blocks and never sends -- call <see cref="RequestBriefProfile"/> to fill it. False until
    /// the first reply for that avatar has arrived.</summary>
    public bool TryGetBriefProfile(Guid agentId, [MaybeNullWhen(false)] out AvatarBriefProfile profile)
    {
        if (_briefProfiles.TryGetValue(agentId, out var entry))
        {
            profile = entry.Profile;
            return true;
        }

        profile = null;
        return false;
    }

    /// <summary>
    /// Asks the grid for an avatar's note, payment-info status and account age. Fire-and-forget: the
    /// answer lands in the cache and raises <see cref="BriefProfileUpdated"/>.
    ///
    /// <para>Without <paramref name="force"/> this is a no-op when the avatar is us, when a complete
    /// entry younger than ten minutes exists, when a request for the same avatar is already running,
    /// or when that avatar failed recently. <paramref name="force"/> skips the freshness and
    /// back-off checks (the Notes tab wants the current note NOW) but still never doubles up on a
    /// request that is already running -- its answer is just as current.</para>
    ///
    /// <para>Safe to call every frame from the main thread: the checks are dictionary lookups, the
    /// HTTP work runs on the thread pool and at most three fetches run at once.</para>
    /// </summary>
    public void RequestBriefProfile(Guid agentId, bool force = false)
    {
        if (agentId == Guid.Empty || !_client.Network.Connected) return;
        if (agentId == _client.Self.AgentID.Guid) return;

        try
        {
            var now = DateTime.UtcNow;
            if (!ShouldRequestBriefProfile(agentId, force, now)) return;

            var id = new UUID(agentId);
            if (!_client.Avatars.AgentProfileAvailable())
            {
                // OpenSim without the AgentProfile cap: two UDP requests, two separate replies
                // (OnAvatarPropertiesReply / OnAvatarNotesReply). Nothing tells us when they are
                // done, so only the back-off guards against re-sending.
                SetBriefRetryAfter(agentId, now + BriefProfileUdpResendBackoff, now);
                _client.Avatars.RequestAvatarProperties(id);
                _client.Avatars.RequestAvatarNotes(id);
                return;
            }

            if (!_briefInFlight.TryAdd(agentId, 0)) return;
            _ = Task.Run(() => FetchBriefProfileViaCapAsync(agentId));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BriefProfile] request for {agentId} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the logged-in user's private note on an avatar to the grid (LibreMetaverse picks the
    /// capability or the UDP packet itself) and updates the cache straight away, raising
    /// <see cref="BriefProfileUpdated"/> -- the window that edited the note does not wait for a round
    /// trip to see it. A no-op while not connected.
    /// </summary>
    public void SetAvatarNote(Guid agentId, string notes)
    {
        if (agentId == Guid.Empty || !_client.Network.Connected) return;

        notes ??= string.Empty;
        _client.Self.UpdateProfileNotes(new UUID(agentId), notes);
        MergeBriefProfile(agentId, p => p with { Notes = notes });
    }

    /// <summary>Whether <see cref="RequestBriefProfile"/> should send for this avatar right now. The
    /// connection and own-agent checks stay in the caller; this is the part a test can drive.</summary>
    internal bool ShouldRequestBriefProfile(Guid agentId, bool force, DateTime nowUtc)
    {
        if (agentId == Guid.Empty) return false;
        if (_briefInFlight.ContainsKey(agentId)) return false;
        if (force) return true;

        if (_briefProfiles.TryGetValue(agentId, out var entry)
            && entry.Profile.Notes != null
            && entry.Profile.Payment != PaymentInfo.Unknown
            && nowUtc - entry.UpdatedUtc < BriefProfileFreshFor)
            return false;

        if (_briefRetryAfter.TryGetValue(agentId, out var retryAfter) && nowUtc < retryAfter)
            return false;

        return true;
    }

    /// <summary>Records that this avatar must not be asked again before <paramref name="until"/>.</summary>
    internal void SetBriefRetryAfter(Guid agentId, DateTime until, DateTime nowUtc)
    {
        if (_briefRetryAfter.Count >= BriefProfileBackoffPruneAt)
        {
            foreach (var kv in _briefRetryAfter)
                if (kv.Value <= nowUtc) _briefRetryAfter.TryRemove(kv.Key, out _);
        }

        _briefRetryAfter[agentId] = until;
    }

    /// <summary>
    /// The one place the cache is written. <paramref name="update"/> receives the current entry (an
    /// empty profile if there is none) and returns the new one, so each caller changes only the
    /// fields its reply actually carries -- a notes reply never touches the payment status and the
    /// other way round. The result is stored with a fresh timestamp; the event is raised afterwards,
    /// outside any lock, and only when the value really changed (record equality).
    ///
    /// <para>A lock-free compare-and-swap loop, so concurrent merges for one avatar (a thread-pool
    /// HTTP reply and a network-thread UDP reply) cannot lose each other's fields. <paramref
    /// name="update"/> may therefore run more than once and must be free of side effects.</para>
    /// </summary>
    internal AvatarBriefProfile MergeBriefProfile(Guid agentId, Func<AvatarBriefProfile, AvatarBriefProfile> update)
    {
        var now = DateTime.UtcNow;
        AvatarBriefProfile merged;
        bool changed;
        while (true)
        {
            if (_briefProfiles.TryGetValue(agentId, out var current))
            {
                merged = update(current.Profile) with { AgentId = agentId };
                if (!_briefProfiles.TryUpdate(agentId, new BriefProfileEntry(merged, now), current)) continue;
                changed = !merged.Equals(current.Profile);
                break;
            }

            var empty = new AvatarBriefProfile(agentId);
            merged = update(empty) with { AgentId = agentId };
            // Nothing learnt: do not invent an entry (TryGetBriefProfile must stay false) or an event.
            if (merged.Equals(empty)) return merged;
            if (!_briefProfiles.TryAdd(agentId, new BriefProfileEntry(merged, now))) continue;
            changed = true;
            break;
        }

        if (changed) BriefProfileUpdated?.Invoke(this, merged);
        return merged;
    }

    /// <summary>
    /// Lays a complete reply (from the AgentProfile cap) over what is cached: a field the reply
    /// carries wins, a field it does not carry (null note, unknown payment, no date) keeps its old
    /// value. <c>AgeHidden</c> is taken from the reply -- only the capability knows it.
    /// </summary>
    internal static AvatarBriefProfile OverlayBriefProfile(AvatarBriefProfile old, AvatarBriefProfile incoming) =>
        old with
        {
            Notes = incoming.Notes ?? old.Notes,
            Payment = incoming.Payment != PaymentInfo.Unknown ? incoming.Payment : old.Payment,
            BornOnUtc = incoming.BornOnUtc ?? old.BornOnUtc,
            AgeHidden = incoming.AgeHidden,
        };

    /// <summary>Maps the AgentProfile capability's answer to the neutral DTO. A missing note becomes
    /// "" (known empty, not "not arrived"); the payment status follows the profile's Identified /
    /// Transacted flags with transacted winning; a default/MinValue member-since means "unknown".</summary>
    internal static AvatarBriefProfile ToBriefProfile(AgentProfileMessage m)
    {
        var identified = (m.Flags & ProfileFlags.Identified) != 0;
        var transacted = (m.Flags & ProfileFlags.Transacted) != 0;

        DateTime? born = null;
        if (m.MemberSince != default && m.MemberSince != DateTime.MinValue)
        {
            born = m.MemberSince.Kind switch
            {
                DateTimeKind.Utc => m.MemberSince,
                DateTimeKind.Local => m.MemberSince.ToUniversalTime(),
                _ => DateTime.SpecifyKind(m.MemberSince, DateTimeKind.Utc),
            };
        }

        return new AvatarBriefProfile(
            m.AvatarID.Guid,
            m.Notes ?? string.Empty,
            AvatarBriefProfile.PaymentFrom(identified, transacted),
            born,
            m.HideAge == true);
    }

    private async Task FetchBriefProfileViaCapAsync(Guid agentId)
    {
        var gateHeld = false;
        try
        {
            await _briefGate.WaitAsync().ConfigureAwait(false);
            gateHeld = true;

            var (success, profile) = await _client.Avatars.RequestAgentProfileAsync(new UUID(agentId))
                .ConfigureAwait(false);
            if (success && profile != null)
            {
                _briefRetryAfter.TryRemove(agentId, out _);
                var incoming = ToBriefProfile(profile);
                MergeBriefProfile(agentId, old => OverlayBriefProfile(old, incoming));
            }
            else
            {
                var now = DateTime.UtcNow;
                SetBriefRetryAfter(agentId, now + BriefProfileFailureBackoff, now);
            }
        }
        catch (Exception ex)
        {
            var now = DateTime.UtcNow;
            SetBriefRetryAfter(agentId, now + BriefProfileFailureBackoff, now);
            Console.Error.WriteLine($"[BriefProfile] AgentProfile fetch for {agentId} failed: {ex.Message}");
        }
        finally
        {
            if (gateHeld) _briefGate.Release();
            _briefInFlight.TryRemove(agentId, out _);
        }
    }

    /// <summary>UDP path: the note arrives in its own reply, separate from the properties. Network thread.</summary>
    private void OnAvatarNotesReply(object? sender, AvatarNotesReplyEventArgs e)
    {
        var notes = e.Notes ?? string.Empty;
        MergeBriefProfile(e.AvatarID.Guid, p => p with { Notes = notes });
    }
}
