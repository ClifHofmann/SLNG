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
/// <param name="ReceivedUtc">When it arrived. Rendered in grid time by the window.</param>
public sealed record NotificationEntry(
    Guid Id,
    NotificationKind Kind,
    Guid SenderId,
    string Text,
    string Detail,
    DateTime ReceivedUtc);

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

    /// <summary>Newest first, which is the order the window shows them in.</summary>
    public IReadOnlyList<NotificationEntry> Entries => _entries;

    public int Count => _entries.Count;

    /// <summary>How many entries a tab holds.</summary>
    public int CountOf(NotificationKind kind) => _entries.Count(e => e.Kind == kind);

    /// <summary>Adds an entry and returns it. Newest goes to the front.</summary>
    public NotificationEntry Add(NotificationKind kind, Guid senderId, string text, string detail = "", DateTime? receivedUtc = null)
    {
        var entry = new NotificationEntry(
            Guid.NewGuid(), kind, senderId, text ?? string.Empty, detail ?? string.Empty,
            receivedUtc ?? DateTime.UtcNow);

        _entries.Insert(0, entry);

        // Trim from the END, which is the oldest -- the list is newest-first.
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);

        Changed?.Invoke(this, EventArgs.Empty);
        return entry;
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

            _entries[i] = e with { Text = e.Text.Replace(placeholder, name, StringComparison.Ordinal) };
            changed++;
        }

        if (changed > 0) Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }
}
