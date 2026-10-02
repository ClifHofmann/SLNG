namespace SLNG.Core.ChatLogs;

/// <summary>
/// FEAT-UI-41: "SLT", the clock Firestorm writes into chat logs. The viewer's log timestamp is
/// built from <c>[TimeHour]:[TimeMin]</c> and friends, and every one of those strings is
/// <c>…,datetime,slt</c> (<c>xui/en/language_settings.xml</c>), i.e. Pacific time -- NOT the
/// machine's local time. Verified against real files: a group line stamped <c>08:07</c> sits next
/// to a <c>conversation.log</c> entry of 15:07 UTC the same day (PDT, UTC-7), on a machine in
/// Germany.
///
/// <para>The viewer learns summer time from the login response (<c>daylight_savings</c>,
/// <c>llstartup.cpp</c> -> <c>LLStringOps::setupDatetimeInfo</c>): PDT is 7 hours behind UTC, PST
/// 8. SLNG does not read that flag, so this applies the US rule that Linden Lab's own grid follows:
/// summer time from the second Sunday of March 02:00 PST to the first Sunday of November 02:00 PDT.
/// What an OpenSim grid puts in that flag was not checked; one real OSGrid file agrees with the
/// US rule.</para>
/// </summary>
public static class SecondLifeTime
{
    /// <summary>True while Pacific daylight time (UTC-7) is in force at this UTC instant.</summary>
    public static bool IsPacificDaylight(DateTime utc)
    {
        int year = utc.Year;
        // 02:00 PST is 10:00 UTC; 02:00 PDT is 09:00 UTC.
        DateTime start = NthSunday(year, 3, 2).AddHours(10);
        DateTime end = NthSunday(year, 11, 1).AddHours(9);
        DateTime t = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
        return t >= start && t < end;
    }

    /// <summary>The Pacific wall-clock time for a UTC instant (Kind is Unspecified, as a log line
    /// carries no zone).</summary>
    public static DateTime FromUtc(DateTime utc)
    {
        DateTime t = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
        return t.AddHours(IsPacificDaylight(utc) ? -7 : -8);
    }

    /// <summary>Same, for any <see cref="DateTime"/>: Utc is taken as UTC, Local and Unspecified as
    /// the machine's local time.</summary>
    public static DateTime FromAny(DateTime time)
        => FromUtc(time.Kind == DateTimeKind.Utc ? time : time.ToUniversalTime());

    /// <summary>For a time read from an older SLNG log, which was written in the machine's local
    /// time: converts it through <paramref name="zone"/>. A time that does not exist in that zone
    /// (the hour skipped by a clock change) is read as standard time rather than failing.</summary>
    public static DateTime FromZone(DateTime wallClock, TimeZoneInfo zone)
    {
        DateTime unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        DateTime utc;
        try { utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, zone); }
        catch (ArgumentException) { utc = unspecified - zone.BaseUtcOffset; }
        return FromUtc(utc);
    }

    private static DateTime NthSunday(int year, int month, int n)
    {
        var first = new DateTime(year, month, 1);
        int toSunday = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(toSunday + 7 * (n - 1));
    }
}
