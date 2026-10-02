using System.Globalization;
using SLNG.Core.ChatLogs;
using Xunit;

namespace SLNG.Core.Tests.ChatLogs;

// FEAT-UI-41. Firestorm stamps its chat logs in Second Life (Pacific) time, not local time. The real
// evidence: a group line stamped 08:07 sits beside a conversation.log entry of 15:07:59 UTC
// (epoch 1775920079) on a machine in Germany -- UTC-7, i.e. PDT.
public sealed class SecondLifeTimeTests
{
    [Fact]
    public void The_real_group_line_stamp_is_pacific_daylight_time()
    {
        var utc = DateTimeOffset.FromUnixTimeSeconds(1775920079).UtcDateTime; // 2026-04-11 15:07:59 UTC

        Assert.Equal("2026/04/11 08:07", SecondLifeTime.FromUtc(utc).ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_real_osgrid_conversation_is_pacific_daylight_time_too()
    {
        var utc = DateTimeOffset.FromUnixTimeSeconds(1782735928).UtcDateTime; // 2026-06-29 12:25:28 UTC

        Assert.Equal("05:25", SecondLifeTime.FromUtc(utc).ToString("HH:mm"));
    }

    [Fact]
    public void Winter_is_eight_hours_behind()
    {
        Assert.Equal(new DateTime(2026, 1, 15, 12, 0, 0), SecondLifeTime.FromUtc(new DateTime(2026, 1, 15, 20, 0, 0, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData(2026, 3, 8, 9, 59, false)]   // second Sunday of March, 01:59 PST
    [InlineData(2026, 3, 8, 10, 0, true)]    // 02:00 PST becomes 03:00 PDT
    [InlineData(2026, 11, 1, 8, 59, true)]   // first Sunday of November, 01:59 PDT
    [InlineData(2026, 11, 1, 9, 0, false)]   // 02:00 PDT becomes 01:00 PST
    public void Summer_time_switches_at_two_in_the_morning_pacific(int y, int mo, int d, int h, int mi, bool daylight)
    {
        Assert.Equal(daylight, SecondLifeTime.IsPacificDaylight(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void A_local_time_is_converted_through_the_zone_it_was_written_in()
    {
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");

        // 18:58 at UTC+2 is 16:58 UTC, which is 09:58 PDT.
        Assert.Equal(new DateTime(2026, 9, 12, 9, 58, 0), SecondLifeTime.FromZone(new DateTime(2026, 9, 12, 18, 58, 0), plusTwo));
    }
}
