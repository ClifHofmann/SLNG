using System;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// "Design / Style" tab content for PreferencesWindow.
/// </summary>
public partial class DesignPreferencesPage : VBoxContainer
{
    private GraphicsSettings _settings = null!;
    private Action _apply = null!;

    private CheckBox _shadowsCheck = null!;
    private VBoxContainer _shadowControls = null!;
    private HSlider _shadowSoftnessSlider = null!;
    private Label _shadowSoftnessValue = null!;
    private HSlider _shadowDistanceSlider = null!;
    private Label _shadowDistanceValue = null!;
    private HSlider _shadowOpacitySlider = null!;
    private Label _shadowOpacityValue = null!;
    private CheckBox _postFxSsaoCheck = null!;
    private CheckBox _postFxSsilCheck = null!;
    private CheckBox _postFxGlowCheck = null!;
    private CheckBox _postFxReflectionProbeCheck = null!;
    private CheckBox _postFxSsrCheck = null!;
    private CheckBox _postFxHeroProbeCheck = null!;

    private bool _refreshing;

    public override void _Ready() => AddThemeConstantOverride("separation", 8);

    public void Initialize(GraphicsSettings settings, Action apply)
    {
        _settings = settings;
        _apply = apply;

        AddHeading(L10n.Tr("ui.preferences.design_heading"));

        _shadowsCheck = AddCheck(L10n.Tr("ui.preferences.shadows"), _settings.Shadows,
                                 on =>
                                 {
                                     _shadowControls.Visible = on;
                                     if (!_refreshing) { _settings.SetShadows(on); _apply(); }
                                 });

        _shadowControls = new VBoxContainer();
        _shadowControls.AddThemeConstantOverride("separation", 6);
        _shadowControls.Visible = _settings.Shadows;
        AddChild(_shadowControls);

        AddSliderRow(_shadowControls, L10n.Tr("ui.preferences.shadow_softness"), 0.1f, 3.0f, 0.1f, _settings.ShadowBlur,
                     "{0:0.0}", val => _settings.SetShadowBlur(val), out _shadowSoftnessSlider, out _shadowSoftnessValue);

        AddSliderRow(_shadowControls, L10n.Tr("ui.preferences.shadow_distance"), 32f, 300f, 16f, _settings.ShadowDistance,
                     "{0:0} m", val => _settings.SetShadowDistance(val), out _shadowDistanceSlider, out _shadowDistanceValue);

        AddSliderRow(_shadowControls, L10n.Tr("ui.preferences.shadow_opacity"), 0.1f, 1.0f, 0.05f, _settings.ShadowOpacity,
                     "{0:P0}", val => _settings.SetShadowOpacity(val), out _shadowOpacitySlider, out _shadowOpacityValue);

        AddChild(new HSeparator());

        AddHeading(L10n.Tr("ui.preferences.post_fx"));

        _postFxSsaoCheck = AddCheck(L10n.Tr("ui.preferences.post_fx_ssao"), _settings.PostFxSsao,
                                on => { if (!_refreshing) { _settings.SetPostFxSsao(on); _apply(); } });

        _postFxSsilCheck = AddCheck(L10n.Tr("ui.preferences.post_fx_ssil"), _settings.PostFxSsil,
                                on => { if (!_refreshing) { _settings.SetPostFxSsil(on); _apply(); } });

        _postFxGlowCheck = AddCheck(L10n.Tr("ui.preferences.post_fx_glow"), _settings.PostFxGlow,
                                on => { if (!_refreshing) { _settings.SetPostFxGlow(on); _apply(); } });

        // FEAT-RENDER-20: the camera-following ReflectionProbe. Toggleable like every other
        // visual feature here (AGENTS.md non-negotiable #3) so it can be profiled/compared against
        // Godot's plain sky-only IBL fallback.
        _postFxReflectionProbeCheck = AddCheck(L10n.Tr("ui.preferences.post_fx_reflection_probe"), _settings.PostFxReflectionProbe,
                                on => { if (!_refreshing) { _settings.SetPostFxReflectionProbe(on); _apply(); } });

        // FEAT-RENDER-21: SSR, kept separate from the probe above -- the two fail differently
        // (probe: covers everything, wrong direction for nearby objects; SSR: correct parallax,
        // only for what is on screen), so switching them independently is how an artefact gets
        // attributed to one of them.
        _postFxSsrCheck = AddCheck(L10n.Tr("ui.preferences.post_fx_ssr"), _settings.PostFxSsr,
                                on => { if (!_refreshing) { _settings.SetPostFxSsr(on); _apply(); } });

        // FEAT-RENDER-22: the real-time mirror probe, separate again -- it is the only one of
        // the three that re-renders the scene continuously.
        _postFxHeroProbeCheck = AddCheck(L10n.Tr("ui.preferences.post_fx_hero_probe"), _settings.PostFxHeroProbe,
                                on => { if (!_refreshing) { _settings.SetPostFxHeroProbe(on); _apply(); } });

        // No hint line here any more: the only thing it ever said was that F2 toggles all three
        // at once, and F2 is gone (see Boot._Input). These three checkboxes are now the whole
        // interface to post-processing.
    }

