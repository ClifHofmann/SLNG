using System;
using System.Collections.Generic;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// Unified "Graphics" tab content for PreferencesWindow (Firestorm-style).
/// Combines Master Quality preset, sub-tabs (General, Hardware, Depth of Field),
/// and Graphics Profile saving/loading.
/// </summary>
public partial class GraphicsPreferencesPage : VBoxContainer
{
    private GraphicsSettings _settings = null!;
    private Action _apply = null!;
    private DofSettings? _dofSettings;
    private Action? _applyDof;
    private Func<long>? _cacheBytes;

    // Master preset controls
    private HSlider _presetSlider = null!;
    private Label _presetLabel = null!;
    private Button _presetResetBtn = null!;

    // Sub-tab selection
    private Button _btnGeneral = null!;
    private Button _btnHardware = null!;
    private Button _btnDof = null!;
    private VBoxContainer _panelGeneral = null!;
    private VBoxContainer _panelHardware = null!;
    private VBoxContainer _panelDof = null!;

    // General sub-tab controls
    private HSlider _drawSlider = null!;
    private Label _drawDistanceValue = null!;
    private HSlider _lodSlider = null!;
    private Label _lodValue = null!;
    private CheckBox _shadowsCheck = null!;
    private VBoxContainer _shadowControls = null!;
    private OptionButton _shadowSplitsOption = null!;
    private HSlider _shadowSoftnessSlider = null!;
    private Label _shadowSoftnessValue = null!;
    private HSlider _shadowDistanceSlider = null!;
    private Label _shadowDistanceValue = null!;
    private HSlider _shadowOpacitySlider = null!;
    private Label _shadowOpacityValue = null!;
    private CheckButton _smallShadowsToggle = null!;
    private CheckBox _postFxSsaoCheck = null!;
    private CheckBox _postFxSsilCheck = null!;
    private CheckBox _postFxGlowCheck = null!;
    private CheckBox _postFxReflectionProbeCheck = null!;
    private CheckBox _postFxProbeAmbientCheck = null!;
    private CheckBox _postFxSsrCheck = null!;
    private CheckBox _postFxHeroProbeCheck = null!;

    // Hardware sub-tab controls
    private OptionButton _vsyncOption = null!;
    private OptionButton _fpsOption = null!;
    private OptionButton _msaaOption = null!;
    private OptionButton _shadowResOption = null!;
    private HSlider _textureMemSlider = null!;
    private Label _textureMemValue = null!;
    private Label _textureMemUsage = null!;

    // Depth of Field sub-tab controls
    private CheckBox _dofEnableCheck = null!;
    private VBoxContainer _dofControls = null!;
    private CheckBox _dofAutoFocusCheck = null!;
    private HSlider _dofFocusSlider = null!;
    private Label _dofFocusValue = null!;
    private HSlider _dofRangeSlider = null!;
    private Label _dofRangeValue = null!;
    private HSlider _dofFalloffSlider = null!;
    private Label _dofFalloffValue = null!;
    private HSlider _dofBlurSlider = null!;
    private Label _dofBlurValue = null!;
    private CheckBox _dofNearBlurCheck = null!;
    private CheckBox _dofShowMarkerCheck = null!;

    // Profile footer controls
    private OptionButton _profileOption = null!;
    private Button _btnLoadProfile = null!;
    private Button _btnSaveProfile = null!;
    private Button _btnDeleteProfile = null!;
    private HBoxContainer _saveProfileRow = null!;
    private LineEdit _profileNameInput = null!;
    private Label _profileStatusLabel = null!;

    private bool _refreshing;
    private double _usageRefreshAccum;
    private double _statusDuration;
    private readonly List<string> _profileNames = new();

