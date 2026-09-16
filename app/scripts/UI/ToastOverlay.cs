using Godot;
using System;

namespace SLNG.App.UI;

/// <summary>
/// Lightweight on-screen toast notification overlay.
/// Displays a floating hint in the top-center of the screen that automatically
/// fades in and out without intercepting mouse clicks or navigation.
/// </summary>
public partial class ToastOverlay : CanvasLayer
{
    private Control _root = null!;
    private PanelContainer _panel = null!;
    private Label _label = null!;
    private Tween? _tween;

    public override void _Ready()
    {
        Layer = 105; // Above TopMenu (100) and standard HUD (10)

        _root = new Control
        {
            Name = "ToastRoot",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.Modulate = new Color(1, 1, 1, 0);
        AddChild(_root);

        _panel = new PanelContainer
        {
            Name = "ToastPanel",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 0f,
            AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.End,
            OffsetTop = 50f,
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.09f, 0.14f, 0.92f),
            BorderColor = new Color(0.18f, 0.75f, 0.85f, 0.6f),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 16,
            CornerRadiusTopRight = 16,
            CornerRadiusBottomLeft = 16,
            CornerRadiusBottomRight = 16,
            ContentMarginLeft = 20,
            ContentMarginRight = 20,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
            ShadowColor = new Color(0, 0, 0, 0.4f),
            ShadowSize = 8,
            ShadowOffset = new Vector2(0, 3),
        };
        _panel.AddThemeStyleboxOverride("panel", style);
        _root.AddChild(_panel);

        _label = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _label.AddThemeFontSizeOverride("font_size", 13);
        _label.AddThemeColorOverride("font_color", new Color(0.95f, 0.97f, 1f));
        _panel.AddChild(_label);
    }

    /// <summary>
    /// Displays a toast message that slides in, stays visible, and fades out.
    /// Safe to call repeatedly -- restarts the timer and updates the text immediately.
    /// </summary>
    public void ShowToast(string message, float duration = 2.0f)
    {
        _label.Text = message;
        _panel.OffsetTop = 40f;

        _tween?.Kill();
        _tween = CreateTween();
        _tween.SetParallel(true);
        _tween.TweenProperty(_root, "modulate:a", 1.0f, 0.15f)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);
        _tween.TweenProperty(_panel, "offset_top", 50f, 0.15f)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);

        _tween.Chain().TweenInterval(duration);
        _tween.Chain().TweenProperty(_root, "modulate:a", 0.0f, 0.35f)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.In);
    }
}