    public void Refresh()
    {
        _refreshing = true;
        try
        {
            _shadowsCheck.ButtonPressed = _settings.Shadows;
            _shadowControls.Visible = _settings.Shadows;

            _shadowSoftnessSlider.Value = _settings.ShadowBlur;
            _shadowSoftnessValue.Text = $"{_settings.ShadowBlur:0.0}";

            _shadowDistanceSlider.Value = _settings.ShadowDistance;
            _shadowDistanceValue.Text = $"{_settings.ShadowDistance:0} m";

            _shadowOpacitySlider.Value = _settings.ShadowOpacity;
            _shadowOpacityValue.Text = $"{_settings.ShadowOpacity:P0}";

            _postFxSsaoCheck.ButtonPressed = _settings.PostFxSsao;
            _postFxSsilCheck.ButtonPressed = _settings.PostFxSsil;
            _postFxGlowCheck.ButtonPressed = _settings.PostFxGlow;
            _postFxReflectionProbeCheck.ButtonPressed = _settings.PostFxReflectionProbe;
            _postFxSsrCheck.ButtonPressed = _settings.PostFxSsr;
            _postFxHeroProbeCheck.ButtonPressed = _settings.PostFxHeroProbe;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void AddHeading(string text)
    {
        var heading = new Label { Text = text };
        heading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        AddChild(heading);
    }

    private void AddHint(string text)
    {
        var hint = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
        hint.AddThemeFontSizeOverride("font_size", 12);
        AddChild(hint);
    }

    private CheckBox AddCheck(string label, bool value, Action<bool> onToggled)
    {
        var check = new CheckBox { Text = label, ButtonPressed = value, FocusMode = FocusModeEnum.None };
        check.SizeFlagsHorizontal = SizeFlags.Fill;
        check.Toggled += pressed => onToggled(pressed);
        AddChild(check);
        return check;
    }

    private void AddSliderRow(VBoxContainer parent, string label, float min, float max, float step, float initialValue,
                              string format, Action<float> onValueChanged, out HSlider createdSlider, out Label createdValue)
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

        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = initialValue,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(160, 0),
        };
        row.AddChild(slider);

        var valueLabel = new Label
        {
            Text = string.Format(format, initialValue),
            CustomMinimumSize = new Vector2(50, 0),
        };
        valueLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        row.AddChild(valueLabel);

        slider.ValueChanged += value =>
        {
            valueLabel.Text = string.Format(format, value);
            if (_refreshing) return;
            onValueChanged((float)value);
            _apply();
        };

        createdSlider = slider;
        createdValue = valueLabel;
        parent.AddChild(row);
    }
}
