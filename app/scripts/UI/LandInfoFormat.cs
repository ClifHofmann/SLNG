using System;
using System.Globalization;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-LAND-01: how a <see cref="ParcelInfo"/> is turned into the strings of the Land-Info General
/// tab. Kept apart from the Controls so the rules (what public land says, when "(Sale Pending)" is
/// appended, which clock the claim date is shown in) read in one place and can be asserted by the
/// selftest without a window. Every user-visible word comes from the locale files.
///
/// <para>Wording and rules follow the reference viewer's <c>LLPanelLandGeneral::refresh</c>
/// (<c>llfloaterland.cpp</c>).</para>
/// </summary>
internal static class LandInfoFormat
{
    /// <summary>Resolves an owner / group / buyer id to a display string, or null while it is still
    /// being looked up. The arguments are the id and whether it is a group's.</summary>
    internal delegate string? NameLookup(Guid id, bool isGroup);

    /// <summary>Shown for a value that is not known (yet).</summary>
    internal const string Unknown = "–";

    internal static string OrDash(string? text) => string.IsNullOrWhiteSpace(text) ? Unknown : text;

    internal static string ParcelIdText(Guid id) => id == Guid.Empty ? Unknown : id.ToString();

    internal static string RatingText(MaturityLevel? rating) => rating switch
    {
        MaturityLevel.General => L10n.Tr("ui.land.rating_general"),
        MaturityLevel.Moderate => L10n.Tr("ui.land.rating_moderate"),
        MaturityLevel.Adult => L10n.Tr("ui.land.rating_adult"),
        _ => Unknown,
    };

    /// <summary>The Owner row: "(public)" for unowned land; otherwise the name, with "(Group Owned)"
    /// when a group holds the land and "(Sale Pending)" when a sale is pending
    /// (<c>llfloaterland.cpp</c>:874-889).</summary>
    internal static string OwnerText(ParcelInfo p, NameLookup lookup)
    {
        string text;
        if (p.OwnerId == Guid.Empty)
        {
            text = L10n.Tr("ui.land.owner_public");
        }
        else
        {
            string name = lookup(p.OwnerId, p.IsGroupOwned) ?? L10n.Tr("ui.land.loading");
            text = p.IsGroupOwned ? L10n.TrFormat("ui.land.owner_group", name) : name;
        }

        return p.Ownership == ParcelOwnership.LeasePending
            ? text + " " + L10n.Tr("ui.land.sale_pending")
            : text;
    }

    /// <summary>The Group row: "(none)" when the parcel is not set to a group.</summary>
    internal static string GroupText(ParcelInfo p, NameLookup lookup) =>
        p.GroupId == Guid.Empty
            ? L10n.Tr("ui.land.none")
            : lookup(p.GroupId, true) ?? L10n.Tr("ui.land.loading");

    /// <summary>The Price row: "L$ N (L$ P/m²)" for a parcel that is for sale, "Not for sale" otherwise.</summary>
    internal static string PriceText(ParcelInfo p)
    {
        if (!p.ForSale) return L10n.Tr("ui.land.not_for_sale");
        string price = p.SalePriceL.ToString("N0");
        if (p.AreaSqm <= 0) return L10n.TrFormat("ui.land.price", price);
        int perSqm = (int)Math.Round((double)p.SalePriceL / p.AreaSqm, MidpointRounding.AwayFromZero);
        return L10n.TrFormat("ui.land.price_per_sqm", price, perSqm.ToString("N0"));
    }

    internal static string AreaText(int areaSqm) => L10n.TrFormat("ui.land.area", areaSqm.ToString("N0"));

    /// <summary>The Traffic row, printed like the viewer's <c>%.0f</c>; a dash until the figure arrives.</summary>
    internal static string TrafficText(float? dwell) =>
        dwell is { } d ? d.ToString("F0", CultureInfo.InvariantCulture) : Unknown;

    /// <summary>The "Authorized buyer" row: "Anyone", or the one avatar the parcel is for sale to.</summary>
    internal static string BuyerText(ParcelInfo p, NameLookup lookup) =>
        p.AuthorizedBuyerId is { } id && id != Guid.Empty
            ? lookup(id, false) ?? L10n.Tr("ui.land.loading")
            : L10n.Tr("ui.land.anyone");

    internal static string ObjectsText(ParcelInfo p) =>
        L10n.Tr(p.SellWithObjects ? "ui.land.objects_included" : "ui.land.objects_not_included");

    // --- the claim date, in Second Life time ------------------------------------------------------

    private static readonly Lazy<TimeZoneInfo?> SlTime = new(FindSlTimeZone);

    /// <summary>Second Life time is US Pacific time with daylight saving. Windows calls the zone
    /// "Pacific Standard Time", Linux and macOS "America/Los_Angeles"; null when neither exists
    /// (a trimmed container), in which case the date is shown in UTC and says so.</summary>
    internal static TimeZoneInfo? FindSlTimeZone()
    {
        foreach (string id in new[] { "Pacific Standard Time", "America/Los_Angeles" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return null;
    }

    internal static string ClaimedText(DateTime? claimedUtc) => ClaimedText(claimedUtc, SlTime.Value);

    /// <summary>"2006-08-15 13:47:25 SLT"; "... UTC (SL time unavailable)" when no zone is given.</summary>
    internal static string ClaimedText(DateTime? claimedUtc, TimeZoneInfo? slZone)
    {
        if (claimedUtc is not { } utc) return Unknown;
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

        if (slZone == null)
            return L10n.TrFormat("ui.land.claimed_utc", Stamp(utc));
        return L10n.TrFormat("ui.land.claimed_slt", Stamp(TimeZoneInfo.ConvertTimeFromUtc(utc, slZone)));
    }

    private static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
