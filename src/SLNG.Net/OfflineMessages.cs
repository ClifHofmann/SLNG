namespace SLNG.Net;

/// <summary>Helpers for instant messages the grid stored while we were offline (BUG-NET-35).</summary>
public static class OfflineMessages
{
    /// <summary>When the sender sent a message, in UTC, or <c>null</c> when the grid gave no time.
    /// LibreMetaverse builds <c>InstantMessage.Timestamp</c> as <c>new DateTime(unixSeconds)</c> -- the Unix
    /// seconds taken as .NET ticks, so a message from 2026 arrives as a date in January of the year 1. The
    /// seconds are still in <see cref="DateTime.Ticks"/>; a value that already looks like a real date (a
    /// fixed library) is used as it is. A live IM carries 0, which is "no time", not 1970.</summary>
    public static DateTime? SentUtc(DateTime timestamp)
    {
        if (timestamp.Year >= 1971) return DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
        if (timestamp.Ticks <= 0) return null;
        // Ticks hold seconds here; refuse a count that would leave the DateTime range.
        long seconds = timestamp.Ticks;
        if (seconds > (DateTime.MaxValue - DateTime.UnixEpoch).TotalSeconds) return null;
        return DateTime.UnixEpoch.AddSeconds(seconds);
    }
}
