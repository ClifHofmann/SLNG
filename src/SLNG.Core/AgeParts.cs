using System;

namespace SLNG.Core;

/// <summary>
/// An account's age as whole calendar years, months and days -- "6 years, 4 months, 12 days" -- for
/// the radar's age tooltip (FEAT-UI-39). The table cell shows total days, which sorts and compares;
/// this is the same age said the way a person says it. Calendar-based on purpose: "a year" is the
/// next anniversary, not 365 days, so a leap year does not push the birthday around.
/// </summary>
/// <param name="Years">Whole years since the account was created.</param>
/// <param name="Months">Whole months after those years, 0 to 11.</param>
/// <param name="Days">Days after those months.</param>
public readonly record struct AgeParts(int Years, int Months, int Days)
{
    /// <summary>The span between two moments, counting dates only (the time of day is ignored, as a
    /// birthday is a date). A <paramref name="nowUtc"/> before <paramref name="bornUtc"/> -- a
    /// skewed clock -- is zero rather than negative.</summary>
    public static AgeParts Between(DateTime bornUtc, DateTime nowUtc)
    {
        var born = bornUtc.Date;
        var now = nowUtc.Date;
        if (now <= born) return new AgeParts(0, 0, 0);

        int years = now.Year - born.Year;
        // AddYears / AddMonths clamp a missing day (29 Feb, 31st) to the month's last, so "the
        // anniversary" is always a real date.
        if (born.AddYears(years) > now) years--;
        var afterYears = born.AddYears(years);

        int months = (now.Year - afterYears.Year) * 12 + now.Month - afterYears.Month;
        if (afterYears.AddMonths(months) > now) months--;
        var afterMonths = afterYears.AddMonths(months);

        return new AgeParts(years, months, (int)(now - afterMonths).TotalDays);
    }
}
