namespace SLNG.Core;

/// <summary>
/// The columns a Firestorm-style "nearby people" table can show (FEAT-UI-39). Declaration order IS
/// the display order, so a column added later slots in where it belongs on screen -- never reorder
/// or renumber, because <see cref="RadarColumns"/> indexes its registry by the enum value.
/// Persistence does not use the numbers; it uses <see cref="RadarColumnInfo.Id"/>.
/// </summary>
public enum RadarColumn
{
    /// <summary>The avatar's name, with the running "[total/in region/in chat range]" counts.</summary>
    Name,
    /// <summary>Voice activity. SLNG has no voice yet, so the cells stay empty; the slot is kept
    /// visible so the layout does not move the day voice arrives.</summary>
    Voice,
    /// <summary>Whether the avatar is in the same region as the local avatar.</summary>
    InRegion,
    /// <summary>The typing indicator. No data source yet, so it is not offered.</summary>
    Typing,
    /// <summary>Whether the avatar is sitting.</summary>
    Sitting,
    /// <summary>The "$" / "$$" payment-info status.</summary>
    Payment,
    /// <summary>Whether the viewer user keeps a private note on the avatar.</summary>
    Note,
    /// <summary>Account age in days.</summary>
    Age,
    /// <summary>How long the avatar has been in the list.</summary>
    Seen,
    /// <summary>Distance from the local avatar in metres.</summary>
    Range,
    /// <summary>The avatar's language. No data source yet, so it is not offered.</summary>
    Language,
}
