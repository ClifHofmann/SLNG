using Godot;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-18: a full-screen loading overlay shown during a teleport, styled to match the login
/// loading screen (same navy-glass blur + teal accent) so the two read as one family. Driven
/// entirely by <c>GridSession.TeleportProgress</c> events, relayed here from <c>Boot</c> on the
/// main thread.
///
/// Deliberately NOT an <see cref="SLNGWindow"/>: the UI standard covers draggable floaters, and
/// this is a modal blocking overlay in the same family as Boot's login <c>%LoadingScreen</c> (a
/// plain <c>CenterContainer</c>), not a window. It is its own <see cref="CanvasLayer"/> at a
/// layer above the HUD (10) so it covers the HUD and every open window regardless of scene order,
/// and its backing rect eats mouse input so nothing behind it is clickable while a teleport is in
/// flight.
///
/// Teleport has no meaningful percentage (the sim reports discrete stages, not progress), so the
/// ring is an indeterminate spinner rather than a fake progress bar — the same honesty rule the
/// login checklist follows by only advancing on real milestones.
/// </summary>
public partial class TeleportOverlay : CanvasLayer
{
    private static readonly Color Accent = new(0.176471f, 0.831373f, 0.74902f);
    private static readonly Color MutedText = new(0.580392f, 0.639216f, 0.721569f);

    private Control _root = null!;
    private ColorRect _dim = null!;
    private Label _statusLabel = null!;
    private SpinnerRing _spinner = null!;
    private Tween? _fadeTween;

    // How long a terminal state lingers before the overlay fades out. Success clears fast (the
    // world is already there); a failure holds long enough to read the reason.
    private const double SuccessHoldSeconds = 0.4;
    private const double FailureHoldSeconds = 3.0;
    private double _hideAtMsec = double.MaxValue;

    public override void _Ready()
    {
        Layer = 100;
        Visible = false;

        _root = new Control
        {
            Name = "Root",
            MouseFilter = Control.MouseFilterEnum.Stop, // swallow every click while shown
        };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.Modulate = new Color(1, 1, 1, 0);
        AddChild(_root);

        _dim = new ColorRect
        {
            Name = "Dim",
            Color = new Color(0.016f, 0.027f, 0.047f, 0.82f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var blur = GD.Load<Material>("res://materials/ui_blur.tres");
        if (blur != null) _dim.Material = blur;
        _root.AddChild(_dim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        center.MouseFilter = Control.MouseFilterEnum.Ignore;
        _root.AddChild(center);

        var card = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.55f),
            BorderColor = new Color(Accent.R, Accent.G, Accent.B, 0.35f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 14,
            CornerRadiusTopRight = 14,
            CornerRadiusBottomLeft = 14,
            CornerRadiusBottomRight = 14,
            ContentMarginTop = 40,
            ContentMarginBottom = 40,
            ContentMarginLeft = 56,
            ContentMarginRight = 56,
        });
        center.AddChild(card);

        var vbox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        vbox.AddThemeConstantOverride("separation", 22);
        card.AddChild(vbox);

        _spinner = new SpinnerRing
        {
            CustomMinimumSize = new Vector2(84, 84),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            AccentColor = Accent,
        };
        vbox.AddChild(_spinner);

        _statusLabel = new Label
        {
            Text = string.Empty,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(340, 0),
        };
        _statusLabel.AddThemeColorOverride("font_color", MutedText);
        _statusLabel.AddThemeFontSizeOverride("font_size", 15);
        vbox.AddChild(_statusLabel);
    }

    /// <summary>Show the overlay (or refresh its text if already shown). Cancels any pending
    /// fade-out — e.g. a fresh teleport started while the previous one's success state was still
    /// lingering on screen.</summary>
    public void ShowProgress(string statusText)
    {
        _hideAtMsec = double.MaxValue;
        _statusLabel.AddThemeColorOverride("font_color", MutedText); // reset from a prior Finish()
        _statusLabel.Text = statusText;
        _spinner.Visible = true;

        Visible = true;
        FadeTo(1f, 0.18);
    }

    public void SetStatus(string statusText)
    {
        if (!Visible) return;
        _statusLabel.Text = statusText;
    }

    /// <summary>Dismiss immediately with no lingering terminal state — for a disconnect / relogin
    /// that happens while a teleport overlay is still up (its session, and therefore its remaining
    /// progress events, are gone).</summary>
    public void ForceHide()
    {
        _fadeTween?.Kill();
        _hideAtMsec = double.MaxValue;
        _root.Modulate = new Color(1, 1, 1, 0);
        Visible = false;
    }

    /// <summary>Enter a terminal state: keep the panel up briefly (long enough to read a failure
    /// reason), then fade out. A success clears almost immediately.</summary>
    public void Finish(bool success, string message)
    {
        if (!Visible) return;

        _spinner.Visible = false;
        _statusLabel.Text = message;
        _statusLabel.AddThemeColorOverride("font_color",
            success ? Accent : new Color(1f, 0.45f, 0.45f));

        _hideAtMsec = Time.GetTicksMsec() + (success ? SuccessHoldSeconds : FailureHoldSeconds) * 1000.0;
    }

    public override void _Process(double delta)
    {
        if (Visible && Time.GetTicksMsec() >= _hideAtMsec)
        {
            _hideAtMsec = double.MaxValue;
            FadeTo(0f, 0.3, thenHide: true);
        }
    }

    private void FadeTo(float targetAlpha, double seconds, bool thenHide = false)
    {
        _fadeTween?.Kill();
        _fadeTween = CreateTween();
        _fadeTween.TweenProperty(_root, "modulate:a", targetAlpha, seconds)
            .SetTrans(Tween.TransitionType.Sine)
            .SetEase(Tween.EaseType.Out);
        if (thenHide) _fadeTween.TweenCallback(Callable.From(() => Visible = false));
    }

    /// <summary>An indeterminate circular spinner drawn each frame — a faint full ring with a
    /// bright arc sweeping around it. Cheaper and simpler than building the login screen's
    /// runtime ring texture, and correct for a process with no measurable percentage.</summary>
    private sealed partial class SpinnerRing : Control
    {
        public Color AccentColor { get; set; } = Colors.White;
        private float _phase;

        public override void _Process(double delta)
        {
            if (!IsVisibleInTree()) return; // no work while the overlay is hidden
            _phase += (float)delta * 3.2f; // ~0.5 rev/s
            if (_phase > Mathf.Tau) _phase -= Mathf.Tau;
            QueueRedraw();
        }

        public override void _Draw()
        {
            var c = Size / 2f;
            float r = Mathf.Min(Size.X, Size.Y) / 2f - 4f;
            if (r <= 0f) return;

            DrawArc(c, r, 0f, Mathf.Tau, 64, new Color(AccentColor.R, AccentColor.G, AccentColor.B, 0.18f), 4f, true);
            DrawArc(c, r, _phase, _phase + Mathf.Pi * 0.55f, 32, AccentColor, 4f, true);
        }
    }
}
