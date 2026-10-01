namespace SLNG.Core;

/// <summary>
/// Static facts about one nearby-people column (FEAT-UI-39); the registry is
/// <see cref="RadarColumns"/>.
/// </summary>
/// <param name="Column">Which column this describes.</param>
/// <param name="Id">A stable lowercase identifier for persistence. The enum's numeric value is
/// deliberately not saved: ids survive reordering and later additions, numbers do not.</param>
/// <param name="TitleKey">The i18n key for the header text, <c>ui.radar.col.&lt;id&gt;</c>.</param>
/// <param name="TooltipKey">The i18n key for the header tooltip, <c>ui.radar.col.&lt;id&gt;.tip</c>.</param>
/// <param name="Sortable">True if clicking the header can sort by it.</param>
/// <param name="DefaultVisible">Whether the column shows for someone who never chose either way.</param>
/// <param name="AlwaysVisible">True if it cannot be hidden: a table without a name column is
/// unreadable.</param>
/// <param name="Offered">True if it appears in the column chooser at all. A column with no data
/// source yet is not offered, and a column that is not offered can never become visible.</param>
public sealed record RadarColumnInfo(
    RadarColumn Column,
    string Id,
    string TitleKey,
    string TooltipKey,
    bool Sortable,
    bool DefaultVisible,
    bool AlwaysVisible,
    bool Offered);
