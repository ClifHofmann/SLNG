using System;
using System.Collections.Generic;
using System.Linq;

namespace SLNG.Core;

/// <summary>Which counted tab a notification belongs in.</summary>
/// <remarks>
/// Four rather than one because the counts are the point of the window: "3 transactions" and
/// "3 things happened" are different sentences, and only the first tells you whether to look.
/// </remarks>
public enum NotificationKind
{
    /// <summary>Simulator alerts and anything the client itself needs to say.</summary>
    System,

    /// <summary>Money in or out.</summary>
    Transaction,

    /// <summary>Offers aimed at you: inventory, teleport lures, friendship.</summary>
    Invitation,

    /// <summary>Group notices and invitations.</summary>
    Group,
}

/// <summary>One line in the notification window.</summary>
/// <param name="Id">Identity for dismissal, and for filling in a name that arrives late.</param>
/// <param name="SenderId">Whoever it came from, or empty. Kept so the entry can show a portrait
/// and so <see cref="NotificationStore.ResolveSender"/> can fix the text once the name is
/// known.</param>
/// <param name="Text">The finished line, in the user's language.</param>
/// <param name="Detail">The longer version behind the expander, or empty when there is none.</param>
/// <param name="ReceivedUtc">When it arrived.</param>
/// <param name="Read">Whether the user has actually looked at the tab this sits in. Drives the
/// badge on the toolbar button — the whole reason to distinguish it from "still in the list".</param>
/// <param name="SenderName">The sender's name exactly as it appears inside <paramref name="Text"/>,
/// so the window can make that one word a link without guessing where the name ends. Kept in step
/// by <see cref="NotificationStore.ResolveSender"/> when a name arrives late.</param>
/// <param name="SenderIsGroup">A group, not a person. Groups get no profile link — opening an
/// avatar profile for a group id would be a wrong window, not a missing one.</param>
/// <param name="ActionKey">Non-empty when this entry still has an UNANSWERED decision behind it,
/// and the window should offer a way back to it. Deliberately an opaque id rather than a delegate
/// or a payload: <c>SLNG.Core</c> must not learn what a group invitation is, let alone how to open
/// a window for one. The owner keys its own payload by this and does the opening.
///
/// <para>Only a prompt that can be left UNANSWERED gets one. A group invitation and an inventory
/// offer send nothing when dismissed with the title-bar ×, so they stay open questions; a script
/// permission request refuses on dismissal and is therefore always settled, and giving it an
/// action would offer to re-ask something already answered.</para>
///
/// <para>It must be cleared the moment the decision is made — see
/// <see cref="NotificationStore.CompleteAction"/>. An entry that re-opens an invitation the user
/// already declined is worse than one that does nothing: it invites an answer the simulator will
/// not take.</para></param>
public sealed record NotificationEntry(
    Guid Id,
    NotificationKind Kind,
    Guid SenderId,
    string Text,
    string Detail,
    DateTime ReceivedUtc,
    bool Read = false,
    string SenderName = "",
    bool SenderIsGroup = false,
    Guid ActionKey = default);

/// <summary>
/// What the notification window shows: entries, per-kind counts, and dismissal.
/// </summary>
/// <remarks>
/// Engine-free so the counting and dismissal rules can be tested without a scene. They are worth
/// testing: a count that drifts away from the list is the specific way this kind of window goes
/// wrong, and it goes wrong quietly — dismiss one entry, then dismiss all, and a naive
/// implementation subtracts twice and shows "Transaktionen (-1)".
/// </remarks>
public sealed class NotificationStore
{
    /// <summary>
    /// The oldest entries are dropped past this. A viewer left running for a day should not grow
    /// a list without end, and nobody scrolls back a thousand notices.
    /// </summary>
    public const int MaxEntries = 200;

    private readonly List<NotificationEntry> _entries = new();

    /// <summary>Raised whenever the list or the counts changed, so the window can redraw. Never
    /// raised for a no-op dismissal.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised for a NEW entry only.</summary>
    /// <remarks>
    /// Separate from <see cref="Changed"/> because a toast must appear when something arrives and
    /// must NOT reappear when something is dismissed — and dismissal changes the list too.
    /// </remarks>
    public event EventHandler<NotificationEntry>? Added;

    /// <summary>Newest first, which is the order the window shows them in.</summary>
    public IReadOnlyList<NotificationEntry> Entries => _entries;

    public int Count => _entries.Count;

    /// <summary>How many entries a tab holds.</summary>
    public int CountOf(NotificationKind kind) => _entries.Count(e => e.Kind == kind);

