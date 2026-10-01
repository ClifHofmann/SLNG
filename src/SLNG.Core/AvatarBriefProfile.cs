using System;
using System.Globalization;

namespace SLNG.Core;

/// <summary>How much payment information an account has -- the "$" and "$$" of a people list.
/// Firestorm shows one or the other, never both, and "used" wins over "on file".</summary>
public enum PaymentInfo
{
    /// <summary>Not known yet: the profile has not arrived.</summary>
    Unknown = 0,
    None = 1,
    /// <summary>"$" -- payment details are on file (the profile's Identified flag).</summary>
    OnFile = 2,
    /// <summary>"$$" -- the account has used them (the profile's Transacted flag).</summary>
    Used = 3,
}

/// <summary>
/// What a nearby-people list needs to know about one avatar beyond name and position
/// (FEAT-UI-39): the viewer user's private note on them, their payment-info status and their
/// account age. All of it comes out of the avatar's profile, so it arrives together and some time
/// after the avatar does. A field that has not arrived is <c>null</c> / <c>Unknown</c>, which is
/// different from "known to be empty" -- <c>Notes == ""</c> means "no note", <c>null</c> means
/// "ask again later".
///
/// Engine- and protocol-neutral: <c>SLNG.Net.GridSession</c> builds it from either the
/// AgentProfile capability or the legacy UDP replies; nothing here knows which.
/// </summary>
/// <param name="AgentId">The avatar this is about.</param>
/// <param name="Notes">The note the logged-in user keeps on this avatar. Stored by the grid, so a
/// note written in another viewer shows up here.</param>
/// <param name="Payment">The "$" / "$$" status.</param>
/// <param name="BornOnUtc">When the account was created, or null if unknown.</param>
/// <param name="AgeHidden">The resident chose not to show their age; a list shows "n.a." and
/// <see cref="AgeInDays"/> answers null.</param>
public sealed record AvatarBriefProfile(
    Guid AgentId,
    string? Notes = null,
    PaymentInfo Payment = PaymentInfo.Unknown,
    DateTime? BornOnUtc = null,
    bool AgeHidden = false)
{
    /// <summary>True only when a note is known and not empty -- what the note icon keys off.</summary>
    public bool HasNote => !string.IsNullOrEmpty(Notes);

    /// <summary>Whole days since the account was created, or null if hidden or unknown. A date in
    /// the future (a skewed clock) counts as 0 rather than a negative age.</summary>
    public int? AgeInDays(DateTime utcNow)
    {
        if (AgeHidden || BornOnUtc is not { } born) return null;
        return Math.Max(0, (int)(utcNow - born).TotalDays);
    }

    /// <summary>The two profile flags as one status: transacted beats identified, as in Firestorm
    /// (fsradar.cpp:343-351) -- "has paid" is the stronger statement and the two never show together.</summary>
    public static PaymentInfo PaymentFrom(bool identified, bool transacted) =>
        transacted ? PaymentInfo.Used : identified ? PaymentInfo.OnFile : PaymentInfo.None;

    /// <summary>The creation date as the legacy UDP reply words it (e.g. "2007-05-14" or
    /// "05/14/2007" -- the format is the grid's own choice), or null if it is blank or does not
    /// parse. Read as invariant culture, so a day-first date from a grid that sends one may come
    /// out wrong or not at all; an age off by months is a smaller harm than a crash.</summary>
    public static DateTime? ParseBornOn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }
}
