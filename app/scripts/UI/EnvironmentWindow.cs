using System;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-ENV-02: pick a Windlight sky/water preset, or hand the sky back to the region.
///
/// Deliberately a PREVIEW-style picker, like the viewer's own: selecting an entry applies it
/// immediately rather than behind an "Apply" button, because judging a sky means looking at the
/// world while you scroll through the list. Nothing here is persisted — a preset lives for the
/// session, and "Region" is one click away, which is the same contract Firestorm's environment
/// floater has.
/// </summary>
public partial class EnvironmentWindow : SLNGWindow
{
    private WindlightPresetLibrary? _library;
    private EnvironmentDriver? _driver;

    private ItemList _skyList = null!;
    private ItemList _waterList = null!;
    private Label _statusLabel = null!;

    private double _statusRefreshTimer;

    public override void _Ready()
    {
        PersistId = "environment";
        base._Ready();

        Title = L10n.Tr("ui.environment.title");
        CustomMinimumSize = new Vector2(360, 320);
        Size = new Vector2(420, 420);
        Position = new Vector2(160, 120);
        Visible = false;

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        ContentContainer.AddChild(vbox);

        _statusLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 11);
        _statusLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.8f, 1f));
        vbox.AddChild(_statusLabel);

        var columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(columns);

        _skyList = AddColumn(columns, L10n.Tr("ui.environment.sky_presets"));
        _skyList.ItemSelected += OnSkySelected;

        _waterList = AddColumn(columns, L10n.Tr("ui.environment.water_presets"));
        _waterList.ItemSelected += OnWaterSelected;

        var resetButton = new Button
        {
            Text = L10n.Tr("ui.environment.use_region"),
            FocusMode = FocusModeEnum.None,
            TooltipText = L10n.Tr("ui.environment.use_region_tooltip"),
        };
        resetButton.Pressed += ResetToRegion;
        vbox.AddChild(resetButton);
    }

    /// <summary>Boot hands over the preset library and the live driver. Both are app-side objects,
    /// so the window talks to them directly rather than through callbacks — there is no protocol
    /// or world state involved in choosing a sky.</summary>
    public void Initialize(WindlightPresetLibrary library, EnvironmentDriver driver)
    {
        _library = library;
        _driver = driver;

        Fill(_skyList, library.SkyNames);
        Fill(_waterList, library.WaterNames);
        UpdateStatus();
    }

    public void Toggle()
    {
        Visible = !Visible;
        if (Visible)
        {
            SelectActive(_skyList, _driver?.SkyPresetName);
            SelectActive(_waterList, _driver?.WaterPresetName);
            UpdateStatus();
            MoveToFront();
        }
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;

        // The region's own environment changes on teleport, and the status line names it. Once a
        // second is plenty and costs nothing next to a per-frame rebuild.
        _statusRefreshTimer += delta;
        if (_statusRefreshTimer < 1.0) return;
        _statusRefreshTimer = 0;
        UpdateStatus();
    }

    private static ItemList AddColumn(Control parent, string title)
    {
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 4);
        parent.AddChild(column);

        var label = new Label { Text = title };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.75f));
        column.AddChild(label);

        var list = new ItemList
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AllowReselect = true,
            FocusMode = FocusModeEnum.None,
        };
        column.AddChild(list);
        return list;
    }

    private static void Fill(ItemList list, System.Collections.Generic.IReadOnlyList<string> names)
    {
        list.Clear();
        foreach (var name in names) list.AddItem(name);
    }

    private static void SelectActive(ItemList list, string? name)
    {
        list.DeselectAll();
        if (name == null) return;

        for (int i = 0; i < list.ItemCount; i++)
        {
            if (list.GetItemText(i) != name) continue;
            list.Select(i);
            list.EnsureCurrentIsVisible();
            return;
        }
    }

    private void OnSkySelected(long index)
    {
        if (_library == null || _driver == null) return;

        string name = _skyList.GetItemText((int)index);
        var sky = _library.LoadSky(name);
        if (sky == null)
        {
            _statusLabel.Text = L10n.TrFormat("ui.environment.load_failed", name);
            return;
        }

        _driver.SetSkyPreset(sky, name);
        UpdateStatus();
    }

    private void OnWaterSelected(long index)
    {
        if (_library == null || _driver == null) return;

        string name = _waterList.GetItemText((int)index);
        var water = _library.LoadWater(name);
        if (water == null)
        {
            _statusLabel.Text = L10n.TrFormat("ui.environment.load_failed", name);
            return;
        }

        _driver.SetWaterPreset(water, name);
        UpdateStatus();
    }

    private void ResetToRegion()
    {
        _driver?.ClearPresets();
        _skyList.DeselectAll();
        _waterList.DeselectAll();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_driver == null)
        {
            _statusLabel.Text = string.Empty;
            return;
        }

        if (!_driver.HasPresetOverride)
        {
            _statusLabel.Text = L10n.TrFormat("ui.environment.status_region", RegionSourceName());
            return;
        }

        string sky = _driver.SkyPresetName ?? L10n.TrFormat("ui.environment.status_region", RegionSourceName());
        string water = _driver.WaterPresetName ?? L10n.TrFormat("ui.environment.status_region", RegionSourceName());
        _statusLabel.Text = L10n.TrFormat("ui.environment.status_preset", sky, water);
    }

    /// <summary>Names the capability the region's environment came from, so "back to region" says
    /// what it goes back to — and so a region that sent nothing is visibly different from one that
    /// sent an EEP day cycle.</summary>
    private string RegionSourceName() => _driver?.RegionSource switch
    {
        SLNG.Core.EnvironmentSource.ExtendedEnvironment => L10n.Tr("ui.environment.source_eep"),
        SLNG.Core.EnvironmentSource.LegacyWindlight => L10n.Tr("ui.environment.source_legacy"),
        _ => L10n.Tr("ui.environment.source_default"),
    };
}
