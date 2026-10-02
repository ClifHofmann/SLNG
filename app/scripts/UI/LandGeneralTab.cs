using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>One page of the Land-Info window. A new tab is a new class implementing this plus one
/// <c>AddTab</c> call in <see cref="LandInfoWindow"/>.</summary>
internal interface ILandInfoTab
{
    /// <summary>The tab's title (already translated).</summary>
    string TabTitle { get; }

    /// <summary>Shows this parcel. Called on the main thread, again whenever the parcel changes.</summary>
    void ShowParcel(ParcelInfo parcel);

    /// <summary>A name that was still loading may have arrived; redraw what depends on names.</summary>
    void RefreshNames();
}

/// <summary>The rows of the General tab, in Firestorm's order.</summary>
internal enum LandRow
{
    Name, ParcelId, Description, Type, Rating, Owner, Group, Claimed, Price, Objects, Auction, Area, Traffic, Buyer,
}

/// <summary>
/// FEAT-LAND-01: the read-only "General" tab of the Land-Info window (Firestorm's About Land ->
/// General). Every row shows what the sim sent; the buttons the viewer has here are present but
/// disabled, because whether the agent may use them is the sim's decision and writing is a later
/// ticket (FEAT-LAND-09).
///
/// <para>The tab knows nothing about the network: names are resolved through the delegate it is
/// given, so it can be built and asserted in the selftest without a session.</para>
/// </summary>
public partial class LandGeneralTab : ScrollContainer, ILandInfoTab
{
    private LandInfoFormat.NameLookup _lookup = static (_, _) => null;
    private ParcelInfo? _parcel;

    private readonly Dictionary<LandRow, (Control Key, Control Value)> _rows = new();
    private TextEdit _description = null!;

    string ILandInfoTab.TabTitle => L10n.Tr("ui.land.tab_general");

    /// <summary>Sets how an owner / group / buyer id becomes a name. May be called again to replace it.</summary>
    internal void Initialize(LandInfoFormat.NameLookup lookup) => _lookup = lookup;

