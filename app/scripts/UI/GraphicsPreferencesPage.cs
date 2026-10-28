using System;
using System.Collections.Generic;
using Godot;
using SLNG.App.UI.Prefs;

namespace SLNG.App.UI;

/// <summary>
/// The Graphics category of Preferences (FEAT-UI-79), on the row builder: one header (quality preset, reset, the Profiles
/// menu), then View &amp; detail, Shadows, Light &amp; effects, Depth of field and Performance. No sub-tabs: what only an
/// expert needs, or what fine-tunes a main switch, is in the section's collapsed Advanced fold-out.
///
/// <para>A row whose switch is off is disabled and dimmed, not hidden, and says so with <see cref="PrefsRow.DependsOn"/>:
/// the six shadow rows (on Shadows), every depth-of-field control (on its Enable) and the real-time mirror probe (on
/// Mirrors).</para>
///
/// <para>This is a Node, not a plain object, because the readouts that change while the window is open -- the video memory
/// in use, the low-VRAM state, how long the profile status stays -- are driven from <c>_Process</c>.
/// <see cref="Build"/> puts it in the Performance section so it is in the tree. <see cref="Refresh"/> re-reads every setting
/// into its control; Boot calls it each time Preferences opens, because F3/F4 and the Graphics menu change settings from
/// outside, and everything the page does while pushing a value INTO a control is fenced off by <c>_refreshing</c> so it
/// does not come back out as the person's change.</para>
/// </summary>
public partial class GraphicsPreferencesPage : Node
{
    private const int SaveProfileMenuId = 1000;
    private const int NoProfilesMenuId = 1001;

    private static readonly int[] FpsChoices = { 0, 30, 60, 90, 120, 144, 240 };
    private static readonly int[] ShadowResChoices = { 1024, 2048, 4096 };

    private GraphicsSettings _settings = null!;
    private Action _apply = null!;
    private DofSettings? _dofSettings;
    private Action? _applyDof;
    private Func<long>? _cacheBytes;
    private Func<long>? _budgetBytes;

    private PrefsSection _performanceSection = null!;

    // Header: quality preset, profiles
    private PrefsRow _presetRow = null!;
    private HSlider _presetSlider = null!;
    private PopupMenu _profilesPopup = null!;
    private PopupMenu _deleteMenu = null!;
    private PrefsRow _saveProfileRow = null!;
    private LineEdit _profileNameInput = null!;
    private PrefsRow _profileStatusRow = null!;
    private Label _profileStatusLabel = null!;
    private readonly List<string> _profileNames = new();

    // Rows other rows depend on
    private PrefsRow _shadowsRow = null!;
    private PrefsRow _mirrorsRow = null!;
    private PrefsRow _heroProbeRow = null!;
    private PrefsRow _dofEnableRow = null!;
    private readonly List<PrefsRow> _shadowDependents = new();
    private readonly List<PrefsRow> _dofDependents = new();

    // Performance readouts
    private PrefsRow _textureMemAutoRow = null!;
    private PrefsRow _textureMemRow = null!;
    private Label _textureMemUsage = null!;
    private PrefsRow _renderThreadNotice = null!;
    private PrefsRow _lowVramStateRow = null!;
    private Label _lowVramCapsState = null!;
    private CheckBox _dofAutoFocusBox = null!;

    /// <summary>Pushes each setting into the control that shows it; <see cref="Refresh"/> runs them all.</summary>
    private readonly List<Action> _refreshers = new();
    private readonly List<Action> _dofRefreshers = new();

    private bool _refreshing;
    private double _usageRefreshAccum;
    private double _statusDuration;

    /// <summary>Fills the six Graphics sections, which Boot has already put in their places, and hands the page its
    /// settings. Call once, after the sections are in the window's tree.</summary>
    /// <param name="quality">The untitled header section: preset slider, reset, Profiles.</param>
    /// <param name="apply">Pushes the settings onto the renderer.</param>
    /// <param name="applyDof">Pushes the depth-of-field settings onto the renderer.</param>
    /// <param name="cacheBytes">What the GPU texture cache holds now (the usage readout).</param>
    /// <param name="budgetBytes">The texture-memory budget in force (what "automatic" worked out).</param>
    public void Build(PrefsSection quality, PrefsSection viewDetail, PrefsSection shadows, PrefsSection lightEffects,
        PrefsSection depthOfField, PrefsSection performance, GraphicsSettings settings, Action apply,
        DofSettings? dofSettings = null, Action? applyDof = null, Func<long>? cacheBytes = null, Func<long>? budgetBytes = null)
    {
        _settings = settings;
        _apply = apply;
        _dofSettings = dofSettings;
        _applyDof = applyDof;
        _cacheBytes = cacheBytes;
        _budgetBytes = budgetBytes;
        _performanceSection = performance;
        performance.AddChild(this);

        BuildQuality(quality);
        BuildViewDetail(viewDetail);
        BuildShadows(shadows);
        BuildLightEffects(lightEffects);
        BuildDepthOfField(depthOfField);
        BuildPerformance(performance);

        Refresh();
    }

