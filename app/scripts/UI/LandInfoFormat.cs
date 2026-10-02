using System;
using System.Globalization;
using System.Numerics;
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

    // --- Options tab (FEAT-LAND-02) -----------------------------------------------------------------
    // Sources: LLPanelLandOptions::refresh (llfloaterland.cpp:2036-2160), LLPanelLandAudio::refresh
    // (llpanellandaudio.cpp:108-166), LLPanelLandMedia::refresh (llpanellandmedia.cpp:120-170).

    /// <summary>How one read-only check box is drawn: whether it is ticked, which label it carries, and
    /// whether the setting applies at all (an inapplicable box is drawn dimmed, like the viewer's greyed one).</summary>
    internal readonly record struct CheckState(bool Ticked, string LabelKey, bool Applicable = true);

    internal static bool Has(ParcelOptions o, ParcelOptions flag) => (o & flag) != 0;

    /// <summary>The "Group" half of an Everyone/Group pair: the viewer ticks it whenever "Everyone" is on
    /// (llfloaterland.cpp:2055, :2061, :2070; llpanellandaudio.cpp:161).</summary>
    internal static bool GroupTicked(ParcelOptions o, ParcelOptions everyone, ParcelOptions group) =>
        Has(o, everyone) || Has(o, group);

    /// <summary>"Safe (no damage)" is the inverse of the damage flag (llfloaterland.cpp:2064).</summary>
    internal static bool SafeTicked(ParcelOptions o) => !Has(o, ParcelOptions.AllowDamage);

    /// <summary>"No Pushing": the region override ticks it and relabels it (llfloaterland.cpp:2076-2082).</summary>
    internal static CheckState NoPushing(ParcelOptions o) =>
        Has(o, ParcelOptions.RegionPushOverride)
            ? new CheckState(true, "ui.land.opt_no_push_override")
            : new CheckState(Has(o, ParcelOptions.RestrictPush), "ui.land.opt_no_push");

    /// <summary>The "Moderate Content" box follows the REGION's rating (llfloaterland.cpp:2148-2158):
    /// General region -> unticked; Moderate -> the parcel's own bit; Adult -> ticked and relabelled "Adult
    /// Content". The first and last are greyed in the viewer, so they are not "applicable". An unknown
    /// rating is drawn like General (nothing to claim).</summary>
    internal static CheckState ModerateContent(MaturityLevel? rating, ParcelOptions o) => rating switch
    {
        MaturityLevel.Moderate => new CheckState(Has(o, ParcelOptions.MaturePublish), "ui.land.opt_moderate"),
        MaturityLevel.Adult => new CheckState(true, "ui.land.opt_adult", false),
        _ => new CheckState(false, "ui.land.opt_moderate", false),
    };

    /// <summary>The category combo's text. Adult and Stage are wire values the viewer's combo has no item
    /// for; they are shown by name rather than hidden.</summary>
    internal static string CategoryText(ParcelCategory? category) => category switch
    {
        ParcelCategory.None => L10n.Tr("ui.land.cat_none"),
        ParcelCategory.Linden => L10n.Tr("ui.land.cat_linden"),
        ParcelCategory.Adult => L10n.Tr("ui.land.cat_adult"),
        ParcelCategory.Arts => L10n.Tr("ui.land.cat_arts"),
        ParcelCategory.Business => L10n.Tr("ui.land.cat_business"),
        ParcelCategory.Educational => L10n.Tr("ui.land.cat_educational"),
        ParcelCategory.Gaming => L10n.Tr("ui.land.cat_gaming"),
        ParcelCategory.Hangout => L10n.Tr("ui.land.cat_hangout"),
        ParcelCategory.Newcomer => L10n.Tr("ui.land.cat_newcomer"),
        ParcelCategory.Park => L10n.Tr("ui.land.cat_park"),
        ParcelCategory.Residential => L10n.Tr("ui.land.cat_residential"),
        ParcelCategory.Shopping => L10n.Tr("ui.land.cat_shopping"),
        ParcelCategory.Stage => L10n.Tr("ui.land.cat_stage"),
        ParcelCategory.Other => L10n.Tr("ui.land.cat_other"),
        ParcelCategory.Rental => L10n.Tr("ui.land.cat_rental"),
        _ => Unknown,
    };

    internal static string RoutingText(ParcelLandingType? routing) => routing switch
    {
        ParcelLandingType.Blocked => L10n.Tr("ui.land.route_blocked"),
        ParcelLandingType.LandingPoint => L10n.Tr("ui.land.route_landing_point"),
        ParcelLandingType.Anywhere => L10n.Tr("ui.land.route_anywhere"),
        _ => Unknown,
    };

    /// <summary>"x, y, z (heading°)" with each metre rounded half-up like the viewer's ll_round, or
    /// "(none)" when the parcel has no landing point (llfloaterland.cpp:2109-2118).</summary>
    internal static string LandingPointText(Vector3? point, int? headingDegrees)
    {
        if (point is not { } p) return L10n.Tr("ui.land.none");
        static int R(float v) => (int)Math.Floor(v + 0.5f);
        return string.Create(CultureInfo.InvariantCulture, $"{R(p.X)}, {R(p.Y)}, {R(p.Z)} ({headingDegrees ?? 0}°)");
    }

    /// <summary>A texture id shown as selectable text (there is no texture preview in this window yet);
    /// "(none)" when nil.</summary>
    internal static string TextureIdText(Guid? id) =>
        id is { } g && g != Guid.Empty ? g.ToString() : L10n.Tr("ui.land.none");

    // --- Media tab -----------------------------------------------------------------------------------

    /// <summary>What a media MIME type allows in the Media tab.</summary>
    internal enum MediaClass
    {
        /// <summary>Static or unknown content (images, documents, none): neither size nor loop.</summary>
        Static,

        /// <summary>Web content: the size fields apply, looping does not.</summary>
        Web,

        /// <summary>Movie or audio: looping applies, the size fields do not.</summary>
        Playable,
    }

    /// <summary>Which widget set a MIME type belongs to. The viewer reads a ~100-entry table
    /// (mime_types.xml, LLMIMETypes::widgetType, llmimetypes.cpp:168) whose sets are web (resize),
    /// movie and audio (loop), image and none (neither); an unlisted type falls into "none". This ports
    /// only the part that matters for parcels: text/html, XHTML and JavaScript -> Web; video/*, audio/*,
    /// Ogg and SMIL -> Playable; everything else, an unknown type and the "none" placeholder (null) ->
    /// Static. Deliberately NOT "unknown -> Web": the viewer disables the size fields for those.</summary>
    internal static MediaClass ClassifyMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return MediaClass.Static;
        string m = mime.Trim().ToLowerInvariant();
        if (m is "text/html" or "application/xhtml+xml" or "application/javascript") return MediaClass.Web;
        if (m.StartsWith("video/", StringComparison.Ordinal) || m.StartsWith("audio/", StringComparison.Ordinal)
            || m is "application/ogg" or "application/smil")
            return MediaClass.Playable;
        return MediaClass.Static;
    }

    internal static string MimeText(string? mime) =>
        string.IsNullOrWhiteSpace(mime) ? L10n.Tr("ui.land.mime_none") : mime;

    internal static bool LoopApplies(ParcelMedia m) => ClassifyMime(m.MimeType) == MediaClass.Playable;

    /// <summary>The viewer shows the loop box unticked when looping does not apply (llpanellandmedia.cpp:155).</summary>
    internal static bool LoopTicked(ParcelMedia m) => LoopApplies(m) && m.Loop;

    internal static bool SizeApplies(ParcelMedia m) => ClassifyMime(m.MimeType) == MediaClass.Web;

    /// <summary>"800 × 600 pixels"; "default" when both are 0 (the viewer's "leave 0 for default"); a dash
    /// when the type does not allow a size.</summary>
    internal static string SizeText(ParcelMedia m)
    {
        if (!SizeApplies(m)) return Unknown;
        if (m.Width == 0 && m.Height == 0) return L10n.Tr("ui.land.media_size_default");
        return L10n.TrFormat("ui.land.media_size_value", m.Width, m.Height);
    }

    // --- Sound tab -----------------------------------------------------------------------------------

    /// <summary>The voice boxes (llpanellandaudio.cpp:125-152). When the region does not allow voice,
    /// "Enable Voice" becomes "Enable Voice (established by the Estate)" and the restrict box is greyed;
    /// the restrict box is otherwise only live while voice is on for the parcel. An unknown region state
    /// (null) draws the plain box. "Restrict Voice to this parcel" is the inverse of UseEstateVoiceChannel.</summary>
    internal static (CheckState Enable, CheckState Restrict) VoiceStates(ParcelOptions o, bool? regionVoice)
    {
        bool allow = Has(o, ParcelOptions.AllowVoice);
        bool estateOff = regionVoice == false;
        var enable = new CheckState(allow, estateOff ? "ui.land.snd_voice_estate" : "ui.land.snd_voice_enable", !estateOff);
        var restrict = new CheckState(!Has(o, ParcelOptions.UseEstateVoiceChannel), "ui.land.snd_voice_local", !estateOff && allow);
        return (enable, restrict);
    }

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

    // --- Covenant tab (FEAT-LAND-05) ----------------------------------------------------------------
    // Sources: process_covenant_reply / onCovenantLoadComplete (llviewermessage.cpp:6782-6947),
    // LLPanelLandCovenant / LLPanelEstateCovenant (llfloaterland.cpp:2997, llfloaterregioninfo.cpp:2565),
    // strings.xml (RegionNoCovenant, RegionNoCovenantOtherOwner, covenant_last_modified, never_text).

    /// <summary>What the body of the covenant view says and how it is drawn.</summary>
    internal enum CovenantBodyKind
    {
        /// <summary>The covenant text itself, shown in a selectable text box.</summary>
        Text,

        /// <summary>A line of ours in place of the text: loading, "no covenant provided".</summary>
        Notice,

        /// <summary>A line of ours that says something went wrong. Drawn so it cannot pass for a covenant
        /// or for "none set".</summary>
        Problem,
    }

    /// <summary>The covenant body for one record. No covenant set prints the viewer's fixed sentence,
    /// the longer one when the estate has an owner (the land is then sold by the estate owner, not by
    /// Linden Lab: <c>llviewermessage.cpp</c>:6853-6861). A fetched text is returned as it is; an empty
    /// one stays empty, which is not "none set".</summary>
    internal static (CovenantBodyKind Kind, string Text) CovenantBody(CovenantInfo c) => c.TextState switch
    {
        CovenantTextState.Loaded => (CovenantBodyKind.Text, c.Text ?? string.Empty),
        CovenantTextState.None => (
            CovenantBodyKind.Notice,
            L10n.Tr(c.EstateOwnerId == Guid.Empty ? "ui.land.cov_none" : "ui.land.cov_none_other_owner")),
        CovenantTextState.Failed => (CovenantBodyKind.Problem, L10n.Tr("ui.land.cov_text_failed")),
        _ => (CovenantBodyKind.Notice, L10n.Tr("ui.land.cov_loading")),
    };

    /// <summary>The body before any record arrived, and after a request that got no reply.</summary>
    internal static (CovenantBodyKind Kind, string Text) CovenantBodyLoading() =>
        (CovenantBodyKind.Notice, L10n.Tr("ui.land.cov_loading"));

    internal static (CovenantBodyKind Kind, string Text) CovenantBodyFailed() =>
        (CovenantBodyKind.Problem, L10n.Tr("ui.land.cov_failed"));

    internal static string CovenantEstateText(CovenantInfo c) => OrDash(c.EstateName);

    /// <summary>The estate owner: "(none)" for a nil id, otherwise the name, or "(loading…)" while it
    /// is still being looked up. The viewer treats the id as an avatar's, so no group lookup.</summary>
    internal static string CovenantOwnerText(CovenantInfo c, NameLookup lookup) =>
        c.EstateOwnerId == Guid.Empty
            ? L10n.Tr("ui.land.none")
            : lookup(c.EstateOwnerId, false) ?? L10n.Tr("ui.land.loading");

    internal static string CovenantModifiedText(DateTime? timestampUtc) =>
        CovenantModifiedText(timestampUtc, TimeZoneInfo.Local);

    /// <summary>"Last modified: 2023-11-14 23:13:20 (UTC+01:00)", or "Last modified: (never)" for a
    /// timestamp of 0. The reference viewer prints this in the USER's local time (<c>llstring.cpp</c>:1474,
    /// the <c>local</c> parameter of <c>LTime*</c> in <c>language_settings.xml</c>), not in SL time like
    /// the General tab's claim date, so the offset is spelled out. UTC when no zone is given.</summary>
    internal static string CovenantModifiedText(DateTime? timestampUtc, TimeZoneInfo? zone)
    {
        if (timestampUtc is not { } utc) return L10n.Tr("ui.land.cov_modified_never");
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

        var offset = zone?.GetUtcOffset(utc) ?? TimeSpan.Zero;
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        string stamp = Stamp(utc + offset) + " "
            + L10n.TrFormat("ui.land.cov_utc_offset", sign + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture));
        return L10n.TrFormat("ui.land.cov_modified", stamp);
    }
}