    /// <summary>Adds an entry and returns it. Newest goes to the front.</summary>
    public NotificationEntry Add(
        NotificationKind kind, Guid senderId, string text, string detail = "",
        string senderName = "", bool senderIsGroup = false, DateTime? receivedUtc = null,
        Guid actionKey = default)
    {
        var entry = new NotificationEntry(
            Guid.NewGuid(), kind, senderId, text ?? string.Empty, detail ?? string.Empty,
            receivedUtc ?? DateTime.UtcNow, Read: false,
            SenderName: senderName ?? string.Empty, SenderIsGroup: senderIsGroup,
            ActionKey: actionKey);

        _entries.Insert(0, entry);

        // Trim from the END, which is the oldest -- the list is newest-first.
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);

        Changed?.Invoke(this, EventArgs.Empty);
        Added?.Invoke(this, entry);
        return entry;
    }

    /// <summary>
    /// Marks the decision behind <paramref name="actionKey"/> as made: the entry keeps its place
    /// in the list but loses its way back, and optionally says what was decided. Returns how many
    /// entries changed.
    /// </summary>
    /// <remarks>
    /// The load-bearing half of <see cref="NotificationEntry.ActionKey"/>. Without it the window
    /// would keep offering to re-open an invitation that has already been accepted or declined,
    /// and the simulator would refuse the second answer — a button that looks alive and is not.
    ///
    /// <para>It does NOT dismiss the entry. The record of "you were invited to X and joined" is
    /// worth more than the empty line left behind by removing it, and dismissal stays the user's
    /// decision everywhere else in this window.</para>
    ///
    /// <para>An empty key matches nothing, so a caller that lost track of its key cannot
    /// accidentally strip the action off every entry that never had one.</para>
    /// </remarks>
    public int CompleteAction(Guid actionKey, string? newText = null)
    {
        if (actionKey == Guid.Empty) return 0;

        int changed = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.ActionKey != actionKey) continue;

            _entries[i] = e with
            {
                ActionKey = Guid.Empty,
                Text = string.IsNullOrEmpty(newText) ? e.Text : newText,
            };
            changed++;
        }

        if (changed > 0) Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    /// <summary>How many entries the user has not looked at, in one tab or in total.</summary>
    public int UnreadOf(NotificationKind kind) => _entries.Count(e => e.Kind == kind && !e.Read);

    /// <summary>Total unread, for the badge on the toolbar button.</summary>
    public int UnreadCount => _entries.Count(e => !e.Read);

    /// <summary>
    /// Marks one tab, or everything, as seen. Returns how many entries changed.
    /// </summary>
    /// <remarks>
    /// Per TAB rather than wholesale, because the window shows one tab at a time: opening it on
    /// Transactions must not silently clear the badge for three unread group notices the user has
    /// not looked at.
    /// </remarks>
    public int MarkRead(NotificationKind? kind = null)
    {
        int changed = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.Read || (kind is { } k && e.Kind != k)) continue;

            _entries[i] = e with { Read = true };
            changed++;
        }

        if (changed > 0) Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    /// <summary>Removes one entry. Returns false if it was already gone, and raises nothing —
    /// dismissing the same entry twice must not be able to move a count.</summary>
    public bool Dismiss(Guid id)
    {
        int index = _entries.FindIndex(e => e.Id == id);
        if (index < 0) return false;

        _entries.RemoveAt(index);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Clears one tab, or everything when <paramref name="kind"/> is null. Returns how
    /// many went.</summary>
    public int DismissAll(NotificationKind? kind = null)
    {
        int removed = kind is { } k ? _entries.RemoveAll(e => e.Kind == k) : _entries.RemoveAll(_ => true);
        if (removed > 0) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>
    /// Rewrites every entry from <paramref name="senderId"/> whose text still carries
    /// <paramref name="placeholder"/>, once that person's name is known.
    /// </summary>
    /// <remarks>
    /// Names arrive asynchronously, and a payment is announced the moment it lands. Without this
    /// the window keeps saying "Jemand hat dir L$ 2200 bezahlt" for the rest of the session —
    /// which is the one thing the entry exists to answer.
    /// </remarks>
    public int ResolveSender(Guid senderId, string placeholder, string name)
    {
        if (senderId == Guid.Empty || string.IsNullOrEmpty(placeholder) || string.IsNullOrEmpty(name)) return 0;

        int changed = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.SenderId != senderId || !e.Text.Contains(placeholder, StringComparison.Ordinal)) continue;

            _entries[i] = e with
            {
                Text = e.Text.Replace(placeholder, name, StringComparison.Ordinal),
                // In step with the text, or the window would look for the placeholder to link and
                // find the real name sitting there instead.
                SenderName = name,
            };
            changed++;
        }

        if (changed > 0) Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }
}
