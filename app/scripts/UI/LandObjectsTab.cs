using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Godot;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>The rows of the Objects tab's counts block, in Firestorm's order.</summary>
internal enum LandObjectsRow
{
    RegionCapacity, ParcelCapacity, ParcelImpact, OwnedByOwner, SetToGroup, OwnedByOthers, Selected, AutoReturn,
}

/// <summary>
/// FEAT-LAND-03: the read-only "Objects" tab of the Land-Info window (Firestorm's About Land -> Objects). Two
/// parts, as in the viewer: the object counts of the parcel (they ride in the same <c>ParcelProperties</c> as
/// everything else, so they follow <see cref="ShowParcel"/>), and the table of who owns objects on it, which
/// is a separate question the sim answers only on request.
///
/// <para><b>When it asks.</b> Never on its own account: the list is requested the first time the tab is
/// <see cref="SetShown">shown</see> for a parcel, again when the parcel changes while the tab is shown, and when
/// Refresh is pressed. (The viewer only ever asks from its Refresh button.) A parcel pushed while the tab is
/// hidden only clears the old list. The session sends a double click on Refresh as one request.</para>
///
/// <para><b>What it shows</b> (<see cref="LandInfoFormat.OwnersBody"/>): nothing asked yet, searching, the table,
/// "none found", "the sim did not list the owners" and "the sim did not answer". The last two are drawn as
/// problems: the sim lists owners only to an agent who may manage the parcel, and says no either with a nil
/// owner row (Second Life) or with silence (OpenSim), so an empty answer on a parcel that has objects, and no
/// answer at all, are shown as a refusal and never as an empty parcel.</para>
///
/// <para><b>Write buttons</b> (Show, Return, Return objects, the auto-return field) are present and disabled:
/// whether the agent may use them is the sim's decision and writing is FEAT-LAND-09. Refresh is a read and stays
/// enabled.</para>
///
/// <para><b>Threading.</b> The session's events fire on network threads. The handlers only queue a closure;
/// <see cref="_Process"/> runs it on the main thread (never <c>Callable.From(..).CallDeferred()</c> off the main
/// thread). Names arrive late: <see cref="RefreshNames"/> rewrites the Name cells where they stand, no row is
/// moved or rebuilt for it.</para>
/// </summary>
public partial class LandObjectsTab : ScrollContainer, ILandInfoTab
{
    private const int TypeColumn = 0, NameColumn = 1, CountColumn = 2, RecentColumn = 3;

    private readonly ConcurrentQueue<Action> _inbox = new();
    private LandInfoFormat.NameLookup _lookup = static (_, _) => null;
    private GridSession? _session;
    private Func<int, bool>? _requester;
    private bool _subscribed;

    private ParcelInfo? _parcel;
    private bool _shown;
    private (ulong Region, int LocalId)? _asked;
    private ParcelObjectOwners? _answer;
    private LandInfoFormat.OwnersBody _body = LandInfoFormat.OwnersBody.NotRequested;

    private readonly Dictionary<LandObjectsRow, Control> _rows = new();
    private readonly Dictionary<Guid, (TreeItem Item, ParcelObjectOwner Owner)> _items = new();
    private readonly List<Button> _writeButtons = new();
    private Label _bonus = null!;
    private LineEdit _autoReturn = null!;
    private Button _refresh = null!;
    private RadarTree _tree = null!;
    private Label _notice = null!;
    private int _requests;

    string ILandInfoTab.TabTitle => L10n.Tr("ui.land.tab_objects");

    /// <summary>Sets how an owner id becomes a name. May be called again to replace it.</summary>
    internal void Initialize(LandInfoFormat.NameLookup lookup) => _lookup = lookup;

    /// <summary>Hands the session to the tab: it asks it for the owner list and listens for the answer. Call once,
    /// after <see cref="Initialize"/>. A null session leaves the tab to <see cref="UseRequester"/> and the
    /// <c>OnOwners*</c> handlers.</summary>
    internal void Bind(GridSession? session)
    {
        Unsubscribe();
        _session = session;
        _requester = session == null ? null : session.RequestParcelObjectOwners;
        UpdateRefreshEnabled();
        if (session == null) return;
        session.ParcelObjectOwnersReceived += OnOwnersReceived;
        session.ParcelObjectOwnersFailed += OnOwnersFailed;
        _subscribed = true;
    }

    /// <summary>Replaces what sends the request (the session's <c>RequestParcelObjectOwners</c>); true means it
    /// was sent. A seam for the selftest, which has no session.</summary>
    internal void UseRequester(Func<int, bool>? requester)
    {
        _requester = requester;
        UpdateRefreshEnabled();
    }

