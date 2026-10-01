using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// The Firestorm-style nearby-people table (FEAT-UI-39): a <see cref="RadarTree"/> with one
/// row per avatar and the columns <see cref="RadarColumnSettings"/> says are visible. The window
/// owns the data, the sorting and the menus; this owns the Tree and nothing else.
///
/// The one rule that shapes it: <b>a refresh never rebuilds the table.</b> A row is a
/// <see cref="TreeItem"/> kept in <c>_items</c> by agent id -- added when an avatar appears, freed
/// when it leaves, and otherwise updated cell by cell where it stands and moved only if its place
/// in the sort order changed. The roster this replaces was rebuilt from fresh Buttons, and a click
/// that landed between two rebuilds was silently lost (Godot's BaseButton only fires when the same
/// instance sees both the press and the release). Only a change of the visible columns, which
/// changes the Tree's shape, starts over.
/// </summary>
internal sealed partial class RadarTableView : Control
{
    // Amber inside say range, normal text inside shout range, muted beyond -- the three bands
    // Firestorm tints the range cell by.
    private static readonly Color SayColour = new(1f, 0.78f, 0.3f);
    private static readonly Color ShoutColour = new(0.92f, 0.92f, 0.92f);
    private static readonly Color FarColour = new(0.55f, 0.55f, 0.58f);

    // A brand-new account stands out, as in Firestorm's list.
    private static readonly Color NewAccountColour = new(1f, 0.45f, 0.45f);
    private const int NewAccountDays = 7;

    /// <summary>The selection changed by a click (or became empty): the avatar's id, or null.</summary>
    public event Action<Guid?>? SelectionChanged;

    /// <summary>A row was double-clicked (or Enter was pressed on it).</summary>
    public event Action<Guid>? RowActivated;

    /// <summary>A row was right-clicked; it has just been selected. Carries where the click was, in
    /// screen coordinates.</summary>
    public event Action<Guid, Vector2>? RowContextMenu;

    /// <summary>A column title was clicked with the given button.</summary>
    public event Action<RadarColumn, MouseButton>? TitleClicked;

    private readonly RadarTree _tree;
    private readonly Label _emptyLabel;
    private readonly List<RadarColumn> _columns = new();
    private readonly Dictionary<Guid, TreeItem> _items = new();

    // Scratch collections reused by every refresh, so a refresh allocates nothing per row.
    private readonly HashSet<Guid> _keep = new();
    private readonly List<Guid> _gone = new();
    private readonly List<TreeItem> _ordered = new();

    private TreeItem? _root;
    private string?[] _titles = Array.Empty<string?>();
    private RadarColumn _sortColumn = RadarColumn.Range;
    private bool _sortAscending = true;
    private Font? _regularFont;
    private Font? _boldFont;

    // Set while this class changes the Tree itself (creating, freeing, selecting, rebuilding), so
    // the Tree's own selection signals are not mistaken for the user clicking.
    private bool _suppress;

