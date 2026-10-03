using System.Globalization;
using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// Developer "Material lab": live render knobs for A/B-ing the legacy-material path against the
/// reference viewer in-world, so the right value can be found in a minute instead of by rebuilds.
/// Not persisted -- <see cref="MaterialLab"/> starts at today's behaviour on every launch. Opened from
/// Developer > Material lab.
/// </summary>
public partial class MaterialLabWindow : SLNGWindow
{
    private HSlider _specSlider = null!;
    private Label _specValue = null!;
    private CheckBox _viewerSpecCheck = null!;
    private bool _refreshing;

    public override void _Ready()
    {
        base._Ready(); // no PersistId on purpose: a dev tool does not remember its place

        Title = L10n.Tr("ui.material_lab.title");
        CustomMinimumSize = new Vector2(400, 380);
        Size = new Vector2(440, 430);
        Position = new Vector2(260, 200);
        Visible = false;

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        ContentContainer.AddChild(vbox);

        var intro = new Label
        {
            Text = L10n.Tr("ui.material_lab.intro"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(220, 0), // width floor: see AvatarHoverWindow
        };
        intro.AddThemeFontSizeOverride("font_size", 11);
        intro.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        vbox.AddChild(intro);

        var caption = new Label { Text = L10n.Tr("ui.material_lab.legacy_specular") };
        vbox.AddChild(caption);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(row);

        _specSlider = new HSlider
        {
            MinValue = 0.0,
            MaxValue = LegacyShadeMirror.MaxLegacySpecularScale,
            Step = 0.01,
            Value = MaterialLab.LegacySpecularScale,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(160, 0),
        };
        _specSlider.ValueChanged += v =>
        {
            _specValue.Text = Format((float)v);
            if (_refreshing) return;
            MaterialLab.SetLegacySpecularScale((float)v);
        };
        row.AddChild(_specSlider);

        _specValue = new Label
        {
            Text = Format(MaterialLab.LegacySpecularScale),
            CustomMinimumSize = new Vector2(44, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _specValue.AddThemeFontSizeOverride("font_size", 11);
        row.AddChild(_specValue);

        var hint = new Label
        {
            Text = L10n.Tr("ui.material_lab.legacy_specular_hint"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(220, 0), // width floor: see AvatarHoverWindow
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        vbox.AddChild(hint);

        vbox.AddChild(new HSeparator());

        _viewerSpecCheck = new CheckBox
        {
            Text = L10n.Tr("ui.material_lab.viewer_spec"),
            ButtonPressed = MaterialLab.ViewerSunSpecular,
            FocusMode = FocusModeEnum.None,
        };
        _viewerSpecCheck.Toggled += on =>
        {
            if (_refreshing) return;
            MaterialLab.SetViewerSunSpecular(on);
        };
        vbox.AddChild(_viewerSpecCheck);

        var viewerHint = new Label
        {
            Text = L10n.Tr("ui.material_lab.viewer_spec_hint"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(220, 0), // width floor: see AvatarHoverWindow
        };
        viewerHint.AddThemeFontSizeOverride("font_size", 11);
        viewerHint.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        vbox.AddChild(viewerHint);

        var reset = new Button
        {
            Text = L10n.Tr("ui.material_lab.reset"),
            FocusMode = FocusModeEnum.None,
        };
        reset.Pressed += () =>
        {
            MaterialLab.Reset();
            Refresh();
        };
        vbox.AddChild(reset);
    }

    /// <summary>Pushes <see cref="MaterialLab"/>'s value into the controls without echoing it back.</summary>
    public void Refresh()
    {
        _refreshing = true;
        _specSlider.Value = MaterialLab.LegacySpecularScale;
        _specValue.Text = Format(MaterialLab.LegacySpecularScale);
        _viewerSpecCheck.ButtonPressed = MaterialLab.ViewerSunSpecular;
        _refreshing = false;
    }

    public void Toggle() => Visible = !Visible;

    private static string Format(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
}
