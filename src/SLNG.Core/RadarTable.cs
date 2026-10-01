using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SLNG.Core;

/// <summary>
/// The pure logic behind the nearby-people table (FEAT-UI-39): filtering, sorting and turning a
/// row into the text of a cell. No UI, no clock of its own (age takes <c>utcNow</c>), so every rule
/// is checked by a test rather than by looking at a window.
/// </summary>
public static class RadarTable
{
    /// <summary>Rows whose name contains <paramref name="text"/>, ignoring case. Blank or
    /// whitespace-only means "no filter". The text is NOT trimmed otherwise: "john " is a legitimate
    /// way to ask for "John Smith" and not "Johnny".</summary>
    public static List<RadarRow> Filter(IEnumerable<RadarRow> rows, string? text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (string.IsNullOrWhiteSpace(text)) return rows.ToList();
        return rows.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// A sorted copy of <paramref name="rows"/>. Stable, and equal values fall back to name and then
    /// agent id, so two refreshes of the same data never shuffle rows that merely tie -- the
    /// table would flicker otherwise, since most columns are full of ties (everyone is "in region").
    /// The tie-break always runs A to Z, whichever way the primary column is sorted.
    ///
    /// A value that is not known yet (no voice level, no age, no profile for payment or note) sorts
    /// LAST in both directions. Descending is "largest first", and an unknown is not the smallest
    /// value, it is no value; letting it lead a descending sort would put the rows we know least
    /// about at the top.
    /// </summary>
    public static List<RadarRow> Sort(IEnumerable<RadarRow> rows, RadarColumn column, bool ascending, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // Language has no data behind it and is not sortable: keep the order we were given.
        if (column == RadarColumn.Language) return rows.ToList();

        Comparison<RadarRow> primary = column switch
        {
            RadarColumn.Name => (a, b) => ascending
                ? string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
                : string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase),
            RadarColumn.Voice => (a, b) => CompareKnownFirst(a.VoiceLevel, b.VoiceLevel, ascending),
            RadarColumn.InRegion => (a, b) => CompareValues(a.InSameRegion, b.InSameRegion, ascending),
            RadarColumn.Typing => (a, b) => CompareValues(a.IsTyping, b.IsTyping, ascending),
            RadarColumn.Sitting => (a, b) => CompareValues(a.IsSitting, b.IsSitting, ascending),
            RadarColumn.Payment => (a, b) => CompareKnownFirst(PaymentKey(a), PaymentKey(b), ascending),
            RadarColumn.Note => (a, b) => CompareKnownFirst(NoteKey(a), NoteKey(b), ascending),
            RadarColumn.Age => (a, b) => CompareKnownFirst(a.Profile?.AgeInDays(utcNow), b.Profile?.AgeInDays(utcNow), ascending),
            RadarColumn.Seen => (a, b) => CompareValues(a.SeenFor, b.SeenFor, ascending),
            RadarColumn.Range => (a, b) => CompareValues(a.Distance, b.Distance, ascending),
            _ => (_, _) => 0,
        };

        var comparer = Comparer<RadarRow>.Create((a, b) =>
        {
            var c = primary(a, b);
            if (c != 0) return c;
            c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.AgentId.CompareTo(b.AgentId);
        });

        // OrderBy is a stable sort, so rows equal on every key keep their input order.
        return rows.OrderBy(r => r, comparer).ToList();
    }

