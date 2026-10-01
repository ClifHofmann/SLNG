using System;
using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>
/// Which nearby-people columns are shown, and how the table is sorted (FEAT-UI-39).
///
/// Visibility is stored as a CHOICE, not as a full snapshot: a column the user has explicitly shown
/// or hidden is remembered, one they never touched follows <see cref="RadarColumnInfo.DefaultVisible"/>.
/// That is why persistence is two lists (<see cref="SerializeShown"/>, <see cref="SerializeHidden"/>)
/// rather than one list of what was visible. With a snapshot, a column added in a later version
/// would be missing from every saved file and so read as hidden; this way it simply shows up on its
/// default, and a default the maintainer changes later reaches users who never overrode it.
/// </summary>
public sealed class RadarColumnSettings
{
    // null = never chosen, follow the default. Indexed by (int)RadarColumn.
    private readonly bool?[] _chosen = new bool?[RadarColumns.All.Count];

    private RadarColumnSettings()
    {
    }

    /// <summary>The column the table is sorted by.</summary>
    public RadarColumn SortColumn { get; set; } = RadarColumn.Range;

    public bool SortAscending { get; set; } = true;

    /// <summary>The persistence id of <see cref="SortColumn"/>.</summary>
    public string? SortId => RadarColumns.Info(SortColumn).Id;

    /// <summary>The visible columns in display order.</summary>
    public IReadOnlyList<RadarColumn> VisibleColumns
    {
        get
        {
            var visible = new List<RadarColumn>();
            foreach (var info in RadarColumns.All)
            {
                if (IsVisible(info.Column)) visible.Add(info.Column);
            }

            return visible;
        }
    }

    public static RadarColumnSettings CreateDefault() => new();

    public bool IsVisible(RadarColumn column)
    {
        var info = RadarColumns.Info(column);
        if (info.AlwaysVisible) return true;
        if (!info.Offered) return false;
        return _chosen[(int)column] ?? info.DefaultVisible;
    }

    /// <summary>Records a choice. Ignored for a pinned column (it stays shown) and for one that is not
    /// offered (it stays hidden), so a caller cannot put the table into a state the chooser could
    /// never produce.</summary>
    public void SetVisible(RadarColumn column, bool visible)
    {
        var info = RadarColumns.Info(column);
        if (info.AlwaysVisible || !info.Offered) return;
        _chosen[(int)column] = visible;
    }

    /// <summary>Forgets every show/hide choice, so each column is back on its default. The sort is
    /// left alone: "reset columns" is about which columns, not about the order.</summary>
    public void ResetColumns() => Array.Clear(_chosen);

    /// <summary>Ids of the columns the user explicitly showed, comma separated, in display order.</summary>
    public string SerializeShown() => Serialize(true);

    /// <summary>Ids of the columns the user explicitly hid, comma separated, in display order.</summary>
    public string SerializeHidden() => Serialize(false);

    /// <summary>
    /// Rebuilds settings from what <see cref="SerializeShown"/>, <see cref="SerializeHidden"/>,
    /// <see cref="SortId"/> and <see cref="SortAscending"/> wrote. Tolerant, because the file is
    /// user-editable and outlives versions: unknown ids are skipped, blanks and stray whitespace are
    /// ignored, a column in both lists counts as shown, and a sort that is missing, unknown, not
    /// sortable or not offered falls back to <see cref="RadarColumn.Range"/>.
    /// </summary>
    public static RadarColumnSettings Parse(string? shown, string? hidden, string? sortId, bool? ascending)
    {
        var settings = CreateDefault();

        // Hidden first, so a column listed in both ends up shown.
        foreach (var id in SplitIds(hidden))
        {
            if (RadarColumns.TryParseId(id, out var column)) settings.SetVisible(column, false);
        }

        foreach (var id in SplitIds(shown))
        {
            if (RadarColumns.TryParseId(id, out var column)) settings.SetVisible(column, true);
        }

        settings.SortColumn = RadarColumns.TryParseId(sortId, out var sort)
            && RadarColumns.Info(sort) is { Sortable: true, Offered: true }
                ? sort
                : RadarColumn.Range;
        settings.SortAscending = ascending ?? true;
        return settings;
    }

    private string Serialize(bool wanted)
    {
        var ids = new List<string>();
        foreach (var info in RadarColumns.All)
        {
            if (_chosen[(int)info.Column] == wanted) ids.Add(info.Id);
        }

        return string.Join(',', ids);
    }

    private static IEnumerable<string> SplitIds(string? list)
    {
        if (string.IsNullOrWhiteSpace(list)) yield break;
        foreach (var part in list.Split(','))
        {
            var id = part.Trim();
            if (id.Length > 0) yield return id;
        }
    }
}
