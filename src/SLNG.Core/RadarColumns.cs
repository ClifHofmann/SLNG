using System;
using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>
/// The registry of nearby-people columns (FEAT-UI-39): what exists, in what order, and which are
/// sortable, on by default, pinned or offered. One table, so the header, the column chooser and
/// the settings parser can never disagree about a column.
///
/// "Offered" is separate from "exists" on purpose: Typing and Language have an enum slot and an id
/// so the layout and the saved settings are ready for them, but nothing feeds them yet, and a
/// column of permanently empty cells in the chooser would only look broken.
/// </summary>
public static class RadarColumns
{
    private const string KeyPrefix = "ui.radar.col.";

    // Index == (int)RadarColumn, so Info() is an array lookup; the tests pin that.
    private static readonly RadarColumnInfo[] Table =
    [
        Make(RadarColumn.Name, "name", sortable: true, defaultVisible: true, alwaysVisible: true, offered: true),
        // SLNG has no voice yet, so these cells stay empty. The maintainer wants the slot visible
        // anyway, so the table does not change shape the day voice lands.
        Make(RadarColumn.Voice, "voice", sortable: true, defaultVisible: true, alwaysVisible: false, offered: true),
        Make(RadarColumn.InRegion, "in_region", sortable: true, defaultVisible: true, alwaysVisible: false, offered: true),
        Make(RadarColumn.Typing, "typing", sortable: true, defaultVisible: false, alwaysVisible: false, offered: false),
        Make(RadarColumn.Sitting, "sitting", sortable: true, defaultVisible: true, alwaysVisible: false, offered: true),
        Make(RadarColumn.Payment, "payment", sortable: true, defaultVisible: true, alwaysVisible: false, offered: true),
        Make(RadarColumn.Note, "note", sortable: true, defaultVisible: true, alwaysVisible: false, offered: true),
        Make(RadarColumn.Age, "age", sortable: true, defaultVisible: true, alwaysVisible: false, offered: true),
        Make(RadarColumn.Seen, "seen", sortable: true, defaultVisible: true, alwaysVisible: false, offered: true),
        Make(RadarColumn.Range, "range", sortable: true, defaultVisible: true, alwaysVisible: false, offered: true),
        Make(RadarColumn.Language, "language", sortable: false, defaultVisible: false, alwaysVisible: false, offered: false),
    ];

    private static readonly Dictionary<string, RadarColumn> ByIdMap = BuildIdMap();

    /// <summary>Every column in display order.</summary>
    public static IReadOnlyList<RadarColumnInfo> All => Table;

    public static RadarColumnInfo Info(RadarColumn column) => Table[(int)column];

    /// <summary>Reads a persisted id back. Trimmed and case-insensitive, because a settings file is
    /// hand-editable and a stray space should not silently drop a column.</summary>
    public static bool TryParseId(string? id, out RadarColumn column)
    {
        column = default;
        return id is not null && ByIdMap.TryGetValue(id.Trim(), out column);
    }

    private static RadarColumnInfo Make(
        RadarColumn column, string id, bool sortable, bool defaultVisible, bool alwaysVisible, bool offered) =>
        new(column, id, KeyPrefix + id, KeyPrefix + id + ".tip", sortable, defaultVisible, alwaysVisible, offered);

    private static Dictionary<string, RadarColumn> BuildIdMap()
    {
        var map = new Dictionary<string, RadarColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in Table) map[info.Id] = info.Column;
        return map;
    }
}