    private static readonly int[] FpsChoices = { 0, 30, 60, 90, 120, 144, 240 };
    private static readonly int[] ShadowResChoices = { 1024, 2048, 4096 };

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 10);
    }

    public void Initialize(GraphicsSettings settings, Action apply, DofSettings? dofSettings = null,
                           Action? applyDof = null, Func<long>? cacheBytes = null)
    {
        _settings = settings;
        _apply = apply;
        _dofSettings = dofSettings;
        _applyDof = applyDof;
        _cacheBytes = cacheBytes;

        BuildMasterPresetHeader();
        AddChild(new HSeparator());
        BuildSubTabBar();
        BuildGeneralPanel();
        BuildHardwarePanel();
        BuildDofPanel();
        AddChild(new HSeparator());
        BuildProfileFooter();

        ShowSubTab(0);
        Refresh();
    }

    // --- Master Preset Header ---------------------------------------------------------------
    private void BuildMasterPresetHeader()
    {
        var masterBox = new VBoxContainer();
        masterBox.AddThemeConstantOverride("separation", 4);
        AddChild(masterBox);

        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", 8);
        masterBox.AddChild(topRow);

        var titleLabel = new Label
        {
            Text = L10n.Tr("ui.preferences.master_quality"),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        titleLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f));
        topRow.AddChild(titleLabel);

        _presetSlider = new HSlider
        {
            MinValue = 0,
            MaxValue = 3,
            Step = 1,
            TicksOnBorders = true,
            TickCount = 4,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(160, 0),
            FocusMode = FocusModeEnum.None,
        };
        _presetSlider.ValueChanged += OnMasterPresetChanged;
        topRow.AddChild(_presetSlider);

        _presetLabel = new Label
        {
            Text = L10n.Tr("ui.preferences.preset_medium"),
            CustomMinimumSize = new Vector2(120, 0),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        _presetLabel.AddThemeColorOverride("font_color", new Color(0.4f, 0.8f, 1.0f));
        topRow.AddChild(_presetLabel);

        _presetResetBtn = new Button
        {
            Text = "↺",
            TooltipText = L10n.Tr("ui.preferences.camera_speed_reset"),
            FocusMode = FocusModeEnum.None,
        };
        _presetResetBtn.Pressed += () =>
        {
            if (_refreshing) return;
            _settings.ApplyPreset(GraphicsPreset.Medium);
            _apply();
            Refresh();
        };
        topRow.AddChild(_presetResetBtn);
    }

    private void OnMasterPresetChanged(double value)
    {
        if (_refreshing) return;
        var preset = (GraphicsPreset)(int)value;
        _settings.ApplyPreset(preset);
        _apply();
        Refresh();
    }

    // --- Sub-Tab Bar ------------------------------------------------------------------------
    private void BuildSubTabBar()
    {
        var tabBar = new HBoxContainer();
        tabBar.AddThemeConstantOverride("separation", 6);
        AddChild(tabBar);

        var group = new ButtonGroup();

        _btnGeneral = CreateSubTabButton(L10n.Tr("ui.preferences.subtab_general"), group, true);
        _btnHardware = CreateSubTabButton(L10n.Tr("ui.preferences.subtab_hardware"), group, false);
        _btnDof = CreateSubTabButton(L10n.Tr("ui.preferences.subtab_dof"), group, false);

        tabBar.AddChild(_btnGeneral);
        tabBar.AddChild(_btnHardware);
        tabBar.AddChild(_btnDof);

        _btnGeneral.Toggled += on => { if (on) ShowSubTab(0); };
        _btnHardware.Toggled += on => { if (on) ShowSubTab(1); };
        _btnDof.Toggled += on => { if (on) ShowSubTab(2); };
    }

    private static Button CreateSubTabButton(string text, ButtonGroup group, bool pressed)
    {
        return new Button
        {
            Text = text,
            ToggleMode = true,
            ButtonGroup = group,
            ButtonPressed = pressed,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(100, 26),
        };
    }

    private void ShowSubTab(int index)
    {
        if (_panelGeneral != null) _panelGeneral.Visible = index == 0;
        if (_panelHardware != null) _panelHardware.Visible = index == 1;
        if (_panelDof != null) _panelDof.Visible = index == 2;
    }

    // --- Sub-Tab 1: General (Allgemein) -----------------------------------------------------
    private void BuildGeneralPanel()
    {
        _panelGeneral = new VBoxContainer();
        _panelGeneral.AddThemeConstantOverride("separation", 8);
        AddChild(_panelGeneral);

        // Draw distance
        AddHeading(_panelGeneral, L10n.Tr("ui.preferences.draw_distance_heading"));
        var drawRow = new HBoxContainer();
        drawRow.AddThemeConstantOverride("separation", 12);
        _panelGeneral.AddChild(drawRow);

        _drawSlider = new HSlider
        {
            MinValue = 32,
            MaxValue = 512,
            Step = 16,
            Value = _settings.DrawDistance,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(200, 0),
            FocusMode = FocusModeEnum.None,
        };
        drawRow.AddChild(_drawSlider);

        _drawDistanceValue = new Label
        {
            Text = $"{_settings.DrawDistance:0} m",
            CustomMinimumSize = new Vector2(56, 0),
        };
        _drawDistanceValue.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        drawRow.AddChild(_drawDistanceValue);

        _drawSlider.ValueChanged += val =>
        {
            _drawDistanceValue.Text = $"{val:0} m";
            if (_refreshing) return;
            _settings.SetDrawDistance((float)val);
            _apply();
            UpdatePresetIndicator();
        };

        // Object detail (LOD)
        AddHeading(_panelGeneral, L10n.Tr("ui.preferences.object_detail_heading"));
        var lodRow = new HBoxContainer();
        lodRow.AddThemeConstantOverride("separation", 12);
        _panelGeneral.AddChild(lodRow);

        _lodSlider = new HSlider
        {
            MinValue = 0.5,
            MaxValue = 4.0,
            Step = 0.125,
            Value = _settings.VolumeLodFactor,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(200, 0),
            FocusMode = FocusModeEnum.None,
        };
        lodRow.AddChild(_lodSlider);

        _lodValue = new Label
        {
            Text = $"{_settings.VolumeLodFactor:0.###}",
            CustomMinimumSize = new Vector2(56, 0),
        };
        _lodValue.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        lodRow.AddChild(_lodValue);

        _lodSlider.ValueChanged += val =>
        {
            _lodValue.Text = $"{val:0.###}";
            if (_refreshing) return;
            _settings.SetVolumeLodFactor((float)val);
            _apply();
            UpdatePresetIndicator();
        };
        AddHint(_panelGeneral, L10n.Tr("ui.preferences.object_detail_hint"));

        _panelGeneral.AddChild(new HSeparator());

        // Shadows
        AddHeading(_panelGeneral, L10n.Tr("ui.preferences.shadows"));
        _shadowsCheck = AddCheck(_panelGeneral, L10n.Tr("ui.preferences.shadows"), _settings.Shadows, on =>
        {
            _shadowControls.Visible = on;
            if (!_refreshing)
            {
                _settings.SetShadows(on);
                _apply();
                UpdatePresetIndicator();
            }
        });

        _shadowControls = new VBoxContainer();
        _shadowControls.AddThemeConstantOverride("separation", 6);
        _shadowControls.Visible = _settings.Shadows;
        _panelGeneral.AddChild(_shadowControls);

        AddRow(_shadowControls, L10n.Tr("ui.preferences.shadow_splits"), BuildOption(
            new[]
            {
                L10n.Tr("ui.preferences.shadow_splits_0"),
                L10n.Tr("ui.preferences.shadow_splits_2"),
                L10n.Tr("ui.preferences.shadow_splits_4"),
            },
            _settings.ShadowSplits switch { 0 => 0, 1 => 1, _ => 2 },
            idx =>
            {
                if (!_refreshing)
                {
                    _settings.SetShadowSplits(idx == 2 ? 4 : idx);
                    _apply();
                    UpdatePresetIndicator();
                }
            },
            out _shadowSplitsOption));

        AddSliderRow(_shadowControls, L10n.Tr("ui.preferences.shadow_softness"), 0.1f, 3.0f, 0.1f, _settings.ShadowBlur,
                     "{0:0.0}", val => { _settings.SetShadowBlur(val); UpdatePresetIndicator(); },
                     out _shadowSoftnessSlider, out _shadowSoftnessValue);

        AddSliderRow(_shadowControls, L10n.Tr("ui.preferences.shadow_distance"), 32f, 300f, 16f, _settings.ShadowDistance,
                     "{0:0} m", val => { _settings.SetShadowDistance(val); UpdatePresetIndicator(); },
                     out _shadowDistanceSlider, out _shadowDistanceValue);

        AddSliderRow(_shadowControls, L10n.Tr("ui.preferences.shadow_opacity"), 0.1f, 1.0f, 0.05f, _settings.ShadowOpacity,
                     "{0:P0}", val => { _settings.SetShadowOpacity(val); UpdatePresetIndicator(); },
                     out _shadowOpacitySlider, out _shadowOpacityValue);

        _smallShadowsToggle = new CheckButton { ButtonPressed = _settings.SmallObjectShadows };
        _smallShadowsToggle.Toggled += pressed =>
        {
            if (!_refreshing)
            {
                _settings.SetSmallObjectShadows(pressed);
                _apply();
                UpdatePresetIndicator();
            }
        };
        AddRow(_shadowControls, L10n.Tr("ui.preferences.small_object_shadows"), _smallShadowsToggle);

        _panelGeneral.AddChild(new HSeparator());

        // Visuals & Post-FX
        AddHeading(_panelGeneral, L10n.Tr("ui.preferences.post_fx"));
        _postFxSsaoCheck = AddCheck(_panelGeneral, L10n.Tr("ui.preferences.post_fx_ssao"), _settings.PostFxSsao,
                                    on => { if (!_refreshing) { _settings.SetPostFxSsao(on); _apply(); UpdatePresetIndicator(); } });

        _postFxSsilCheck = AddCheck(_panelGeneral, L10n.Tr("ui.preferences.post_fx_ssil"), _settings.PostFxSsil,
                                    on => { if (!_refreshing) { _settings.SetPostFxSsil(on); _apply(); UpdatePresetIndicator(); } });

        _postFxGlowCheck = AddCheck(_panelGeneral, L10n.Tr("ui.preferences.post_fx_glow"), _settings.PostFxGlow,
                                    on => { if (!_refreshing) { _settings.SetPostFxGlow(on); _apply(); UpdatePresetIndicator(); } });

        _postFxReflectionProbeCheck = AddCheck(_panelGeneral, L10n.Tr("ui.preferences.post_fx_reflection_probe"), _settings.PostFxReflectionProbe,
                                              on => { if (!_refreshing) { _settings.SetPostFxReflectionProbe(on); _apply(); UpdatePresetIndicator(); } });

        _postFxProbeAmbientCheck = AddCheck(_panelGeneral, L10n.Tr("ui.preferences.post_fx_probe_ambient"), _settings.PostFxProbeAmbient,
                                            on => { if (!_refreshing) { _settings.SetPostFxProbeAmbient(on); _apply(); UpdatePresetIndicator(); } });

        _postFxSsrCheck = AddCheck(_panelGeneral, L10n.Tr("ui.preferences.post_fx_ssr"), _settings.PostFxSsr,
                                   on => { if (!_refreshing) { _settings.SetPostFxSsr(on); _apply(); UpdatePresetIndicator(); } });

        _postFxHeroProbeCheck = AddCheck(_panelGeneral, L10n.Tr("ui.preferences.post_fx_hero_probe"), _settings.PostFxHeroProbe,
                                         on => { if (!_refreshing) { _settings.SetPostFxHeroProbe(on); _apply(); UpdatePresetIndicator(); } });
    }

    // --- Sub-Tab 2: Hardware (Hardware) -----------------------------------------------------
    private void BuildHardwarePanel()
    {
        _panelHardware = new VBoxContainer();
        _panelHardware.AddThemeConstantOverride("separation", 8);
        AddChild(_panelHardware);

        AddHeading(_panelHardware, L10n.Tr("ui.preferences.graphics_heading"));

        // V-Sync
        AddRow(_panelHardware, L10n.Tr("ui.preferences.vsync"), BuildOption(
            new[]
            {
                L10n.Tr("ui.preferences.vsync_off"),
                L10n.Tr("ui.preferences.vsync_on"),
                L10n.Tr("ui.preferences.vsync_adaptive"),
                L10n.Tr("ui.preferences.vsync_mailbox"),
            },
            _settings.VSyncMode,
            idx => { if (!_refreshing) { _settings.SetVSyncMode(idx); _apply(); } },
            out _vsyncOption));
        AddHint(_panelHardware, L10n.Tr("ui.preferences.vsync_hint"));

        // FPS limit
        var fpsLabels = new string[FpsChoices.Length];
        fpsLabels[0] = L10n.Tr("ui.preferences.fps_unlimited");
        for (int i = 1; i < FpsChoices.Length; i++) fpsLabels[i] = FpsChoices[i].ToString();

        int fpsIndex = Array.IndexOf(FpsChoices, _settings.MaxFps);
        AddRow(_panelHardware, L10n.Tr("ui.preferences.fps_limit"), BuildOption(
            fpsLabels,
            fpsIndex < 0 ? 0 : fpsIndex,
            idx => { if (!_refreshing) { _settings.SetMaxFps(FpsChoices[idx]); _apply(); } },
            out _fpsOption));

        _panelHardware.AddChild(new HSeparator());

        // MSAA
        AddHeading(_panelHardware, L10n.Tr("ui.preferences.quality_heading"));
        AddRow(_panelHardware, L10n.Tr("ui.preferences.msaa"), BuildOption(
            new[] { L10n.Tr("ui.preferences.off"), "2x", "4x", "8x" },
            _settings.Msaa,
            idx => { if (!_refreshing) { _settings.SetMsaa(idx); _apply(); UpdatePresetIndicator(); } },
            out _msaaOption));

        // Shadow resolution
        int resIndex = Array.IndexOf(ShadowResChoices, _settings.ShadowResolution);
        AddRow(_panelHardware, L10n.Tr("ui.preferences.shadow_resolution"), BuildOption(
            new[]
            {
                L10n.Tr("ui.preferences.shadow_res_1024"),
                L10n.Tr("ui.preferences.shadow_res_2048"),
                L10n.Tr("ui.preferences.shadow_res_4096"),
            },
            resIndex < 0 ? 2 : resIndex,
            idx => { if (!_refreshing) { _settings.SetShadowResolution(ShadowResChoices[idx]); _apply(); UpdatePresetIndicator(); } },
            out _shadowResOption));

        _panelHardware.AddChild(new HSeparator());

        // Texture memory
        AddHeading(_panelHardware, L10n.Tr("ui.preferences.texture_memory_heading"));
        var texMemRow = new HBoxContainer();
        texMemRow.AddThemeConstantOverride("separation", 12);
        _panelHardware.AddChild(texMemRow);

        _textureMemSlider = new HSlider
        {
            MinValue = 256,
            MaxValue = 8192,
            Step = 128,
            Value = _settings.TextureMemoryMb,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(200, 0),
            FocusMode = FocusModeEnum.None,
        };
        texMemRow.AddChild(_textureMemSlider);

        _textureMemValue = new Label
        {
            Text = $"{_settings.TextureMemoryMb} MB",
            CustomMinimumSize = new Vector2(64, 0),
        };
        _textureMemValue.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        texMemRow.AddChild(_textureMemValue);

        _textureMemSlider.ValueChanged += val =>
        {
            _textureMemValue.Text = $"{val:0} MB";
            if (_refreshing) return;
            _settings.SetTextureMemoryMb((int)val);
            _apply();
        };

        _textureMemUsage = new Label { Text = "" };
        _textureMemUsage.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.65f));
        _textureMemUsage.AddThemeFontSizeOverride("font_size", 12);
        _panelHardware.AddChild(_textureMemUsage);
    }

    // --- Sub-Tab 3: Depth of Field (Schärfentiefe) -------------------------------------------
    private void BuildDofPanel()
    {
        _panelDof = new VBoxContainer();
        _panelDof.AddThemeConstantOverride("separation", 8);
        AddChild(_panelDof);

        AddHeading(_panelDof, L10n.Tr("ui.preferences.subtab_dof"));

        bool dofEnabled = _dofSettings?.Enabled ?? false;
        _dofEnableCheck = AddCheck(_panelDof, L10n.Tr("ui.snapshot.dof_enable"), dofEnabled, on =>
        {
            _dofControls.Visible = on;
            if (!_refreshing && _dofSettings != null)
            {
                _dofSettings.SetEnabled(on);
                _applyDof?.Invoke();
            }
        });

        _dofControls = new VBoxContainer();
        _dofControls.AddThemeConstantOverride("separation", 6);
        _dofControls.Visible = dofEnabled;
        _panelDof.AddChild(_dofControls);

        _dofAutoFocusCheck = AddCheck(_dofControls, L10n.Tr("ui.snapshot.dof_auto_focus"), _dofSettings?.AutoFocus ?? true, on =>
        {
            if (!_refreshing && _dofSettings != null)
            {
                _dofSettings.SetAutoFocus(on);
                _applyDof?.Invoke();
            }
        });

        AddSliderRow(_dofControls, L10n.Tr("ui.snapshot.dof_focus"), DofSettings.MinFocusDistance, DofSettings.MaxFocusDistance, 0.1f,
                     _dofSettings?.FocusDistance ?? DofSettings.DefaultFocusDistance, "{0:0.0} m",
                     val =>
                     {
                         if (_dofSettings != null)
                         {
                             if (_dofSettings.AutoFocus)
                             {
                                 _dofSettings.SetAutoFocus(false);
                                 _dofAutoFocusCheck.ButtonPressed = false;
                             }
                             _dofSettings.SetFocusDistance(val);
                             _applyDof?.Invoke();
                         }
                     },
                     out _dofFocusSlider, out _dofFocusValue);

        AddSliderRow(_dofControls, L10n.Tr("ui.snapshot.dof_range"), DofSettings.MinFocusRange, DofSettings.MaxFocusRange, 0.1f,
                     _dofSettings?.FocusRange ?? DofSettings.DefaultFocusRange, "{0:0.0} m",
                     val => { _dofSettings?.SetFocusRange(val); _applyDof?.Invoke(); },
                     out _dofRangeSlider, out _dofRangeValue);

        AddSliderRow(_dofControls, L10n.Tr("ui.snapshot.dof_falloff"), DofSettings.MinFalloff, DofSettings.MaxFalloff, 0.01f,
                     _dofSettings?.Falloff ?? DofSettings.DefaultFalloff, "{0:P0}",
                     val => { _dofSettings?.SetFalloff(val); _applyDof?.Invoke(); },
                     out _dofFalloffSlider, out _dofFalloffValue);

        AddSliderRow(_dofControls, L10n.Tr("ui.snapshot.dof_blur"), DofSettings.MinBlurAmount, DofSettings.MaxBlurAmount, 0.01f,
                     _dofSettings?.BlurAmount ?? DofSettings.DefaultBlurAmount, "{0:0.00}",
                     val => { _dofSettings?.SetBlurAmount(val); _applyDof?.Invoke(); },
                     out _dofBlurSlider, out _dofBlurValue);

        _dofNearBlurCheck = AddCheck(_dofControls, L10n.Tr("ui.snapshot.dof_near_blur"), _dofSettings?.NearBlur ?? true, on =>
        {
            if (!_refreshing && _dofSettings != null)
            {
                _dofSettings.SetNearBlur(on);
                _applyDof?.Invoke();
            }
        });

        _dofShowMarkerCheck = AddCheck(_dofControls, L10n.Tr("ui.snapshot.dof_show_marker"), _dofSettings?.ShowFocusMarker ?? false, on =>
        {
            if (!_refreshing && _dofSettings != null)
            {
                _dofSettings.SetShowFocusMarker(on);
            }
        });

        var resetDofBtn = new Button
        {
            Text = L10n.Tr("ui.snapshot.dof_reset"),
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(100, 0),
        };
        resetDofBtn.Pressed += () =>
        {
            if (_dofSettings == null) return;
            _dofSettings.ResetToDefaults();
            RefreshDof();
            _applyDof?.Invoke();
        };
        _dofControls.AddChild(resetDofBtn);
    }

    // --- Bottom Bar: Profiles ---------------------------------------------------------------
    private void BuildProfileFooter()
    {
        var footerBox = new VBoxContainer();
        footerBox.AddThemeConstantOverride("separation", 6);
        AddChild(footerBox);

        var actionRow = new HBoxContainer();
        actionRow.AddThemeConstantOverride("separation", 8);
        footerBox.AddChild(actionRow);

        var header = new Label
        {
            Text = L10n.Tr("ui.preferences.preset_header"),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        header.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        actionRow.AddChild(header);

        _profileOption = new OptionButton
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(140, 26),
            ClipText = true,
        };
        actionRow.AddChild(_profileOption);

        _btnLoadProfile = new Button
        {
            Text = L10n.Tr("ui.preferences.btn_load_preset"),
            FocusMode = FocusModeEnum.None,
        };
        _btnLoadProfile.Pressed += OnLoadProfile;
        actionRow.AddChild(_btnLoadProfile);

        _btnSaveProfile = new Button
        {
            Text = L10n.Tr("ui.preferences.btn_save_preset"),
            FocusMode = FocusModeEnum.None,
        };
        _btnSaveProfile.Pressed += () =>
        {
            _saveProfileRow.Visible = !_saveProfileRow.Visible;
            if (_saveProfileRow.Visible) _profileNameInput.GrabFocus();
        };
        actionRow.AddChild(_btnSaveProfile);

        _btnDeleteProfile = new Button
        {
            Text = L10n.Tr("ui.preferences.btn_delete_preset"),
            FocusMode = FocusModeEnum.None,
        };
        _btnDeleteProfile.Pressed += OnDeleteProfile;
        actionRow.AddChild(_btnDeleteProfile);

        // Save input row (expandable)
        _saveProfileRow = new HBoxContainer();
        _saveProfileRow.AddThemeConstantOverride("separation", 8);
        _saveProfileRow.Visible = false;
        footerBox.AddChild(_saveProfileRow);

        _profileNameInput = new LineEdit
        {
            PlaceholderText = L10n.Tr("ui.preferences.profile_name_prompt"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _profileNameInput.TextSubmitted += _ => OnConfirmSaveProfile();
        _saveProfileRow.AddChild(_profileNameInput);

        var btnConfirm = new Button
        {
            Text = "OK",
            FocusMode = FocusModeEnum.None,
        };
        btnConfirm.Pressed += OnConfirmSaveProfile;
        _saveProfileRow.AddChild(btnConfirm);

        var btnCancel = new Button
        {
            Text = "✕",
            FocusMode = FocusModeEnum.None,
        };
        btnCancel.Pressed += () => { _saveProfileRow.Visible = false; };
        _saveProfileRow.AddChild(btnCancel);

        _profileStatusLabel = new Label { Text = "" };
        _profileStatusLabel.AddThemeColorOverride("font_color", new Color(0.4f, 0.9f, 0.5f));
        _profileStatusLabel.AddThemeFontSizeOverride("font_size", 12);
        footerBox.AddChild(_profileStatusLabel);
    }

    private void OnLoadProfile()
    {
        int idx = _profileOption.Selected;
        if (idx < 0 || idx >= _profileNames.Count) return;
        string name = _profileNames[idx];
        if (_settings.LoadProfile(name))
        {
            _apply();
            Refresh();
            ShowStatus(string.Format(L10n.Tr("ui.preferences.profile_loaded"), name));
        }
    }

    private void OnConfirmSaveProfile()
    {
        string name = _profileNameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        if (_settings.SaveProfile(name))
        {
            _saveProfileRow.Visible = false;
            _profileNameInput.Text = "";
            RefreshProfiles();
            int newIdx = _profileNames.IndexOf(name);
            if (newIdx >= 0) _profileOption.Select(newIdx);
            ShowStatus(string.Format(L10n.Tr("ui.preferences.profile_saved"), name));
        }
    }

    private void OnDeleteProfile()
    {
        int idx = _profileOption.Selected;
        if (idx < 0 || idx >= _profileNames.Count) return;
        string name = _profileNames[idx];
        if (GraphicsSettings.DeleteProfile(name))
        {
            RefreshProfiles();
            ShowStatus(string.Format(L10n.Tr("ui.preferences.profile_deleted"), name));
        }
    }

    private void RefreshProfiles()
    {
        _profileOption.Clear();
        _profileNames.Clear();
        var names = GraphicsSettings.GetProfileNames();
        _profileNames.AddRange(names);

        if (_profileNames.Count == 0)
        {
            _profileOption.AddItem("-");
            _profileOption.Disabled = true;
            _btnLoadProfile.Disabled = true;
            _btnDeleteProfile.Disabled = true;
        }
        else
        {
            _profileOption.Disabled = false;
            _btnLoadProfile.Disabled = false;
            _btnDeleteProfile.Disabled = false;
            foreach (string n in _profileNames)
            {
                _profileOption.AddItem(n);
            }
            _profileOption.Select(0);
        }
    }

    private void ShowStatus(string msg)
    {
        _profileStatusLabel.Text = msg;
        _statusDuration = 4.0;
    }

    private void UpdatePresetIndicator()
    {
        var preset = _settings.DetectPreset();
        UpdatePresetDisplay(preset);
    }

    private void UpdatePresetDisplay(GraphicsPreset preset)
    {
        switch (preset)
        {
            case GraphicsPreset.Low:
                _presetLabel.Text = L10n.Tr("ui.preferences.preset_low");
                _presetSlider.SetValueNoSignal(0);
                break;
            case GraphicsPreset.Medium:
                _presetLabel.Text = L10n.Tr("ui.preferences.preset_medium");
                _presetSlider.SetValueNoSignal(1);
                break;
            case GraphicsPreset.High:
                _presetLabel.Text = L10n.Tr("ui.preferences.preset_high");
                _presetSlider.SetValueNoSignal(2);
                break;
            case GraphicsPreset.Ultra:
                _presetLabel.Text = L10n.Tr("ui.preferences.preset_ultra");
                _presetSlider.SetValueNoSignal(3);
                break;
            default:
                _presetLabel.Text = L10n.Tr("ui.preferences.preset_custom");
                break;
        }
    }

    // --- Process & Refresh ------------------------------------------------------------------
    public override void _Process(double delta)
    {
        if (_statusDuration > 0)
        {
            _statusDuration -= delta;
            if (_statusDuration <= 0) _profileStatusLabel.Text = "";
        }

        if (_textureMemUsage == null || !IsVisibleInTree()) return;
        _usageRefreshAccum += delta;
        if (_usageRefreshAccum < 0.25) return;
        _usageRefreshAccum = 0;

        long cacheMb = (_cacheBytes?.Invoke() ?? 0) >> 20;
        long gpuMb = (long)(Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0));
        _textureMemUsage.Text = cacheMb > 0
            ? $"Cache: {cacheMb} MB   ·   GPU gesamt: {gpuMb} MB"
            : $"GPU gesamt: {gpuMb} MB";
    }

    public void Refresh()
    {
        _refreshing = true;
        try
        {
            // General
            _drawSlider.Value = _settings.DrawDistance;
            _drawDistanceValue.Text = $"{_settings.DrawDistance:0} m";

            _lodSlider.Value = _settings.VolumeLodFactor;
            _lodValue.Text = $"{_settings.VolumeLodFactor:0.###}";

            _shadowsCheck.ButtonPressed = _settings.Shadows;
            _shadowControls.Visible = _settings.Shadows;
            _shadowSplitsOption.Select(_settings.ShadowSplits switch { 0 => 0, 1 => 1, _ => 2 });

            _shadowSoftnessSlider.Value = _settings.ShadowBlur;
            _shadowSoftnessValue.Text = $"{_settings.ShadowBlur:0.0}";

            _shadowDistanceSlider.Value = _settings.ShadowDistance;
            _shadowDistanceValue.Text = $"{_settings.ShadowDistance:0} m";

            _shadowOpacitySlider.Value = _settings.ShadowOpacity;
            _shadowOpacityValue.Text = $"{_settings.ShadowOpacity:P0}";

            _smallShadowsToggle.ButtonPressed = _settings.SmallObjectShadows;

            _postFxSsaoCheck.ButtonPressed = _settings.PostFxSsao;
            _postFxSsilCheck.ButtonPressed = _settings.PostFxSsil;
            _postFxGlowCheck.ButtonPressed = _settings.PostFxGlow;
            _postFxReflectionProbeCheck.ButtonPressed = _settings.PostFxReflectionProbe;
            _postFxProbeAmbientCheck.ButtonPressed = _settings.PostFxProbeAmbient;
            _postFxSsrCheck.ButtonPressed = _settings.PostFxSsr;
            _postFxHeroProbeCheck.ButtonPressed = _settings.PostFxHeroProbe;

            // Hardware
            _vsyncOption.Select(Mathf.Clamp(_settings.VSyncMode, 0, _vsyncOption.ItemCount - 1));
            int fpsIdx = Array.IndexOf(FpsChoices, _settings.MaxFps);
            _fpsOption.Select(fpsIdx < 0 ? 0 : fpsIdx);

            _msaaOption.Select(Mathf.Clamp(_settings.Msaa, 0, _msaaOption.ItemCount - 1));
            int resIdx = Array.IndexOf(ShadowResChoices, _settings.ShadowResolution);
            _shadowResOption.Select(resIdx < 0 ? 2 : resIdx);

            _textureMemSlider.Value = _settings.TextureMemoryMb;
            _textureMemValue.Text = $"{_settings.TextureMemoryMb} MB";

            // Depth of Field
            RefreshDof();

            // Preset & Profiles
            UpdatePresetIndicator();
            RefreshProfiles();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshDof()
    {
        if (_dofSettings == null) return;
        _dofEnableCheck.ButtonPressed = _dofSettings.Enabled;
        _dofControls.Visible = _dofSettings.Enabled;
        _dofAutoFocusCheck.ButtonPressed = _dofSettings.AutoFocus;
        _dofFocusSlider.Value = _dofSettings.FocusDistance;
        _dofFocusValue.Text = $"{_dofSettings.FocusDistance:0.0} m";
        _dofRangeSlider.Value = _dofSettings.FocusRange;
        _dofRangeValue.Text = $"{_dofSettings.FocusRange:0.0} m";
        _dofFalloffSlider.Value = _dofSettings.Falloff;
        _dofFalloffValue.Text = $"{_dofSettings.Falloff:P0}";
        _dofBlurSlider.Value = _dofSettings.BlurAmount;
        _dofBlurValue.Text = $"{_dofSettings.BlurAmount:0.00}";
        _dofNearBlurCheck.ButtonPressed = _dofSettings.NearBlur;
        _dofShowMarkerCheck.ButtonPressed = _dofSettings.ShowFocusMarker;
    }

    // --- Helpers ----------------------------------------------------------------------------
    private static void AddHeading(Control parent, string text)
    {
        var heading = new Label { Text = text };
        heading.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.85f));
        parent.AddChild(heading);
    }

    private static void AddHint(Control parent, string text)
    {
        var hint = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
        hint.AddThemeFontSizeOverride("font_size", 12);
        parent.AddChild(hint);
    }

    private static CheckBox AddCheck(Control parent, string label, bool value, Action<bool> onToggled)
    {
        var check = new CheckBox { Text = label, ButtonPressed = value, FocusMode = FocusModeEnum.None };
        check.SizeFlagsHorizontal = SizeFlags.Fill;
        check.Toggled += on => onToggled(on);
        parent.AddChild(check);
        return check;
    }

    private static void AddRow(Control parent, string label, Control control)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);

        var name = new Label
        {
            Text = label,
            CustomMinimumSize = new Vector2(130, 0),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.Fill,
        };
        name.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        row.AddChild(name);

        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (control is OptionButton option)
        {
            option.ClipText = true;
            option.CustomMinimumSize = new Vector2(130, 0);
        }
        row.AddChild(control);

        parent.AddChild(row);
    }

    private static OptionButton BuildOption(string[] labels, int selected, Action<int> onSelected,
                                            out OptionButton created)
    {
        var option = new OptionButton();
        created = option;
        foreach (string label in labels) option.AddItem(label);
        option.Select(Mathf.Clamp(selected, 0, labels.Length - 1));
        option.ItemSelected += idx => onSelected((int)idx);
        return option;
    }

    private static void AddSliderRow(Control parent, string label, float min, float max, float step,
                                     float current, string format, Action<float> onChanged,
                                     out HSlider slider, out Label valueLabel)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        parent.AddChild(row);

        var name = new Label
        {
            Text = label,
            CustomMinimumSize = new Vector2(130, 0),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        name.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        row.AddChild(name);

        slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = current,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
        };
        row.AddChild(slider);

        valueLabel = new Label
        {
            Text = string.Format(format, current),
            CustomMinimumSize = new Vector2(60, 0),
        };
        valueLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        row.AddChild(valueLabel);

        var capturedValLabel = valueLabel;
        var capturedSlider = slider;
        capturedSlider.ValueChanged += v =>
        {
            capturedValLabel.Text = string.Format(format, v);
            onChanged((float)v);
        };
    }
}
