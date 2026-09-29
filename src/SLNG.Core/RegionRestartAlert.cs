namespace SLNG.Core;

/// <summary>
/// FEAT-UI-34: turns the two pieces of a region-restart alert the simulator sends into a
/// <see cref="RegionRestartEvent"/>.
///
/// <para>The simulator announces a restart as an <c>AlertMessage</c> whose notification id is
/// <c>RegionRestartMinutes</c> or <c>RegionRestartSeconds</c>, with a small LLSD block carrying
/// <c>MINUTES</c> or <c>SECONDS</c> and <c>NAME</c> (llviewermessage.cpp:5127-5159). Minutes are
/// multiplied by 60, exactly as the reference viewer does. The LLSD stays inside
/// <c>SLNG.Net</c>; this class takes the already-extracted primitives so the rule can be tested
/// without a protocol library.</para>
/// </summary>
public static class RegionRestartAlert
{
    public const string MinutesNotificationId = "RegionRestartMinutes";
    public const string SecondsNotificationId = "RegionRestartSeconds";

    /// <summary>A day. Far past any real restart notice; it only exists so a garbage value cannot
    /// overflow the minutes multiplication or park a countdown at "999999:59".</summary>
    public const int MaxSeconds = 24 * 60 * 60;

    /// <summary>Whether this notification id announces a region restart at all.</summary>
    public static bool IsRestartNotification(string? notificationId) =>
        notificationId == MinutesNotificationId || notificationId == SecondsNotificationId;

    /// <summary>Builds the event, or returns false when the id is not a restart notice.</summary>
    /// <param name="minutes">The <c>MINUTES</c> value; read only for the minutes notice.</param>
    /// <param name="seconds">The <c>SECONDS</c> value; read only for the seconds notice.</param>
    /// <remarks>A restart notice whose number is missing is still a restart notice, so it comes
    /// out as zero seconds rather than being dropped: an imminent-restart warning that silently
    /// vanishes is worse than one that reads "0:00". The reference viewer does the same
    /// (<c>asInteger()</c> of an absent key is 0).</remarks>
    public static bool TryCreate(string? notificationId, string? regionName, long? minutes, long? seconds,
                                 out RegionRestartEvent restart)
    {
        restart = new RegionRestartEvent(string.Empty, 0);
        if (!IsRestartNotification(notificationId)) return false;

        long total = notificationId == MinutesNotificationId
            ? (minutes ?? 0) * 60
            : seconds ?? 0;

        restart = new RegionRestartEvent(
            regionName ?? string.Empty,
            (int)Math.Clamp(total, 0, MaxSeconds));
        return true;
    }
}