    // --- Quality: preset, reset, profiles ---------------------------------------------------

    private void BuildQuality(PrefsSection s)
    {
        _presetRow = s.AddSlider(Id(s, "preset"), "ui.preferences.master_quality", 0, 3, 1, 1,
            v => PresetName((int)v), OnMasterPresetChanged);
        _presetSlider = _presetRow.ControlAs<HSlider>()!;
        _presetSlider.TicksOnBorders = true;
        _presetSlider.TickCount = 4;

        var tools = new HFlowContainer(); // wraps rather than pushing the page wider than a narrow window
        tools.AddThemeConstantOverride("h_separation", PrefsStyle.ColumnGap);
        tools.AddThemeConstantOverride("v_separation", PrefsStyle.RowSeparation);

        var reset = new Button
        {
            Text = "↺",
            TooltipText = L10n.Tr("ui.preferences.preset_reset"),
            FocusMode = Control.FocusModeEnum.None,
        };
        reset.Pressed += () =>
        {
            if (_refreshing) return;
            _settings.ApplyPreset(GraphicsPreset.Medium);
            _apply();
            Refresh();
        };
        tools.AddChild(reset);

        // Profiles: a saved profile loads when it is clicked; below a separator, "Save current as..." and a Delete submenu.
        var profiles = new MenuButton
        {
            Text = L10n.Tr("ui.preferences.profiles") + " ▾",
            Flat = false,
            FocusMode = Control.FocusModeEnum.None,
        };
        _profilesPopup = profiles.GetPopup();
        _deleteMenu = new PopupMenu { Name = "DeleteProfileMenu" };
        _profilesPopup.AddChild(_deleteMenu); // a submenu must be a child of the menu it hangs from
        _profilesPopup.IdPressed += OnProfileMenuId;
        _deleteMenu.IdPressed += OnDeleteProfileId;
        tools.AddChild(profiles);
        s.AddBlock(Id(s, "profiles"), tools, null, false,
            "ui.preferences.preset_reset", "ui.preferences.profiles", "ui.preferences.profile_save_as", "ui.preferences.btn_delete_preset");

        // The name field of "Save current as...", shown on demand.
        var saveBox = new HBoxContainer();
        saveBox.AddThemeConstantOverride("separation", PrefsStyle.ColumnGap);
        _profileNameInput = new LineEdit
        {
            PlaceholderText = L10n.Tr("ui.preferences.profile_name_prompt"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _profileNameInput.TextSubmitted += _ => OnConfirmSaveProfile();
        saveBox.AddChild(_profileNameInput);
        var confirm = new Button { Text = L10n.Tr("ui.preferences.profile_save_ok"), FocusMode = Control.FocusModeEnum.None };
        confirm.Pressed += OnConfirmSaveProfile;
        saveBox.AddChild(confirm);
        var cancel = new Button { Text = "✕", TooltipText = L10n.Tr("ui.preferences.profile_save_cancel"), FocusMode = Control.FocusModeEnum.None };
        cancel.Pressed += () => { _saveProfileRow.Visible = false; };
        saveBox.AddChild(cancel);
        _saveProfileRow = s.AddBlock(Id(s, "profile_save"), saveBox);
        _saveProfileRow.Visible = false;

        _profileStatusLabel = PrefsStyle.MakeStatus("");
        _profileStatusRow = s.AddBlock(Id(s, "profile_status"), _profileStatusLabel);
        _profileStatusRow.Visible = false;
    }

    private void OnMasterPresetChanged(double value)
    {
        if (_refreshing) return;
        var preset = (GraphicsPreset)(int)value;
        _settings.ApplyPreset(preset);
        _apply();
        Refresh();
    }

    private static string PresetName(int index) => index switch
    {
        0 => L10n.Tr("ui.preferences.preset_low"),
        1 => L10n.Tr("ui.preferences.preset_medium"),
        2 => L10n.Tr("ui.preferences.preset_high"),
        _ => L10n.Tr("ui.preferences.preset_ultra"),
    };

    private void OnProfileMenuId(long id)
    {
        if (id == SaveProfileMenuId)
        {
            _saveProfileRow.Visible = true;
            _profileNameInput.GrabFocus();
            return;
        }
        if (id >= 0 && id < _profileNames.Count) LoadProfile(_profileNames[(int)id]);
    }

    private void OnDeleteProfileId(long id)
    {
        if (id < 0 || id >= _profileNames.Count) return;
        string name = _profileNames[(int)id];
        if (_settings.DeleteProfile(name))
        {
            RefreshProfiles();
            ShowStatus(string.Format(L10n.Tr("ui.preferences.profile_deleted"), name));
        }
    }

    private void LoadProfile(string name)
    {
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
            ShowStatus(string.Format(L10n.Tr("ui.preferences.profile_saved"), name));
        }
    }

    /// <summary>Rebuilds the Profiles menu from what is saved; the profile in use is the one with a mark.</summary>
    private void RefreshProfiles()
    {
        _profilesPopup.Clear();
        _deleteMenu.Clear();
        _profileNames.Clear();
        _profileNames.AddRange(GraphicsSettings.GetProfileNames());

        for (int i = 0; i < _profileNames.Count; i++)
        {
            string name = _profileNames[i];
            _profilesPopup.AddRadioCheckItem(name, i);
            _profilesPopup.SetItemChecked(i, string.Equals(name, _settings.CurrentProfileName, StringComparison.OrdinalIgnoreCase));
            _deleteMenu.AddItem(name, i);
        }
        if (_profileNames.Count == 0)
        {
            _profilesPopup.AddItem(L10n.Tr("ui.preferences.profiles_none"), NoProfilesMenuId);
            _profilesPopup.SetItemDisabled(0, true);
        }

        _profilesPopup.AddSeparator();
        _profilesPopup.AddItem(L10n.Tr("ui.preferences.profile_save_as"), SaveProfileMenuId);
        _profilesPopup.AddSubmenuNodeItem(L10n.Tr("ui.preferences.btn_delete_preset"), _deleteMenu);
        _profilesPopup.SetItemDisabled(_profilesPopup.ItemCount - 1, _profileNames.Count == 0);
    }

    private void ShowStatus(string msg)
    {
        _profileStatusLabel.Text = msg;
        _profileStatusRow.Visible = true;
        _statusDuration = 4.0;
    }

    private void UpdatePresetIndicator()
    {
        var preset = _settings.DetectPreset();
        if (preset == GraphicsPreset.Custom)
        {
            _presetRow.ValueLabel!.Text = L10n.Tr("ui.preferences.preset_custom");
            return;
        }
        _presetSlider.SetValueNoSignal((int)preset);
        _presetRow.ValueLabel!.Text = PresetName((int)preset);
    }

    // --- View & detail ----------------------------------------------------------------------

    private void BuildViewDetail(PrefsSection s)
    {
        SliderRow(s, "draw_distance", "ui.preferences.draw_distance_heading", 32, 512, 16,
                () => _settings.DrawDistance, v => $"{v:0} m", v => _settings.SetDrawDistance((float)v), apply: true, preset: true)
            .WithKeywords("RenderFarClip");

        // FEAT-PERF-07: the reference viewers stop at 4. With compressed textures (FEAT-PERF-17) the GPU has room on crowded
        // regions, and a higher factor only costs triangles, not draw calls, so the slider goes further. VolumeLod stays
        // valid below 10 (its distance factor 1 - f*0.1).
        SliderRow(s, "object_detail", "ui.preferences.object_detail_heading", 0.5, 8.0, 0.125,
                () => _settings.VolumeLodFactor, v => $"{v:0.###}", v => _settings.SetVolumeLodFactor((float)v), apply: true, preset: true,
                hintKey: "ui.preferences.object_detail_hint")
            .WithKeywords("RenderVolumeLODFactor");

        // FEAT-PERF-08: the cap is "unlimited" at the top of the slider.
        SliderRow(s, "max_full_avatars", "ui.preferences.max_full_avatars_heading", 1, 51, 1,
                () => _settings.MaxFullyRenderedAvatars <= 0 ? 51 : _settings.MaxFullyRenderedAvatars,
                v => (int)v >= 51 ? L10n.Tr("ui.preferences.fps_unlimited") : $"{(int)v}",
                v => _settings.SetMaxFullyRenderedAvatars((int)v >= 51 ? 0 : (int)v), apply: true, preset: true,
                hintKey: "ui.preferences.max_full_avatars_hint")
            .WithKeywords("RenderAvatarMaxNonImpostors");

        // FEAT-PERF-14: small-object cull.
        SliderRow(s, "small_cull", "ui.preferences.small_cull_heading", 0, 8, 0.5,
            () => _settings.SmallObjectCullPixels, v => SmallCullText((float)v), v => _settings.SetSmallObjectCullPixels((float)v),
            apply: true, preset: true, hintKey: "ui.preferences.small_cull_hint", advanced: true);
    }

    private static string SmallCullText(float pixels)
        => pixels <= 0f ? L10n.Tr("ui.preferences.small_cull_off") : $"{pixels:0.#} px";

    // --- Shadows ----------------------------------------------------------------------------

    private void BuildShadows(PrefsSection s)
    {
        _shadowsRow = CheckRow(s, "shadows", "ui.preferences.shadows", () => _settings.Shadows, on => _settings.SetShadows(on),
                apply: true, preset: true, always: UpdateShadowDependents)
            .WithKeywords("RenderShadowDetail");

        int ShadowResIndex()
        {
            int index = Array.IndexOf(ShadowResChoices, _settings.ShadowResolution);
            return index < 0 ? 2 : index;
        }
        _shadowDependents.Add(OptionRow(s, "shadow_resolution", "ui.preferences.shadow_resolution",
                new[]
                {
                    L10n.Tr("ui.preferences.shadow_res_1024"),
                    L10n.Tr("ui.preferences.shadow_res_2048"),
                    L10n.Tr("ui.preferences.shadow_res_4096"),
                },
                ShadowResIndex, idx => _settings.SetShadowResolution(ShadowResChoices[idx]), apply: true, preset: true,
                optionKeys: new[] { "ui.preferences.shadow_res_1024", "ui.preferences.shadow_res_2048", "ui.preferences.shadow_res_4096" })
            .DependsOn(_shadowsRow)
            .WithKeywords("RenderShadowResolutionScale"));

        // The rows below fine-tune the Shadows switch, so they are Advanced; all of them go grey while shadows are off.
        _shadowDependents.Add(OptionRow(s, "shadow_splits", "ui.preferences.shadow_splits",
                new[]
                {
                    L10n.Tr("ui.preferences.shadow_splits_0"),
                    L10n.Tr("ui.preferences.shadow_splits_2"),
                    L10n.Tr("ui.preferences.shadow_splits_4"),
                },
                () => _settings.ShadowSplits switch { 0 => 0, 1 => 1, _ => 2 },
                idx => _settings.SetShadowSplits(idx == 2 ? 4 : idx), apply: true, preset: true, advanced: true,
                optionKeys: new[] { "ui.preferences.shadow_splits_0", "ui.preferences.shadow_splits_2", "ui.preferences.shadow_splits_4" })
            .DependsOn(_shadowsRow));

        // The three sliders were never applied to the renderer from here (it reads them itself); only the preset marker moves.
        _shadowDependents.Add(SliderRow(s, "shadow_softness", "ui.preferences.shadow_softness", 0.1, 3.0, 0.1,
                () => _settings.ShadowBlur, v => $"{v:0.0}", v => _settings.SetShadowBlur((float)v), apply: false, preset: true,
                advanced: true)
            .DependsOn(_shadowsRow));
        _shadowDependents.Add(SliderRow(s, "shadow_distance", "ui.preferences.shadow_distance", 32, 300, 16,
                () => _settings.ShadowDistance, v => $"{v:0} m", v => _settings.SetShadowDistance((float)v), apply: false, preset: true,
                advanced: true)
            .DependsOn(_shadowsRow));
        _shadowDependents.Add(SliderRow(s, "shadow_opacity", "ui.preferences.shadow_opacity", 0.1, 1.0, 0.05,
                () => _settings.ShadowOpacity, v => $"{v:P0}", v => _settings.SetShadowOpacity((float)v), apply: false, preset: true,
                advanced: true)
            .DependsOn(_shadowsRow));

        _shadowDependents.Add(CheckRow(s, "small_object_shadows", "ui.preferences.small_object_shadows",
                () => _settings.SmallObjectShadows, on => _settings.SetSmallObjectShadows(on), apply: true, preset: true, advanced: true)
            .DependsOn(_shadowsRow));

        UpdateShadowDependents(_settings.Shadows);
    }

    private void UpdateShadowDependents(bool shadowsOn)
    {
        foreach (var row in _shadowDependents) row.SetEnabled(shadowsOn);
    }

    // --- Light & effects --------------------------------------------------------------------

    private void BuildLightEffects(PrefsSection s)
    {
        CheckRow(s, "ssao", "ui.preferences.post_fx_ssao", () => _settings.PostFxSsao, on => _settings.SetPostFxSsao(on), apply: true, preset: true)
            .WithKeywords("RenderDeferredSSAO", "AO");
        CheckRow(s, "glow", "ui.preferences.post_fx_glow", () => _settings.PostFxGlow, on => _settings.SetPostFxGlow(on), apply: true, preset: true)
            .WithKeywords("RenderGlow");

        // FEAT-PERF-15: the viewer's RenderMirrors. Off on every preset; takes effect at once, no relog.
        _mirrorsRow = CheckRow(s, "mirrors", "ui.preferences.mirrors", () => _settings.Mirrors, on => _settings.SetMirrors(on),
                apply: true, preset: true, always: UpdateHeroProbeEnabled)
            .WithTooltip("ui.preferences.mirrors_tooltip")
            .WithKeywords("RenderMirrors");

        CheckRow(s, "ssil", "ui.preferences.post_fx_ssil", () => _settings.PostFxSsil, on => _settings.SetPostFxSsil(on), apply: true, preset: true, advanced: true);
        CheckRow(s, "reflection_probe", "ui.preferences.post_fx_reflection_probe", () => _settings.PostFxReflectionProbe,
            on => _settings.SetPostFxReflectionProbe(on), apply: true, preset: true, advanced: true);
        CheckRow(s, "probe_ambient", "ui.preferences.post_fx_probe_ambient", () => _settings.PostFxProbeAmbient,
            on => _settings.SetPostFxProbeAmbient(on), apply: true, preset: true, advanced: true);
        CheckRow(s, "ssr", "ui.preferences.post_fx_ssr", () => _settings.PostFxSsr, on => _settings.SetPostFxSsr(on), apply: true, preset: true, advanced: true)
            .WithKeywords("RenderScreenSpaceReflections");

        // FEAT-RENDER-22: the real-time probe behind the mirrors. It has nothing to show without them.
        _heroProbeRow = CheckRow(s, "hero_probe", "ui.preferences.post_fx_hero_probe", () => _settings.PostFxHeroProbe,
                on => _settings.SetPostFxHeroProbe(on), apply: true, preset: true, advanced: true)
            .DependsOn(_mirrorsRow);

        UpdateHeroProbeEnabled(_settings.Mirrors);
    }

    private void UpdateHeroProbeEnabled(bool mirrorsOn)
    {
        _heroProbeRow?.SetEnabled(mirrorsOn);
    }

    // --- Depth of field ---------------------------------------------------------------------

    private void BuildDepthOfField(PrefsSection s)
    {
        _dofEnableRow = s.AddCheck(Id(s, "enable"), "ui.snapshot.dof_enable", _dofSettings?.Enabled ?? false, on =>
            {
                UpdateDofDependents(on);
                if (_refreshing || _dofSettings == null) return;
                _dofSettings.SetEnabled(on);
                _applyDof?.Invoke();
            })
            .WithKeywords("RenderDepthOfField");
        var enableBox = _dofEnableRow.ControlAs<CheckBox>()!;
        _dofRefreshers.Add(() => enableBox.ButtonPressed = _dofSettings!.Enabled);

        var autoFocus = DofCheck(s, "auto_focus", "ui.snapshot.dof_auto_focus", () => _dofSettings?.AutoFocus ?? true,
            on => _dofSettings!.SetAutoFocus(on), applyDof: true);
        _dofAutoFocusBox = autoFocus.ControlAs<CheckBox>()!;

        DofSlider(s, "focus_distance", "ui.snapshot.dof_focus", DofSettings.MinFocusDistance, DofSettings.MaxFocusDistance, 0.1,
            () => _dofSettings?.FocusDistance ?? DofSettings.DefaultFocusDistance, v => $"{v:0.0} m",
            v =>
            {
                // Moving the focus by hand is taking over from the automatic focus.
                if (_dofSettings!.AutoFocus)
                {
                    _dofSettings.SetAutoFocus(false);
                    _dofAutoFocusBox.ButtonPressed = false;
                }
                _dofSettings.SetFocusDistance((float)v);
            });
        DofSlider(s, "focus_range", "ui.snapshot.dof_range", DofSettings.MinFocusRange, DofSettings.MaxFocusRange, 0.1,
            () => _dofSettings?.FocusRange ?? DofSettings.DefaultFocusRange, v => $"{v:0.0} m", v => _dofSettings!.SetFocusRange((float)v));
        DofSlider(s, "falloff", "ui.snapshot.dof_falloff", DofSettings.MinFalloff, DofSettings.MaxFalloff, 0.01,
            () => _dofSettings?.Falloff ?? DofSettings.DefaultFalloff, v => $"{v:P0}", v => _dofSettings!.SetFalloff((float)v));
        DofSlider(s, "blur", "ui.snapshot.dof_blur", DofSettings.MinBlurAmount, DofSettings.MaxBlurAmount, 0.01,
            () => _dofSettings?.BlurAmount ?? DofSettings.DefaultBlurAmount, v => $"{v:0.00}", v => _dofSettings!.SetBlurAmount((float)v));
        DofCheck(s, "near_blur", "ui.snapshot.dof_near_blur", () => _dofSettings?.NearBlur ?? true,
            on => _dofSettings!.SetNearBlur(on), applyDof: true);
        DofCheck(s, "show_marker", "ui.snapshot.dof_show_marker", () => _dofSettings?.ShowFocusMarker ?? false,
            on => _dofSettings!.SetShowFocusMarker(on), applyDof: false);

        var resetDofBtn = new Button
        {
            Text = L10n.Tr("ui.snapshot.dof_reset"),
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            CustomMinimumSize = new Vector2(100, 0),
        };
        resetDofBtn.Pressed += () =>
        {
            if (_dofSettings == null) return;
            _dofSettings.ResetToDefaults();
            RefreshDof();
            _applyDof?.Invoke();
        };
        _dofDependents.Add(s.AddBlock(Id(s, "reset"), resetDofBtn, null, false, "ui.snapshot.dof_reset").DependsOn(_dofEnableRow));

        UpdateDofDependents(_dofSettings?.Enabled ?? false);
    }

    private PrefsRow DofCheck(PrefsSection s, string name, string labelKey, Func<bool> get, Action<bool> set, bool applyDof)
    {
        var row = s.AddCheck(Id(s, name), labelKey, get(), on =>
            {
                if (_refreshing || _dofSettings == null) return;
                set(on);
                if (applyDof) _applyDof?.Invoke();
            })
            .DependsOn(_dofEnableRow);
        var box = row.ControlAs<CheckBox>()!;
        _dofRefreshers.Add(() => box.ButtonPressed = get());
        _dofDependents.Add(row);
        return row;
    }

    private PrefsRow DofSlider(PrefsSection s, string name, string labelKey, double min, double max, double step,
        Func<double> get, Func<double, string> format, Action<double> set)
    {
        var row = SliderRow(s, name, labelKey, min, max, step, get, format,
            v =>
            {
                if (_dofSettings == null) return;
                set(v);
                _applyDof?.Invoke();
            }, apply: false, preset: false, refreshers: _dofRefreshers).DependsOn(_dofEnableRow);
        _dofDependents.Add(row);
        return row;
    }

    private void UpdateDofDependents(bool enabled)
    {
        foreach (var row in _dofDependents) row.SetEnabled(enabled);
    }

    private void RefreshDof()
    {
        if (_dofSettings == null) return;
        bool wasRefreshing = _refreshing;
        _refreshing = true; // pushing the settings into the controls is not the person changing them
        try
        {
            foreach (var refresh in _dofRefreshers) refresh();
        }
        finally
        {
            _refreshing = wasRefreshing;
        }
        UpdateDofDependents(_dofSettings.Enabled);
    }

    // --- Performance ------------------------------------------------------------------------

    private void BuildPerformance(PrefsSection s)
    {
        var fpsLabels = new string[FpsChoices.Length];
        fpsLabels[0] = L10n.Tr("ui.preferences.fps_unlimited");
        for (int i = 1; i < FpsChoices.Length; i++) fpsLabels[i] = FpsChoices[i].ToString();
        OptionRow(s, "fps_limit", "ui.preferences.fps_limit", fpsLabels,
                () => Math.Max(0, Array.IndexOf(FpsChoices, _settings.MaxFps)),
                idx => _settings.SetMaxFps(FpsChoices[idx]), apply: true, preset: false,
                optionKeys: new[] { "ui.preferences.fps_unlimited" })
            .WithLiteralTexts(fpsLabels[1..])
            .WithKeywords("MaxFPS");

        OptionRow(s, "vsync", "ui.preferences.vsync",
                new[]
                {
                    L10n.Tr("ui.preferences.vsync_off"),
                    L10n.Tr("ui.preferences.vsync_on"),
                    L10n.Tr("ui.preferences.vsync_adaptive"),
                    L10n.Tr("ui.preferences.vsync_mailbox"),
                },
                () => _settings.VSyncMode, idx => _settings.SetVSyncMode(idx), apply: true, preset: false,
                hintKey: "ui.preferences.vsync_hint",
                optionKeys: new[] { "ui.preferences.vsync_off", "ui.preferences.vsync_on", "ui.preferences.vsync_adaptive", "ui.preferences.vsync_mailbox" })
            .WithKeywords("RenderVSyncEnable");

        OptionRow(s, "msaa", "ui.preferences.msaa", new[] { L10n.Tr("ui.preferences.off"), "2x", "4x", "8x" },
                () => _settings.Msaa, idx => _settings.SetMsaa(idx), apply: true, preset: true,
                optionKeys: new[] { "ui.preferences.off" })
            .WithLiteralTexts("2x", "4x", "8x")
            .WithKeywords("RenderFSAASamples");

        BuildTextureMemory(s);

        // Render thread (FEAT-PERF-13). Not a GraphicsSettings value and not part of any preset: the engine decides it before
        // the client's code runs, so it is saved where the engine reads it (RenderThread.OverridePath) and only takes effect
        // on the next start. While the saved choice differs from what this run uses, a notice says so.
        s.AddOption(Id(s, "render_thread"), "ui.preferences.render_thread",
                new[] { L10n.Tr("ui.preferences.render_thread_main"), L10n.Tr("ui.preferences.render_thread_separate") },
                RenderThread.SavedPreferenceIsSeparate() ? 1 : 0,
                idx =>
                {
                    if (_refreshing) return;
                    RenderThread.SavePreference(idx == 1);
                    UpdateRenderThreadNotice();
                },
                hintKey: "ui.preferences.render_thread_hint", advanced: true,
                optionKeys: new[] { "ui.preferences.render_thread_main", "ui.preferences.render_thread_separate" })
            .WithApply(PrefsApply.Restart);
        _renderThreadNotice = s.AddBlock(Id(s, "render_thread_pending"), PrefsStyle.MakeNotice(L10n.Tr("ui.preferences.render_thread_restart")),
            null, true, "ui.preferences.render_thread_restart");
        UpdateRenderThreadNotice();

        // FEAT-PERF-17: block-compressed (BC1/BC3) world textures. Not part of the presets.
        CheckRow(s, "compress_textures", "ui.preferences.compress_textures", () => _settings.CompressTextures,
                on => _settings.SetCompressTextures(on), apply: true, preset: false, advanced: true)
            .WithTooltip("ui.preferences.compress_textures_tooltip")
            .WithKeywords("RenderCompressTextures");

        // FEAT-PERF-25: the low-VRAM caps, and what they are doing right now.
        CheckRow(s, "low_vram_caps", "ui.preferences.low_vram_caps", () => _settings.LowVramCaps,
            on => _settings.SetLowVramCaps(on), apply: true, preset: false, hintKey: "ui.preferences.low_vram_caps_hint", advanced: true);
        _lowVramCapsState = PrefsStyle.MakeHint("");
        _lowVramStateRow = s.AddBlock(Id(s, "low_vram_state"), _lowVramCapsState, null, true);
        _lowVramStateRow.Visible = false;

        // FEAT-PERF-23: merge other avatars' worn meshes. Not part of the presets; off until the A/B.
        var outfit = s.AddCheck(Id(s, "outfit_merge"), "ui.preferences.outfit_merge", _settings.OutfitMerge, on =>
            {
                if (_refreshing) return;
                _settings.SetOutfitMerge(on);
                // A click is the user's newer word: it overrides --outfit-merge for the rest of the session.
                RenderConfig.OutfitMergeArg = null;
                _apply();
            },
            hintKey: "ui.preferences.outfit_merge_hint", advanced: true);
        var outfitBox = outfit.ControlAs<CheckBox>()!;
        _refreshers.Add(() => outfitBox.ButtonPressed = RenderConfig.OutfitMergeArg ?? _settings.OutfitMerge); // what is in effect
    }

    private void BuildTextureMemory(PrefsSection s)
    {
        _textureMemAutoRow = s.AddCheck(Id(s, "texture_memory_auto"), "ui.preferences.texture_memory_auto", _settings.TextureMemoryAuto, on =>
        {
            if (_refreshing) return;
            _settings.SetTextureMemoryAuto(on);
            _apply();
            UpdateTextureMemControls();
        });
        _textureMemAutoRow.WithKeywords("VRAM");
        var autoBox = _textureMemAutoRow.ControlAs<CheckBox>()!;
        _refreshers.Add(() => autoBox.ButtonPressed = _settings.TextureMemoryAuto);

        // Under "automatic" the slider shows the budget the viewer worked out and cannot be moved.
        _textureMemRow = s.AddSlider(Id(s, "texture_memory"), "ui.preferences.texture_memory_heading", 256, 8192, 128,
                _settings.TextureMemoryMb,
                v => _settings.TextureMemoryAuto ? AutoBudgetText(CurrentBudgetMb()) : $"{v:0} MB",
                v =>
                {
                    if (_refreshing) return;
                    _settings.SetTextureMemoryMb((int)v);
                    _apply();
                })
            .DependsOn(_textureMemAutoRow)
            .WithKeywords("VRAM");
        // The "Auto: 1920 MB" readout is wide. At the window's narrowest the slider gives way (it is greyed out while the text is that
        // long) rather than the whole page growing wider than the window.
        _textureMemRow.ControlAs<HSlider>()!.CustomMinimumSize = new Vector2(24, 0);

        _textureMemUsage = PrefsStyle.MakeHint("");
        s.AddBlock(Id(s, "texture_memory_usage"), _textureMemUsage);
    }

    private static string AutoBudgetText(long budgetMb) => L10n.TrFormat("ui.preferences.texture_memory_auto_value", budgetMb);

    private long CurrentBudgetMb() => (_budgetBytes?.Invoke() ?? ((long)_settings.TextureMemoryMb << 20)) >> 20;

    /// <summary>Shows the slider and its readout for the current mode: the budget the viewer worked out while "automatic" is
    /// on, the person's own number while it is off. The slider moves without raising its signal: its handler stores the
    /// value as the manual size, and the budget shown under "automatic" must never replace the number the person chose.</summary>
    private void UpdateTextureMemControls()
    {
        var slider = _textureMemRow.ControlAs<HSlider>()!;
        var label = _textureMemRow.ValueLabel!;
        if (_settings.TextureMemoryAuto)
        {
            _textureMemRow.SetEnabled(false);
            long currentBudgetMb = CurrentBudgetMb();
            slider.SetValueNoSignal(currentBudgetMb);
            label.Text = AutoBudgetText(currentBudgetMb);
        }
        else
        {
            _textureMemRow.SetEnabled(true);
            slider.SetValueNoSignal(_settings.TextureMemoryMb);
            label.Text = $"{_settings.TextureMemoryMb} MB";
        }
    }

    /// <summary>The notice under the render-thread row: shown once the saved choice differs from what this run is using.</summary>
    private void UpdateRenderThreadNotice()
    {
        _renderThreadNotice.Visible = RenderThread.SavedPreferenceIsSeparate() != RenderThread.IsSeparate;
    }

    // --- Process & Refresh ------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (_statusDuration > 0)
        {
            _statusDuration -= delta;
            if (_statusDuration <= 0)
            {
                _profileStatusLabel.Text = "";
                _profileStatusRow.Visible = false;
            }
        }

        if (_textureMemUsage == null || !_performanceSection.IsVisibleInTree()) return;
        _usageRefreshAccum += delta;
        if (_usageRefreshAccum < 0.25) return;
        _usageRefreshAccum = 0;

        long cacheMb = (_cacheBytes?.Invoke() ?? 0) >> 20;
        long gpuMb = (long)(Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0));
        _textureMemUsage.Text = cacheMb > 0
            ? L10n.TrFormat("ui.preferences.texture_memory_usage", cacheMb, gpuMb)
            : L10n.TrFormat("ui.preferences.texture_memory_usage_gpu", gpuMb);

