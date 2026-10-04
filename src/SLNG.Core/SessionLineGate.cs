namespace SLNG.Core;

/// <summary>
/// Drops the second copy of a session chat line (group or conference) that the same sender sent with the
/// same text a moment earlier. The grid can deliver one session line through more than one path (the
/// invitation event and the plain instant message), and the second copy then shows up as a duplicate
/// line. The price is that a person who deliberately sends the same text twice inside
/// <see cref="Window"/> sees it once; that is far rarer than the duplicate.
/// Not thread-safe on its own; <c>GridSession</c> calls it under a lock.
/// </summary>
public sealed class SessionLineGate
{
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(1500);

    private readonly Dictionary<(Guid Session, Guid From), (string Text, long Ticks)> _last = new();

    /// <summary>True when this is a repeat of the sender's previous line in the session, inside the window.
    /// Records the line either way.</summary>
    public bool IsDuplicate(Guid sessionId, Guid fromId, string text, long nowTicks)
    {
        var key = (sessionId, fromId);
        bool duplicate = _last.TryGetValue(key, out var prev)
                         && prev.Text == text
                         && nowTicks - prev.Ticks >= 0
                         && nowTicks - prev.Ticks < Window.Ticks;
        _last[key] = (text, nowTicks);
        if (_last.Count > 512) _last.Clear(); // a few hundred senders at most matter; never grow without bound
        return duplicate;
    }
}