    public override void _Ready()
    {
        var column = LandTabPage.Prepare(this);

        _bonus = LandTabPage.Note(string.Empty);
        column.AddChild(_bonus);

        var grid = new GridContainer { Columns = 4, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 6);
        column.AddChild(grid);

        AddCountRow(grid, LandObjectsRow.RegionCapacity, "ui.land.obj_region_capacity", withButtons: false);
        AddCountRow(grid, LandObjectsRow.ParcelCapacity, "ui.land.obj_parcel_capacity", withButtons: false);
        AddCountRow(grid, LandObjectsRow.ParcelImpact, "ui.land.obj_parcel_impact", withButtons: false);
        AddCountRow(grid, LandObjectsRow.OwnedByOwner, "ui.land.obj_owned_by_owner", withButtons: true);
        AddCountRow(grid, LandObjectsRow.SetToGroup, "ui.land.obj_set_to_group", withButtons: true);
        AddCountRow(grid, LandObjectsRow.OwnedByOthers, "ui.land.obj_owned_by_others", withButtons: true);
        AddCountRow(grid, LandObjectsRow.Selected, "ui.land.obj_selected", withButtons: false);

        // Auto-return: the viewer's editable field, here the same box read-only and disabled.
        _autoReturn = new LineEdit
        {
            Editable = false,
            CustomMinimumSize = new Vector2(64, 0),
            TooltipText = L10n.Tr("ui.land.not_available_yet"),
            FocusMode = FocusModeEnum.None,
        };
        _rows[LandObjectsRow.AutoReturn] = _autoReturn;
        var autoKey = new Label
        {
            Text = L10n.Tr("ui.land.obj_autoreturn"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        autoKey.AddThemeFontSizeOverride("font_size", 12);
        autoKey.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        grid.AddChild(autoKey);
        grid.AddChild(_autoReturn);
        grid.AddChild(new Control());
        grid.AddChild(new Control());

        column.AddChild(BuildOwnersHeader());
        column.AddChild(BuildOwnersBody());

        ClearCounts();
        SetBody(LandInfoFormat.OwnersBody.NotRequested);
    }

    // --- ILandInfoTab ------------------------------------------------------------------------------

    /// <inheritdoc/>
    public void ShowParcel(ParcelInfo parcel)
    {
        bool another = _parcel == null || parcel.RegionHandle != _parcel.RegionHandle || parcel.LocalId != _parcel.LocalId;
        _parcel = parcel;
        RenderCounts(parcel.Prims);
        UpdateRefreshEnabled();

        if (another)
        {
            // The old list belongs to the old parcel; it is not shown against the new one.
            _asked = null;
            _answer = null;
            ClearTree();
            SetBody(LandInfoFormat.OwnersBody.NotRequested);
        }
        else if (_answer != null)
        {
            ApplyAnswer(); // a count that changed since the answer can change what an empty list means
        }

        if (_shown) EnsureRequested();
    }

    /// <inheritdoc/>
    public void RefreshNames()
    {
        // In place: only the text of the Name cells. A name arriving never moves or rebuilds a row.
        foreach (var (item, owner) in _items.Values)
            item.SetText(NameColumn, LandInfoFormat.OwnerNameText(owner, _lookup));
    }

    // --- when the tab asks --------------------------------------------------------------------------

    /// <summary>Tells the tab whether it is on screen (the window calls this when its tab is picked and when the
    /// tabs appear or go). The first time it becomes visible for a parcel the owner list is requested.</summary>
    internal void SetShown(bool shown)
    {
        _shown = shown;
        if (shown) EnsureRequested();
    }

    /// <summary>Asks again for the shown parcel (the Refresh button). A read; always allowed.</summary>
    internal void Refresh()
    {
        if (_parcel != null) Request();
    }

    // Requests once per parcel; Refresh and a retry go through Request directly.
    private void EnsureRequested()
    {
        if (_parcel == null) return;
        if (_asked == (_parcel.RegionHandle, _parcel.LocalId)) return;
        Request();
    }

    private void Request()
    {
        if (_parcel == null) return;
        _asked = (_parcel.RegionHandle, _parcel.LocalId);
        _answer = null;
        ClearTree();

        if (_requester != null && _requester(_parcel.LocalId))
        {
            _requests++;
            SetBody(LandInfoFormat.OwnersBody.Loading);
        }
        else
        {
            SetBody(LandInfoFormat.OwnersBody.NotRequested); // not connected, or not a parcel
        }
    }

    // --- network-thread handlers: queue, never touch a Control ---------------------------------------

    internal void OnOwnersReceived(object? sender, ParcelObjectOwners owners) =>
        _inbox.Enqueue(() =>
        {
            // An answer for a parcel the tab is no longer about is not what it is showing.
            if (_asked == (owners.RegionHandle, owners.LocalId)) ShowOwners(owners);
        });

    internal void OnOwnersFailed(object? sender, ParcelObjectOwnersFailure failure) =>
        _inbox.Enqueue(() =>
        {
            // A late timeout must not blank a list that has arrived meanwhile.
            if (_asked == (failure.RegionHandle, failure.LocalId) && _body == LandInfoFormat.OwnersBody.Loading)
                SetBody(LandInfoFormat.OwnersBody.NoAnswer);
        });

    public override void _Process(double delta)
    {
        while (_inbox.TryDequeue(out var work)) work();
    }

    public override void _ExitTree()
    {
        Unsubscribe();
        base._ExitTree();
    }

    private void Unsubscribe()
    {
        if (!_subscribed || _session == null) return;
        _subscribed = false;
        _session.ParcelObjectOwnersReceived -= OnOwnersReceived;
        _session.ParcelObjectOwnersFailed -= OnOwnersFailed;
    }

    // --- state ---------------------------------------------------------------------------------------

    /// <summary>Shows an answer. Main thread.</summary>
    internal void ShowOwners(ParcelObjectOwners answer)
    {
        _answer = answer;
        ApplyAnswer();
    }

    private void ApplyAnswer()
    {
        if (_answer == null) return;
        var body = LandInfoFormat.OwnersAnswer(_answer, _parcel?.Prims.TotalPrims ?? 0);
        if (body == LandInfoFormat.OwnersBody.List) FillTree(_answer);
        else ClearTree();
        SetBody(body);
    }

    private void SetBody(LandInfoFormat.OwnersBody body)
    {
        _body = body;
        bool list = body == LandInfoFormat.OwnersBody.List;
        _tree.Visible = list;
        _notice.Visible = !list;
        _notice.Text = LandInfoFormat.OwnersNotice(body);
        _notice.AddThemeColorOverride("font_color", LandInfoFormat.OwnersIsProblem(body) ? ProblemText : UiTheme.SecondaryText);
    }

    private static readonly Color ProblemText = new(1f, 0.72f, 0.35f);

    private void FillTree(ParcelObjectOwners answer)
    {
        ClearTree();
        var root = _tree.CreateItem();
        foreach (var owner in LandInfoFormat.SortOwners(answer.Owners, _lookup))
        {
            var item = _tree.CreateItem(root);
            item.SetText(TypeColumn, LandInfoFormat.OwnerTypeText(owner));
            item.SetText(NameColumn, LandInfoFormat.OwnerNameText(owner, _lookup));
            item.SetText(CountColumn, LandInfoFormat.Count(owner.Count));
            item.SetTextAlignment(CountColumn, HorizontalAlignment.Right);
            item.SetText(RecentColumn, LandInfoFormat.OwnerRecentText(owner.NewestUtc));
            _items[owner.OwnerId] = (item, owner);
        }
    }

    private void ClearTree()
    {
        _items.Clear();
        _tree.Clear();
    }

    private void RenderCounts(ParcelPrimCounts c)
    {
        _bonus.Visible = c.HasBonus;
        if (c.HasBonus) _bonus.Text = LandInfoFormat.BonusText(c);

        SetRow(LandObjectsRow.RegionCapacity, LandInfoFormat.RegionCapacityText(c));
        SetRow(LandObjectsRow.ParcelCapacity, LandInfoFormat.Count(c.ParcelCapacity));
        SetRow(LandObjectsRow.ParcelImpact, LandInfoFormat.Count(c.TotalPrims));
        SetRow(LandObjectsRow.OwnedByOwner, LandInfoFormat.Count(c.OwnerPrims));
        SetRow(LandObjectsRow.SetToGroup, LandInfoFormat.Count(c.GroupPrims));
        SetRow(LandObjectsRow.OwnedByOthers, LandInfoFormat.Count(c.OtherPrims));
        SetRow(LandObjectsRow.Selected, LandInfoFormat.Count(c.SelectedPrims));
        SetRow(LandObjectsRow.AutoReturn, LandInfoFormat.AutoReturnText(c.AutoReturnMinutes));
    }

    private void ClearCounts()
    {
        _bonus.Visible = false;
        foreach (var row in _rows.Keys) SetRow(row, LandInfoFormat.Unknown);
    }

    private void SetRow(LandObjectsRow row, string text) => LandTabPage.SetText(_rows[row], text);

    // Refresh needs somewhere to send to and a parcel that is one; it is a read, so nothing else gates it.
    private void UpdateRefreshEnabled()
    {
        if (_refresh != null) _refresh.Disabled = _requester == null || _parcel == null || _parcel.LocalId <= 0;
    }

    // --- building -----------------------------------------------------------------------------------

    private void AddCountRow(GridContainer grid, LandObjectsRow row, string keyId, bool withButtons)
    {
        var key = new Label { Text = L10n.Tr(keyId), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        key.AddThemeFontSizeOverride("font_size", 12);
        key.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        grid.AddChild(key);

        var value = new Label { HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(120, 0) };
        _rows[row] = value;
        grid.AddChild(value);

        if (!withButtons)
        {
            grid.AddChild(new Control());
            grid.AddChild(new Control());
            return;
        }

        grid.AddChild(WriteButton("ui.land.obj_btn_show"));
        grid.AddChild(WriteButton("ui.land.obj_btn_return"));
    }

    // A button of the viewer's that is not available yet: present, disabled, explained.
    private Button WriteButton(string textId)
    {
        var button = new Button
        {
            Text = L10n.Tr(textId),
            Disabled = true,
            TooltipText = L10n.Tr("ui.land.not_available_yet"),
            FocusMode = FocusModeEnum.None,
        };
        _writeButtons.Add(button);
        return button;
    }

    private Control BuildOwnersHeader()
    {
        var header = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        header.AddThemeConstantOverride("separation", 6);

        var title = new Label { Text = L10n.Tr("ui.land.obj_owners"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        header.AddChild(title);

        _refresh = new Button
        {
            Text = L10n.Tr("ui.land.obj_refresh"),
            TooltipText = L10n.Tr("ui.land.obj_refresh_tip"),
            FocusMode = FocusModeEnum.None,
            Disabled = true, // until there is a parcel to ask about
        };
        _refresh.Pressed += Refresh;
        header.AddChild(_refresh);

        header.AddChild(WriteButton("ui.land.obj_btn_return_objects"));
        return header;
    }

    private Control BuildOwnersBody()
    {
        var body = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.CustomMinimumSize = new Vector2(0, 170);

        _tree = new RadarTree
        {
            HideRoot = true,
            Columns = 4,
            ColumnTitlesVisible = true,
            SelectMode = Tree.SelectModeEnum.Row,
            AllowSearch = false,
            ScrollHorizontalEnabled = true,
            FocusMode = FocusModeEnum.None,
            Visible = false,
        };
        string[] titles = { "ui.land.obj_col_type", "ui.land.obj_col_name", "ui.land.obj_col_count", "ui.land.obj_col_recent" };
        int[] widths = { 76, 0, 60, 150 };
        for (int i = 0; i < titles.Length; i++)
        {
            _tree.SetColumnTitle(i, L10n.Tr(titles[i]));
            _tree.SetColumnExpand(i, i == NameColumn);
            _tree.SetColumnExpandRatio(i, 1);
            _tree.SetColumnCustomMinimumWidth(i, widths[i]);
            _tree.SetColumnClipContent(i, true);
        }
        _tree.SetColumnTitleAlignment(CountColumn, HorizontalAlignment.Right);
        body.AddChild(_tree);
        _tree.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _notice = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            VerticalAlignment = VerticalAlignment.Top,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        body.AddChild(_notice);
        _notice.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        return body;
    }

    // --- for the selftest ----------------------------------------------------------------------------

    internal string RowText(LandObjectsRow row) => LandTabPage.Text(_rows[row]);

    internal bool BonusVisible => _bonus.Visible;

    internal string BonusText => _bonus.Text;

    internal LandInfoFormat.OwnersBody Body => _body;

    internal string NoticeText => _notice.Visible ? _notice.Text : string.Empty;

    internal bool TableVisible => _tree.Visible;

    /// <summary>The table's rows top to bottom as (type, name, count, most recent) texts.</summary>
    internal List<(string Type, string Name, string Count, string Recent)> TableRows()
    {
        var rows = new List<(string, string, string, string)>();
        for (var item = _tree.GetRoot()?.GetFirstChild(); item != null; item = item.GetNext())
            rows.Add((item.GetText(TypeColumn), item.GetText(NameColumn), item.GetText(CountColumn), item.GetText(RecentColumn)));
        return rows;
    }

    /// <summary>The disabled write buttons (Show x3, Return x3, Return objects).</summary>
    internal IReadOnlyList<Button> WriteButtons => _writeButtons;

    internal Button RefreshButton => _refresh;

    internal bool AutoReturnEditable => _autoReturn.Editable;

    /// <summary>How many requests were sent through the requester.</summary>
    internal int RequestsSent => _requests;

    internal bool Shown => _shown;
}
