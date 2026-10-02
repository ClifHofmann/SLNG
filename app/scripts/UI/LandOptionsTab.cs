using System.Collections.Generic;
using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>The check boxes of the Options tab, in Firestorm's order.</summary>
internal enum LandOption
{
    Fly, BuildEveryone, BuildGroup, EntryEveryone, EntryGroup, ScriptsEveryone, ScriptsGroup,
    Safe, NoPushing, ShowInSearch, Moderate, SeeAvatars,
}

/// <summary>The text rows of the Options tab.</summary>
internal enum LandOptionRow { Category, Snapshot, LandingPoint, Routing }

/// <summary>
/// FEAT-LAND-02: the read-only "Options" tab of the Land-Info window (Firestorm's About Land -> Options).
/// What the sim says the parcel allows; nothing here can be changed (FEAT-LAND-09). Which value a box
/// shows (Group follows Everyone, Safe is the inverse of damage, the region override, the rating-driven
/// Moderate/Adult box) is decided in <see cref="LandInfoFormat"/>, following
/// <c>LLPanelLandOptions::refresh</c> (<c>llfloaterland.cpp</c>:2036-2160).
///
/// <para>The Snapshot is shown as its texture id (selectable text): the window has no texture service,
/// and a preview would need one plus a GPU-cache hand-off. The tab knows nothing about the network.</para>
/// </summary>
public partial class LandOptionsTab : ScrollContainer, ILandInfoTab
{
    private readonly Dictionary<LandOption, LandReadOnlyCheck> _checks = new();
    private readonly Dictionary<LandOptionRow, Control> _rows = new();

    string ILandInfoTab.TabTitle => L10n.Tr("ui.land.tab_options");

    public override void _Ready()
    {
        var column = LandTabPage.Prepare(this);

        column.AddChild(LandTabPage.Note(L10n.Tr("ui.land.opt_allow_label")));
        var allow = LandTabPage.NewGrid(column);
        AddPair(allow, "ui.land.opt_fly", LandOption.Fly, null);
        AddPair(allow, "ui.land.opt_build", LandOption.BuildEveryone, LandOption.BuildGroup);
        AddPair(allow, "ui.land.opt_entry", LandOption.EntryEveryone, LandOption.EntryGroup);
        AddPair(allow, "ui.land.opt_scripts", LandOption.ScriptsEveryone, LandOption.ScriptsGroup);

        var flags = new VBoxContainer();
        flags.AddThemeConstantOverride("separation", 4);
        column.AddChild(flags);
        AddCheck(flags, LandOption.Safe, "ui.land.opt_safe");
        AddCheck(flags, LandOption.NoPushing, "ui.land.opt_no_push");

        // "Show Place in Search" with its category beside it, as the viewer's combo sits beside the box.
        var search = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        search.AddThemeConstantOverride("separation", 12);
        flags.AddChild(search);
        AddCheck(search, LandOption.ShowInSearch, "ui.land.opt_search");
        var category = LandTabPage.ValueLabel();
        _rows[LandOptionRow.Category] = category;
        search.AddChild(category);

        AddCheck(flags, LandOption.Moderate, "ui.land.opt_moderate");
        AddCheck(flags, LandOption.SeeAvatars, "ui.land.opt_see_avatars");

        var grid = LandTabPage.NewGrid(column);
        AddRow(grid, LandOptionRow.Snapshot, "ui.land.opt_snapshot", LandTabPage.SelectableValue());
        AddRow(grid, LandOptionRow.LandingPoint, "ui.land.opt_landing_point", LandTabPage.ValueLabel());
        AddRow(grid, LandOptionRow.Routing, "ui.land.opt_routing", LandTabPage.ValueLabel());

        Clear();
    }

    /// <inheritdoc/>
    public void ShowParcel(ParcelInfo p)
    {
        var o = p.Options;
        Show(LandOption.Fly, LandInfoFormat.Has(o, ParcelOptions.AllowFly));
        Show(LandOption.BuildEveryone, LandInfoFormat.Has(o, ParcelOptions.BuildEveryone));
        Show(LandOption.BuildGroup, LandInfoFormat.GroupTicked(o, ParcelOptions.BuildEveryone, ParcelOptions.BuildGroup));
        Show(LandOption.EntryEveryone, LandInfoFormat.Has(o, ParcelOptions.ObjectEntryEveryone));
        Show(LandOption.EntryGroup, LandInfoFormat.GroupTicked(o, ParcelOptions.ObjectEntryEveryone, ParcelOptions.ObjectEntryGroup));
        Show(LandOption.ScriptsEveryone, LandInfoFormat.Has(o, ParcelOptions.ScriptsEveryone));
        Show(LandOption.ScriptsGroup, LandInfoFormat.GroupTicked(o, ParcelOptions.ScriptsEveryone, ParcelOptions.ScriptsGroup));
        Show(LandOption.Safe, LandInfoFormat.SafeTicked(o));
        _checks[LandOption.NoPushing].Show(LandInfoFormat.NoPushing(o));
        Show(LandOption.ShowInSearch, LandInfoFormat.Has(o, ParcelOptions.ShowInSearch));
        _checks[LandOption.Moderate].Show(LandInfoFormat.ModerateContent(p.Rating, o));
        Show(LandOption.SeeAvatars, LandInfoFormat.Has(o, ParcelOptions.SeeAvatars));

        LandTabPage.SetText(_rows[LandOptionRow.Category], LandInfoFormat.CategoryText(p.Category));
        LandTabPage.SetText(_rows[LandOptionRow.Snapshot], LandInfoFormat.TextureIdText(p.SnapshotId));
        LandTabPage.SetText(_rows[LandOptionRow.LandingPoint], LandInfoFormat.LandingPointText(p.LandingPoint, p.LandingHeadingDegrees));
        LandTabPage.SetText(_rows[LandOptionRow.Routing], LandInfoFormat.RoutingText(p.TeleportRouting));
    }

    /// <inheritdoc/>
    public void RefreshNames() { } // nothing here is a name

    // --- for the selftest ----------------------------------------------------------------------------

    internal LandReadOnlyCheck Check(LandOption option) => _checks[option];

    internal string RowText(LandOptionRow row) => LandTabPage.Text(_rows[row]);

    // --- building ------------------------------------------------------------------------------------

    private void Clear()
    {
        foreach (var check in _checks.Values) check.Show(false, false);
        foreach (var row in _rows.Values) LandTabPage.SetText(row, LandInfoFormat.Unknown);
    }

    private void Show(LandOption option, bool ticked) => _checks[option].Show(ticked);

    private LandReadOnlyCheck AddCheck(Container parent, LandOption option, string labelKey)
    {
        var check = new LandReadOnlyCheck(L10n.Tr(labelKey));
        _checks[option] = check;
        parent.AddChild(check);
        return check;
    }

    private void AddPair(GridContainer grid, string keyId, LandOption everyone, LandOption? group)
    {
        var every = new LandReadOnlyCheck(L10n.Tr("ui.land.opt_everyone"));
        _checks[everyone] = every;
        Control value = every;
        if (group is { } g)
        {
            var grp = new LandReadOnlyCheck(L10n.Tr("ui.land.opt_group"));
            _checks[g] = grp;
            value = LandTabPage.Pair(every, grp);
        }
        LandTabPage.AddRow(grid, L10n.Tr(keyId), value);
    }

    private void AddRow(GridContainer grid, LandOptionRow row, string keyId, Control value)
    {
        _rows[row] = value;
        LandTabPage.AddRow(grid, L10n.Tr(keyId), value);
    }
}