    public override void _Ready()
    {
        // ShowNever, not Disabled: Disabled would make the page demand the width of its widest line
        // and push the whole window wider (see PreferencesWindow.AddTab).
        HorizontalScrollMode = ScrollMode.ShowNever;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        margin.AddThemeConstantOverride("margin_left", 6);
        margin.AddThemeConstantOverride("margin_right", 6);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        AddChild(margin);

        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 10);
        margin.AddChild(column);

        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 6);
        column.AddChild(grid);

        AddRow(grid, LandRow.Name, "ui.land.row_name", ValueLabel());
        AddRow(grid, LandRow.ParcelId, "ui.land.row_parcel_id", new LineEdit
        {
            Editable = false, // read-only but selectable: the id is something to copy
            SelectingEnabled = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        });
        _description = new TextEdit
        {
            Editable = false,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(0, 72),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        AddRow(grid, LandRow.Description, "ui.land.row_description", _description);
        AddRow(grid, LandRow.Type, "ui.land.row_type", ValueLabel());
        AddRow(grid, LandRow.Rating, "ui.land.row_rating", ValueLabel());
        AddRow(grid, LandRow.Owner, "ui.land.row_owner", ValueLabel());
        AddRow(grid, LandRow.Group, "ui.land.row_group", ValueLabel());
        AddRow(grid, LandRow.Claimed, "ui.land.row_claimed", ValueLabel());
        AddRow(grid, LandRow.Price, "ui.land.row_price", ValueLabel());
        AddRow(grid, LandRow.Buyer, "ui.land.row_buyer", ValueLabel());
        AddRow(grid, LandRow.Objects, "ui.land.row_objects", ValueLabel());
        AddRow(grid, LandRow.Auction, "ui.land.row_auction", ValueLabel());
        AddRow(grid, LandRow.Area, "ui.land.row_area", ValueLabel());
        AddRow(grid, LandRow.Traffic, "ui.land.row_traffic", ValueLabel());

        column.AddChild(BuildActionRow());

        Clear();
    }

    /// <inheritdoc/>
    public void ShowParcel(ParcelInfo parcel)
    {
        _parcel = parcel;
        Render(parcel);
    }

    /// <inheritdoc/>
    public void RefreshNames()
    {
        if (_parcel != null) Render(_parcel);
    }

    /// <summary>The text of one row, for the selftest (which cannot read private Controls).</summary>
    internal string RowText(LandRow row) => _rows[row].Value switch
    {
        Label l => l.Text,
        LineEdit e => e.Text,
        TextEdit t => t.Text,
        _ => string.Empty,
    };

    internal bool RowVisible(LandRow row) => _rows[row].Value.Visible;

    /// <summary>The disabled write buttons, for the selftest.</summary>
    internal IReadOnlyList<Button> ActionButtons => _actionButtons;
    private readonly List<Button> _actionButtons = new();

    private void Render(ParcelInfo p)
    {
        SetText(LandRow.Name, LandInfoFormat.OrDash(p.Name));
        SetText(LandRow.ParcelId, LandInfoFormat.ParcelIdText(p.ParcelId));
        // Not reassigned when unchanged: the same text again would throw the scroll position away.
        if (_description.Text != p.Description) _description.Text = p.Description;
        SetText(LandRow.Type, LandInfoFormat.OrDash(p.LandType));
        SetText(LandRow.Rating, LandInfoFormat.RatingText(p.Rating));
        SetText(LandRow.Owner, LandInfoFormat.OwnerText(p, _lookup));
        SetText(LandRow.Group, LandInfoFormat.GroupText(p, _lookup));
        SetText(LandRow.Claimed, LandInfoFormat.ClaimedText(p.ClaimDateUtc));
        SetText(LandRow.Price, LandInfoFormat.PriceText(p));
        SetText(LandRow.Area, LandInfoFormat.AreaText(p.AreaSqm));
        SetText(LandRow.Traffic, LandInfoFormat.TrafficText(p.Dwell));

        // Only meaningful while the parcel is for sale.
        SetRowVisible(LandRow.Buyer, p.ForSale);
        SetRowVisible(LandRow.Objects, p.ForSale);
        if (p.ForSale)
        {
            SetText(LandRow.Buyer, LandInfoFormat.BuyerText(p, _lookup));
            SetText(LandRow.Objects, LandInfoFormat.ObjectsText(p));
        }

        SetRowVisible(LandRow.Auction, p.AuctionId != 0);
        if (p.AuctionId != 0) SetText(LandRow.Auction, p.AuctionId.ToString());
    }

    private void Clear()
    {
        foreach (var row in _rows.Keys) SetText(row, LandInfoFormat.Unknown);
        _description.Text = string.Empty;
        SetRowVisible(LandRow.Buyer, false);
        SetRowVisible(LandRow.Objects, false);
        SetRowVisible(LandRow.Auction, false);
    }

    private void SetText(LandRow row, string text)
    {
        switch (_rows[row].Value)
        {
            case Label l: l.Text = text; break;
            case LineEdit e: e.Text = text; break;
        }
    }

    private void SetRowVisible(LandRow row, bool visible)
    {
        var (key, value) = _rows[row];
        key.Visible = visible;
        value.Visible = visible;
    }

    private void AddRow(GridContainer grid, LandRow row, string keyId, Control value)
    {
        var key = new Label
        {
            Text = L10n.Tr(keyId),
            CustomMinimumSize = new Vector2(120, 0),
            SizeFlagsVertical = SizeFlags.ShrinkBegin,
        };
        key.AddThemeFontSizeOverride("font_size", 12);
        key.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        grid.AddChild(key);
        grid.AddChild(value);
        _rows[row] = (key, value);
    }

    private static Label ValueLabel() => new()
    {
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
    };

    /// <summary>The viewer's General-tab write actions, all disabled. Linden Sale is left out: it is a
    /// Linden-only control.</summary>
    private Control BuildActionRow()
    {
        var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        flow.AddThemeConstantOverride("h_separation", 6);
        flow.AddThemeConstantOverride("v_separation", 6);

        foreach (string id in new[]
        {
            "ui.land.btn_set_group", "ui.land.btn_deed", "ui.land.btn_sell", "ui.land.btn_cancel_sale",
            "ui.land.btn_buy", "ui.land.btn_buy_for_group", "ui.land.btn_buy_pass", "ui.land.btn_scripts",
            "ui.land.btn_abandon", "ui.land.btn_reclaim",
        })
        {
            var button = new Button
            {
                Text = L10n.Tr(id),
                Disabled = true,
                TooltipText = L10n.Tr("ui.land.not_available_yet"),
                FocusMode = FocusModeEnum.None,
            };
            _actionButtons.Add(button);
            flow.AddChild(button);
        }
        return flow;
    }
}
