using Godot;

namespace SLNG.App.UI;

/// <summary>
/// "Display" tab content for PreferencesWindow (FEAT-UI-07): a single slider controlling the
/// global SLNGWindow scale, bound to UiSettings. Kept as its own reusable Control (mirrors
/// ToolbarPreferencesPage) so PreferencesWindow itself stays generic -- see
/// PreferencesWindow.AddTab.
/// </summary>
public partial class DisplayPreferencesPage : VBoxContainer
{
    private UiSettings _settings = null!;
    private Label _valueLabel = null!;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 10);
    }

    /// <summary>Builds the scale slider. Call once, right after this page has been added via
    /// PreferencesWindow.AddTab (mirrors ToolbarPreferencesPage.Initialize).</summary>
    public void Initialize(UiSettings settings, SLNG.Core.Services.LocalizationManager locManager)
    {
        _settings = settings;

        // --- Language Settings ---
        var langHeading = new Label { Text = L10n.Tr("ui.preferences.language_heading") };
        langHeading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        AddChild(langHeading);

        var langDropdown = new OptionButton();
        var locales = locManager.GetAvailableLocales();
        for (int i = 0; i < locales.Count; i++)
        {
            langDropdown.AddItem(locales[i]);
            if (locales[i] == _settings.Language)
                langDropdown.Select(i);
        }
        langDropdown.ItemSelected += index =>
        {
            string newLang = locales[(int)index];
            _settings.SetLanguage(newLang);
            locManager.CurrentLocale = newLang;
        };
        AddChild(langDropdown);
        
        var separator = new HSeparator();
        separator.AddThemeConstantOverride("separation", 15);
        AddChild(separator);

        // --- Scale Settings ---
        var heading = new Label { Text = L10n.Tr("ui.preferences.ui_scale_heading") };
        heading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        AddChild(heading);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        AddChild(row);

        var slider = new HSlider
        {
            MinValue = SLNGWindow.MinUiScale,
            MaxValue = SLNGWindow.MaxUiScale,
            Step = 0.05,
            Value = _settings.Scale,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(200, 0),
        };
        row.AddChild(slider);

        _valueLabel = new Label
        {
            Text = FormatPercent(_settings.Scale),
            CustomMinimumSize = new Vector2(48, 0),
        };
        _valueLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        row.AddChild(_valueLabel);

        slider.ValueChanged += value =>
        {
            float scale = (float)value;
            _valueLabel.Text = FormatPercent(scale);
            _settings.SetScale(scale);
        };

        var hint = new Label
        {
            Text = L10n.Tr("ui.preferences.ui_scale_hint"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
        hint.AddThemeFontSizeOverride("font_size", 12);
        AddChild(hint);
    }

    private static string FormatPercent(float scale) => $"{Mathf.RoundToInt(scale * 100)}%";
}
