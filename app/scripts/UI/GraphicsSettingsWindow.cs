using Godot;

namespace SLNG.App.UI;

/// <summary>
/// A standalone, floating window dedicated to Graphics and Photo settings.
/// Removed from the main PreferencesWindow to allow for real-time tweaking
/// while interacting with the 3D world.
/// </summary>
public partial class GraphicsSettingsWindow : SLNGWindow
{
    private TabContainer _tabContainer = null!;
    private QualityPreferencesPage _qualityPage = null!;
    private DesignPreferencesPage _designPage = null!;

    public override void _Ready()
    {
        PersistId = "graphics_settings";
        base._Ready();

        Title = L10n.Tr("ui.preferences.tab_quality"); // Or a specific key for Graphics Settings
        Visible = false;
        CustomMinimumSize = new Vector2(350, 500);
        Size = new Vector2(350, 500);
        Position = new Vector2(50, 100);

        OnCloseRequested = Hide;

        _tabContainer = new TabContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        ContentContainer.AddChild(_tabContainer);

        _qualityPage = new QualityPreferencesPage { Name = L10n.Tr("ui.preferences.tab_quality") };
        _tabContainer.AddChild(_qualityPage);

        _designPage = new DesignPreferencesPage { Name = L10n.Tr("ui.preferences.tab_design") };
        _tabContainer.AddChild(_designPage);
    }

    public void Initialize(GraphicsSettings settings, System.Action applySettings)
    {
        _qualityPage.Initialize(settings, applySettings);
        _designPage.Initialize(settings, applySettings);
    }
}