    /// <summary>The range cell: the distance to two decimals, culture-independent ("39.78"). When the
    /// height is not known the distance is only a lower bound, so the cell says so with a leading "&gt;"
    /// and shows at least the far clip -- an avatar we can only place coarsely is at least that far,
    /// not "0.00".</summary>
    public static string FormatRange(RadarRow row, float farClipMetres)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.HeightKnown
            ? row.Distance.ToString("F2", CultureInfo.InvariantCulture)
            : ">" + MathF.Max(row.Distance, farClipMetres).ToString("F2", CultureInfo.InvariantCulture);
    }

    /// <summary>The seen cell as h:mm:ss. Hours are not padded and never roll over into days -- 25 hours
    /// reads "25:00:00" -- and a negative span (a clock step) reads as zero rather than a minus sign.
    /// Fractions of a second are dropped, not rounded, so the display never runs ahead of the clock.</summary>
    public static string FormatSeen(TimeSpan seen)
    {
        var total = seen < TimeSpan.Zero ? 0L : (long)seen.TotalSeconds;
        return string.Format(
            CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", total / 3600, total % 3600 / 60, total % 60);
    }

    /// <summary>The age cell: "" until the profile (or its creation date) arrives, "n.a." when the
    /// resident hides their age, otherwise the whole days. Hidden wins over an unknown date, because
    /// "hidden" is a fact about the resident and "unknown" only a fact about us.</summary>
    public static string FormatAge(AvatarBriefProfile? profile, DateTime utcNow)
    {
        if (profile is null) return "";
        if (profile.AgeHidden) return "n.a.";
        return profile.AgeInDays(utcNow) is { } days ? days.ToString(CultureInfo.InvariantCulture) : "";
    }

    /// <summary>The payment cell: "$$" for used, "$" for on file, otherwise empty -- never both, and
    /// "none" and "not known yet" look the same, as in Firestorm.</summary>
    public static string FormatPayment(PaymentInfo payment) => payment switch
    {
        PaymentInfo.Used => "$$",
        PaymentInfo.OnFile => "$",
        _ => "",
    };

    /// <summary>The chat range a distance falls in. The bounds are inclusive: exactly 20.0 m is still
    /// say range.</summary>
    public static RangeBand BandOf(float distance, float sayRangeMetres = 20f, float shoutRangeMetres = 100f)
    {
        if (distance <= sayRangeMetres) return RangeBand.Say;
        return distance <= shoutRangeMetres ? RangeBand.Shout : RangeBand.Far;
    }

    /// <summary>The three numbers Firestorm puts after "Name": everyone listed, those in the same
    /// region, and those within say range.</summary>
    public static (int Total, int InRegion, int InChatRange) Counts(IReadOnlyCollection<RadarRow> rows, float sayRangeMetres = 20f)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return (rows.Count, rows.Count(r => r.InSameRegion), rows.Count(r => r.Distance <= sayRangeMetres));
    }

    /// <summary><see cref="Counts"/> as the header suffix, e.g. "[5/5/1]".</summary>
    public static string CountsSuffix(IReadOnlyCollection<RadarRow> rows, float sayRangeMetres = 20f)
    {
        var (total, inRegion, inChatRange) = Counts(rows, sayRangeMetres);
        return string.Format(CultureInfo.InvariantCulture, "[{0}/{1}/{2}]", total, inRegion, inChatRange);
    }

    /// <summary>Compares two possibly-unknown values: unknown (null) is always after known, whichever
    /// way the sort runs; known values compare normally and are flipped for descending.</summary>
    private static int CompareKnownFirst<T>(T? a, T? b, bool ascending) where T : struct, IComparable<T>
    {
        if (a is not { } x) return b is null ? 0 : 1;
        if (b is not { } y) return -1;
        return ascending ? x.CompareTo(y) : y.CompareTo(x);
    }

    /// <summary>The same ordering for a value that is always known (a flag, a distance).</summary>
    private static int CompareValues<T>(T a, T b, bool ascending) where T : struct, IComparable<T> =>
        ascending ? a.CompareTo(b) : b.CompareTo(a);

    /// <summary>Unknown for no profile and for a profile whose payment status has not arrived.</summary>
    private static int? PaymentKey(RadarRow row) =>
        row.Profile is { Payment: not PaymentInfo.Unknown } p ? (int)p.Payment : null;

    /// <summary>Unknown only without a profile; with one, a note either exists or does not.</summary>
    private static bool? NoteKey(RadarRow row) => row.Profile?.HasNote;
}