    public RadarTableView()
    {
        CustomMinimumSize = new Vector2(0, 120);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        _tree = new RadarTree
        {
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            AllowRmbSelect = true,
            AllowSearch = false,         // type-ahead would swallow the keys that walk the avatar
            ColumnTitlesVisible = true,
            // Left on: without it the Tree reports the sum of its column widths as its minimum, and the
            // window could not be made narrower than that. The name column flexes, so a window of
            // normal width never scrolls.
            ScrollHorizontalEnabled = true,
            // Like the inventory trees: clicks work without focus, and the table never holds the
            // keyboard focus that the movement keys need.
            FocusMode = FocusModeEnum.None,
        };
        AddChild(_tree);
        _tree.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _emptyLabel = new Label
        {
            Text = L10n.Tr("ui.minimap.none_nearby"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _emptyLabel.AddThemeFontSizeOverride("font_size", 11);
        _emptyLabel.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        AddChild(_emptyLabel);
        _emptyLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _emptyLabel.OffsetTop = 24; // clear of the header

        _tree.ItemSelected += OnItemSelected;
        _tree.NothingSelected += OnNothingSelected;
        _tree.ItemActivated += OnItemActivated;
        _tree.ItemMouseSelected += OnItemMouseSelected;
        _tree.ColumnTitleClicked += OnColumnTitleClicked;
    }

    /// <summary>Starts the table over with these columns: sets the Tree's shape and drops every row.
    /// The next <see cref="Update"/> fills it again. The only thing that rebuilds the Tree.</summary>
    public void Configure(IReadOnlyList<RadarColumn> columns)
    {
        _suppress = true;
        try
        {
            _tree.Clear();
            _items.Clear();
            _columns.Clear();
            _columns.AddRange(columns);
            _tree.Columns = _columns.Count;
            for (int i = 0; i < _columns.Count; i++)
            {
                var info = RadarColumns.Info(_columns[i]);
                var (minWidth, expand, align) = LayoutOf(_columns[i]);
                _tree.SetColumnExpand(i, expand);
                _tree.SetColumnExpandRatio(i, 1);
                _tree.SetColumnCustomMinimumWidth(i, minWidth);
                _tree.SetColumnClipContent(i, true);
                _tree.SetColumnTitleAlignment(i, align);
                _tree.SetColumnTitleTooltipText(i, L10n.Tr(info.TooltipKey));
            }

            _root = _tree.CreateItem();
            _titles = new string?[_columns.Count];
            _emptyLabel.Visible = true;
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>Which column the table is sorted by, for the arrow in its title. The window sorts the
    /// rows; this only draws the arrow.</summary>
    public void SetSort(RadarColumn column, bool ascending)
    {
        _sortColumn = column;
        _sortAscending = ascending;
        Array.Clear(_titles); // re-set on the next Update
    }

    /// <summary>
    /// Brings the table in line with <paramref name="rows"/>, already filtered and sorted. Rows for
    /// avatars that are gone are freed, new ones created, the rest updated where they stand, and
    /// only a row whose place changed is moved. <paramref name="allRows"/> is the unfiltered set,
    /// for the counts in the name header.
    /// </summary>
    public void Update(IReadOnlyList<RadarRow> rows, IReadOnlyCollection<RadarRow> allRows,
        DateTime utcNow, float drawDistance, float sayRange, float shoutRange)
    {
        if (_root == null) return;
        EnsureFonts();

        _suppress = true;
        try
        {
            _keep.Clear();
            foreach (var row in rows) _keep.Add(row.AgentId);

            _gone.Clear();
            foreach (var id in _items.Keys)
            {
                if (!_keep.Contains(id)) _gone.Add(id);
            }
            foreach (var id in _gone)
            {
                _items[id].Free();
                _items.Remove(id);
            }

            _ordered.Clear();
            foreach (var row in rows)
            {
                if (!_items.TryGetValue(row.AgentId, out var item))
                {
                    item = CreateRowItem(row.AgentId);
                    _items[row.AgentId] = item;
                }

                FillRow(item, row, utcNow, drawDistance, sayRange, shoutRange);
                _ordered.Add(item);
            }

            PutInOrder();
            UpdateTitles(allRows, sayRange);
            _emptyLabel.Visible = _items.Count == 0;
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>Selects the avatar's row (or clears the selection for null or an avatar that has no
    /// row, e.g. one the filter hides), optionally scrolling it into view.</summary>
    public void SelectRow(Guid? agentId, bool scrollTo)
    {
        if (_root == null) return;
        _suppress = true;
        try
        {
            if (agentId is { } id && _items.TryGetValue(id, out var item))
            {
                if (_tree.GetSelected() != item) item.Select(0);
                if (scrollTo) _tree.ScrollToItem(item);
            }
            else if (_tree.GetSelected() != null)
            {
                _tree.DeselectAll();
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    private static (int MinWidth, bool Expand, HorizontalAlignment Align) LayoutOf(RadarColumn column) => column switch
    {
        // The name takes whatever the others leave; the numbers are right-aligned and fixed. The
        // widths leave room for the sort arrow in the title, so changing the sort never shifts
        // the columns.
        RadarColumn.Name => (80, true, HorizontalAlignment.Left),
        // Each width includes the 8 px of cell padding the Tree theme adds (RadarTree).
        RadarColumn.Age => (50, false, HorizontalAlignment.Right),
        RadarColumn.Seen => (68, false, HorizontalAlignment.Right),
        RadarColumn.Range => (72, false, HorizontalAlignment.Right),
        _ => (30, false, HorizontalAlignment.Center), // the icon columns
    };

    /// <summary>The age cell shows days, which sorts; the tooltip says the same age the way a person
    /// does -- years, months, days -- and gives the account's creation date (the "rez day").</summary>
    private static string AgeTooltip(AvatarBriefProfile? profile, DateTime utcNow)
    {
        if (profile == null) return "";
        if (profile.AgeHidden) return L10n.Tr("ui.radar.age_hidden");
        if (profile.AgePartsAt(utcNow) is not { } parts || profile.BornOnUtc is not { } born) return "";

        // Units that are zero are left out ("2 years, 5 days"), but an age under a day still says "0 days".
        var pieces = new List<string>(3);
        if (parts.Years > 0) pieces.Add(Counted(parts.Years, "age_year"));
        if (parts.Months > 0) pieces.Add(Counted(parts.Months, "age_month"));
        if (parts.Days > 0 || pieces.Count == 0) pieces.Add(Counted(parts.Days, "age_day"));

        // The date's pattern is itself a translation, so German reads 14.05.2007 and English 2007-05-14.
        string rezDay = born.ToString(L10n.Tr("ui.radar.age_date_format"), System.Globalization.CultureInfo.InvariantCulture);
        return string.Join(", ", pieces) + "\n" + L10n.TrFormat("ui.radar.age_rezday", rezDay);
    }

    /// <summary>"1 year" / "2 years": the singular and the plural are two keys, since languages differ
    /// in more than an appended s.</summary>
    private static string Counted(int count, string unitKey) =>
        L10n.TrFormat($"ui.radar.{unitKey}_{(count == 1 ? "one" : "other")}", count);

    private TreeItem CreateRowItem(Guid agentId)
    {
        var item = _tree.CreateItem(_root);
        // The id rides on the row itself, so a click is answered from the row that was clicked and
        // not from any parallel list that could be a refresh out of step.
        item.SetMetadata(0, agentId.ToString());
        for (int i = 0; i < _columns.Count; i++)
        {
            item.SetSelectable(i, true);
            item.SetTextAlignment(i, LayoutOf(_columns[i]).Align);
            if (_columns[i] != RadarColumn.Name) continue;
            item.SetIconMaxWidth(i, 9);
            item.SetTextOverrunBehavior(i, TextServer.OverrunBehavior.TrimEllipsis);
        }

        return item;
    }

    private void FillRow(TreeItem item, RadarRow row, DateTime utcNow, float drawDistance, float sayRange, float shoutRange)
    {
        for (int i = 0; i < _columns.Count; i++)
        {
            switch (_columns[i])
            {
                case RadarColumn.Name:
                    item.SetText(i, row.Name);
                    item.SetIcon(i, RadarIcons.Dot);
                    item.SetIconModulate(i, RadarIcons.RelationColor(row.Relation));
                    if (row.Relation == RadarRelation.Muted) item.SetCustomColor(i, RadarIcons.Muted);
                    else item.ClearCustomColor(i);
                    break;

                case RadarColumn.Voice:
                    // Empty until the voice subsystem supplies a level (MVP5-1) -- never fake data.
                    item.SetText(i, row.VoiceLevel is > 0.01f ? RadarIcons.Speaking : "");
                    break;

                case RadarColumn.InRegion:
                    item.SetText(i, row.InSameRegion ? RadarIcons.InRegion : "");
                    break;

                case RadarColumn.Typing:
                    item.SetText(i, row.IsTyping ? RadarIcons.Typing : "");
                    break;

                case RadarColumn.Sitting:
                    item.SetText(i, row.IsSitting ? RadarIcons.Sitting : "");
                    break;

                case RadarColumn.Payment:
                    item.SetText(i, RadarTable.FormatPayment(row.Profile?.Payment ?? PaymentInfo.Unknown));
                    break;

                case RadarColumn.Note:
                    bool hasNote = row.Profile?.HasNote == true;
                    item.SetText(i, hasNote ? RadarIcons.Note : "");
                    // The note is the tooltip; the Tree's tooltip is the wrapping one in RadarTree.
                    item.SetTooltipText(i, hasNote ? row.Profile!.Notes! : "");
                    break;

                case RadarColumn.Age:
                    item.SetText(i, RadarTable.FormatAge(row.Profile, utcNow));
                    item.SetTooltipText(i, AgeTooltip(row.Profile, utcNow));
                    if (row.Profile?.AgeInDays(utcNow) is < NewAccountDays) item.SetCustomColor(i, NewAccountColour);
                    else item.ClearCustomColor(i);
                    break;

                case RadarColumn.Seen:
                    item.SetText(i, RadarTable.FormatSeen(row.SeenFor));
                    break;

                case RadarColumn.Range:
                    item.SetText(i, RadarTable.FormatRange(row, drawDistance));
                    item.SetCustomColor(i, RadarTable.BandOf(row.Distance, sayRange, shoutRange) switch
                    {
                        RangeBand.Say => SayColour,
                        RangeBand.Shout => ShoutColour,
                        _ => FarColour,
                    });
                    // Heavier text only while the avatar is inside draw distance, i.e. actually rendered.
                    item.SetCustomFont(i, row.WithinDrawDistance ? _boldFont! : _regularFont!);
                    break;
            }
        }
    }

    /// <summary>Orders the Tree's rows like <c>_ordered</c> moving as few as it can: it walks both in
    /// step and only a row that is not where it belongs is moved in front of the one that is. Rows
    /// already in order -- the common case, a refresh of an unchanged list -- cost nothing.</summary>
    private void PutInOrder()
    {
        var current = _root!.GetFirstChild();
        foreach (var item in _ordered)
        {
            if (ReferenceEquals(current, item))
            {
                current = current!.GetNext();
                continue;
            }

            // Every row before this one is already settled, and the Tree holds exactly the rows in
            // _ordered, so `current` is an unsettled row and exists.
            item.MoveBefore(current!);
        }
    }

    private void UpdateTitles(IReadOnlyCollection<RadarRow> allRows, float sayRange)
    {
        for (int i = 0; i < _columns.Count; i++)
        {
            var column = _columns[i];
            string text = RadarIcons.HeaderGlyph(column) ?? L10n.Tr(RadarColumns.Info(column).TitleKey);
            if (column == RadarColumn.Name) text += " " + RadarTable.CountsSuffix(allRows, sayRange);
            if (column == _sortColumn) text += _sortAscending ? " " + RadarIcons.SortUp : " " + RadarIcons.SortDown;

            if (_titles[i] == text) continue;
            _tree.SetColumnTitle(i, text);
            _titles[i] = text;
        }
    }

    /// <summary>The range cell's two fonts, made once: the theme's own, and the same one emboldened.</summary>
    private void EnsureFonts()
    {
        if (_regularFont != null) return;
        _regularFont = _tree.GetThemeFont("font");
        _boldFont = new FontVariation { BaseFont = _regularFont, VariationEmbolden = 0.6f };
    }

    private Guid? SelectedId()
    {
        var item = _tree.GetSelected();
        return item != null && Guid.TryParse(item.GetMetadata(0).AsString(), out var id) ? id : null;
    }

    private void OnItemSelected()
    {
        if (!_suppress) SelectionChanged?.Invoke(SelectedId());
    }

    private void OnNothingSelected()
    {
        if (!_suppress) SelectionChanged?.Invoke(null);
    }

    private void OnItemActivated()
    {
        if (!_suppress && SelectedId() is { } id) RowActivated?.Invoke(id);
    }

    private void OnItemMouseSelected(Vector2 mousePosition, long mouseButtonIndex)
    {
        if (_suppress || (MouseButton)mouseButtonIndex != MouseButton.Right) return;
        if (SelectedId() is { } id) RowContextMenu?.Invoke(id, _tree.GetGlobalTransformWithCanvas() * mousePosition);
    }

    private void OnColumnTitleClicked(long column, long mouseButtonIndex)
    {
        if (column < 0 || column >= _columns.Count) return;
        TitleClicked?.Invoke(_columns[(int)column], (MouseButton)mouseButtonIndex);
    }
}