        _lowVramCapsState.Text = _settings.CapChanges.Count > 0
            ? L10n.Tr("ui.preferences.low_vram_caps_active") + " " + string.Join(", ", _settings.CapChanges)
            : "";
        _lowVramStateRow.Visible = _lowVramCapsState.Text.Length > 0;

        if (_settings.TextureMemoryAuto)
        {
            long currentBudgetMb = CurrentBudgetMb();
            _textureMemRow.ValueLabel!.Text = AutoBudgetText(currentBudgetMb);
            _textureMemRow.ControlAs<HSlider>()!.SetValueNoSignal(currentBudgetMb); // see UpdateTextureMemControls
        }
    }

    /// <summary>Shows every setting in its control again. Called each time Preferences opens and after a preset or a
    /// profile changed many of them at once.</summary>
    public void Refresh()
    {
        _refreshing = true;
        try
        {
            foreach (var refresh in _refreshers) refresh();
            UpdateShadowDependents(_settings.Shadows);
            UpdateHeroProbeEnabled(_settings.Mirrors);
            UpdateTextureMemControls();

            RefreshDof();

            UpdatePresetIndicator();
            RefreshProfiles();
        }
        finally
        {
            _refreshing = false;
        }
    }

    // --- Row helpers ------------------------------------------------------------------------

    private static string Id(PrefsSection s, string name) => $"{s.CategoryId}.{s.Id}.{name}";

    /// <summary>What every setting does after it changed: tell the renderer, and move the preset marker (a setting the
    /// presets do not cover leaves the marker alone).</summary>
    private void After(bool apply, bool preset)
    {
        if (apply) _apply();
        if (preset) UpdatePresetIndicator();
    }

    /// <param name="always">Runs on every change of the checkbox, also while <see cref="Refresh"/> is pushing a value in, so
    /// the rows that depend on it follow.</param>
    private PrefsRow CheckRow(PrefsSection s, string name, string labelKey, Func<bool> get, Action<bool> set, bool apply, bool preset,
        string? hintKey = null, bool advanced = false, Action<bool>? always = null)
    {
        var row = s.AddCheck(Id(s, name), labelKey, get(), on =>
        {
            always?.Invoke(on);
            if (_refreshing) return;
            set(on);
            After(apply, preset);
        }, hintKey, advanced);
        var box = row.ControlAs<CheckBox>()!;
        _refreshers.Add(() => box.ButtonPressed = get());
        return row;
    }

    private PrefsRow SliderRow(PrefsSection s, string name, string labelKey, double min, double max, double step,
        Func<double> get, Func<double, string> format, Action<double> set, bool apply, bool preset,
        string? hintKey = null, bool advanced = false, List<Action>? refreshers = null)
    {
        var row = s.AddSlider(Id(s, name), labelKey, min, max, step, get(), format, v =>
        {
            if (_refreshing) return;
            set(v);
            After(apply, preset);
        }, hintKey, advanced);
        var slider = row.ControlAs<HSlider>()!;
        var label = row.ValueLabel!;
        (refreshers ?? _refreshers).Add(() =>
        {
            double value = get();
            slider.Value = value;
            label.Text = format(value);
        });
        return row;
    }

    private PrefsRow OptionRow(PrefsSection s, string name, string labelKey, string[] labels, Func<int> getIndex, Action<int> set,
        bool apply, bool preset, string? hintKey = null, bool advanced = false, string[]? optionKeys = null)
    {
        int Index() => Mathf.Clamp(getIndex(), 0, labels.Length - 1);
        var row = s.AddOption(Id(s, name), labelKey, labels, Index(), idx =>
        {
            if (_refreshing) return;
            set(idx);
            After(apply, preset);
        }, hintKey, advanced, optionKeys);
        var option = row.ControlAs<OptionButton>()!;
        _refreshers.Add(() => option.Select(Index()));
        return row;
    }
}
