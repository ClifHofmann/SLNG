using Godot;

namespace SLNG.App.UI;

/// <summary>
/// "Display" tab content for PreferencesWindow (FEAT-UI-07): the interface scale (an "automatic" checkbox plus a slider), bound to UiSettings. Kept as its own reusable Control (mirrors
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
        var langHeading = new Label
        {
            Text = L10n.Tr("ui.preferences.language_heading"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
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
        // FEAT-UI-30/31: the reference viewer's three nametag toggles
        // (NameTagShowGroupTitles / NameTagShowDisplayNames / NameTagShowUsernames), all default
        // on. Ordered the way the lines appear in the tag.
        var titlesCheck = new CheckBox
        {
            Text = L10n.Tr("ui.preferences.show_group_titles"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ButtonPressed = _settings.ShowGroupTitles,
            TooltipText = L10n.Tr("ui.preferences.show_group_titles_tip"),
        };
        titlesCheck.Toggled += on => _settings.SetShowGroupTitles(on);
        AddChild(titlesCheck);

        var displayCheck = new CheckBox
        {
            Text = L10n.Tr("ui.preferences.show_display_names"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ButtonPressed = _settings.ShowDisplayNames,
            TooltipText = L10n.Tr("ui.preferences.show_display_names_tip"),
        };
        displayCheck.Toggled += on => _settings.SetShowDisplayNames(on);
        AddChild(displayCheck);

        // FEAT-UI-30: NOT a display toggle -- see UiSettings.HideOwnGroupTitle. Placed with
        // them because that is where a user looks for it, but the tooltip has to say that this
        // one actually reaches other people.
        var hideOwnTitleCheck = new CheckBox
        {
            Text = L10n.Tr("ui.preferences.hide_own_group_title"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ButtonPressed = _settings.HideOwnGroupTitle,
            TooltipText = L10n.Tr("ui.preferences.hide_own_group_title_tip"),
        };
        hideOwnTitleCheck.Toggled += on => _settings.SetHideOwnGroupTitle(on);
        AddChild(hideOwnTitleCheck);

        var legacyCheck = new CheckBox
        {
            Text = L10n.Tr("ui.preferences.show_legacy_names"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ButtonPressed = _settings.ShowLegacyNames,
            TooltipText = L10n.Tr("ui.preferences.show_legacy_names_tip"),
        };
        legacyCheck.Toggled += on => _settings.SetShowLegacyNames(on);
        AddChild(legacyCheck);

        // Not a display setting, but the page has no chat section and this is where the name options are.
        var conferenceCheck = new CheckBox
        {
            Text = L10n.Tr("ui.preferences.conference_as_im"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ButtonPressed = _settings.ConferenceChatsAsIm,
            TooltipText = L10n.Tr("ui.preferences.conference_as_im_tip"),
        };
        conferenceCheck.Toggled += on => _settings.SetConferenceChatsAsIm(on);
        AddChild(conferenceCheck);

        var friendToastCheck = new CheckBox
        {
            Text = L10n.Tr("ui.preferences.friend_presence_toasts"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ButtonPressed = _settings.ShowFriendPresenceToasts,
            TooltipText = L10n.Tr("ui.preferences.friend_presence_toasts_tip"),
        };
        friendToastCheck.Toggled += on => _settings.SetShowFriendPresenceToasts(on);
        AddChild(friendToastCheck);

        
        // FEAT-UI-04: the build grid's granularity, the way a paint program exposes its grid.
        // The listed steps are the ones SL builders actually use; finer than 1 cm is below what
        // the simulator's position precision can hold anyway.
        var gridHeading = new Label { Text = L10n.Tr("ui.preferences.build_grid_heading") };
        gridHeading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        AddChild(gridHeading);

        var gridDropdown = new OptionButton { TooltipText = L10n.Tr("ui.preferences.build_grid_tip") };
        float[] steps = { 0.01f, 0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f };
        for (int i = 0; i < steps.Length; i++)
        {
            gridDropdown.AddItem($"{steps[i]:0.##} m");
            if (Mathf.IsEqualApprox(steps[i], _settings.BuildGridSpacing)) gridDropdown.Select(i);
        }
        gridDropdown.ItemSelected += index => _settings.SetBuildGridSpacing(steps[(int)index]);
        AddChild(gridDropdown);

        var rotHeading = new Label { Text = L10n.Tr("ui.preferences.build_rotation_snap_heading") };
        rotHeading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        AddChild(rotHeading);

        var rotDropdown = new OptionButton { TooltipText = L10n.Tr("ui.preferences.build_rotation_snap_tip") };
        float[] angles = { 1f, 5f, 10f, 15f, 22.5f, 30f, 45f, 90f };
        for (int i = 0; i < angles.Length; i++)
        {
            rotDropdown.AddItem($"{angles[i]:0.##}°");
            if (Mathf.IsEqualApprox(angles[i], _settings.BuildRotationSnapDegrees)) rotDropdown.Select(i);
        }
        rotDropdown.ItemSelected += index => _settings.SetBuildRotationSnapDegrees(angles[(int)index]);
        AddChild(rotDropdown);

        var separator = new HSeparator();
        separator.AddThemeConstantOverride("separation", 15);
        AddChild(separator);

        // --- Scale Settings ---
        // FEAT-UI-42: automatic (follows the display) or a chosen number. The slider shows the scale
        // in force either way; it is only editable while automatic is off.
        var heading = new Label { Text = L10n.Tr("ui.preferences.ui_scale_heading") };
        heading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        AddChild(heading);

        var autoCheck = new CheckBox
        {
            Text = L10n.Tr("ui.preferences.ui_scale_auto"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ButtonPressed = _settings.ScaleAutomatic,
            TooltipText = L10n.Tr("ui.preferences.ui_scale_auto_tip"),
        };
        AddChild(autoCheck);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        AddChild(row);

        var slider = new HSlider
        {
            MinValue = SLNG.Core.UiScalePolicy.Min,
            MaxValue = SLNG.Core.UiScalePolicy.Max,
            Step = SLNG.Core.UiScalePolicy.Step,
            Value = _settings.Scale,
            Editable = !_settings.ScaleAutomatic,
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

        // The slider's value is shown live but applied on release. Applying every step would resize
        // the very control being dragged, and the handle would chase the cursor.
        bool dragging = false;
        bool syncing = false; // true while the code, not the user, moves the slider
        slider.DragStarted += () => dragging = true;
        slider.DragEnded += changed =>
        {
            dragging = false;
            if (changed) _settings.SetScale((float)slider.Value);
        };
        slider.ValueChanged += value =>
        {
            _valueLabel.Text = FormatPercent((float)value);
            if (!dragging && !syncing) _settings.SetScale((float)value); // keyboard / click on the track
        };

        var detected = new Label();
        detected.AddThemeColorOverride("font_color", new Color(0.62f, 0.72f, 0.82f));
        detected.AddThemeFontSizeOverride("font_size", 12);
        AddChild(detected);
        void ShowDetected() => detected.Text = L10n.TrFormat("ui.preferences.ui_scale_detected", FormatPercent(UiScale.OsScale));
        ShowDetected();

        autoCheck.Toggled += on =>
        {
            _settings.SetScaleAutomatic(on);
            slider.Editable = !on;
            syncing = true;
            slider.Value = _settings.Scale; // automatic: the display's; off: the scale just in force
            syncing = false;
            _valueLabel.Text = FormatPercent(_settings.Scale);
            ShowDetected();
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
