using Godot;
using System;
using System.Collections.Generic;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-33: the small panel that slides in at the top right when something arrives and goes
/// away on its own.
/// </summary>
/// <remarks>
/// <para>Deliberately NOT <see cref="ToastOverlay"/>, which already exists and stays. That one is
/// for hints the user caused themselves — "Immer rennen: an" — so it is centred, holds one line
/// at a time, and ignores the mouse. An incoming notification is the opposite case on all three
/// counts: several can land at once, they belong out of the way rather than across the middle of
/// the view, and clicking one should take you to it. Merging the two would mean a payment and a
/// keypress hint taking turns in the same slot.</para>
///
/// <para>Auto-dismissal is the whole point — it must not become another thing to close. What it
/// does NOT do is decide anything: the toast is a pointer at the record, and dismissing it leaves
/// the entry in <see cref="NotificationStore"/> untouched. Missing a toast therefore costs
/// nothing, which is what makes a disappearing notification acceptable at all.</para>
/// </remarks>
public partial class NotificationToastOverlay : CanvasLayer
{
    /// <summary>How long a toast stays before it fades.</summary>
    private const float HoldSeconds = 6.0f;

    /// <summary>At most this many at once; the oldest is retired early to make room. A payment
    /// arriving during a burst of group notices should not be queued behind them for a minute.
    /// </summary>
    private const int MaxVisible = 4;

    private VBoxContainer _stack = null!;
    private readonly List<Control> _live = new();

    /// <summary>Raised when a toast is clicked, with the tab its entry lives in.</summary>
    public event Action<NotificationKind>? Clicked;

    public override void _Ready()
    {
        Layer = 104; // just under ToastOverlay (105), above the HUD

        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        _stack = new VBoxContainer
        {
            // Top right, growing downwards, clear of the top menu.
            AnchorLeft = 1f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 0f,
            OffsetLeft = -340, OffsetRight = -16, OffsetTop = 56,
            GrowHorizontal = Control.GrowDirection.Begin,
            GrowVertical = Control.GrowDirection.End,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _stack.AddThemeConstantOverride("separation", 6);
        root.AddChild(_stack);
    }

    public void Show(NotificationEntry entry)
    {
        if (!IsInstanceValid(_stack)) return;

        while (_live.Count >= MaxVisible) Retire(_live[0]);

        var panel = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Stop,
            Modulate = new Color(1, 1, 1, 0),
        };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            // Opaque enough to read over anything, for the same measured reason the windows are
            // (see SLNGWindow): what is behind a floating panel is the world, not a background
            // we control.
            BgColor = new Color(0, 0, 0, 0.88f),
            BorderWidthLeft = 3,
            BorderColor = AccentFor(entry.Kind),
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 10, ContentMarginRight = 10,
            ContentMarginTop = 8, ContentMarginBottom = 8,
        });

        var label = new Label
        {
            Text = entry.Text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", 12);
        panel.AddChild(label);

        var kind = entry.Kind;
        panel.GuiInput += @event =>
        {
            if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                Clicked?.Invoke(kind);
                Retire(panel);
            }
        };

        _stack.AddChild(panel);
        _live.Add(panel);

        var tween = CreateTween();
        tween.TweenProperty(panel, "modulate:a", 1.0f, 0.15f);
        tween.TweenInterval(HoldSeconds);
        tween.TweenProperty(panel, "modulate:a", 0.0f, 0.4f);
        tween.TweenCallback(Callable.From(() => Retire(panel)));
    }

    private void Retire(Control panel)
    {
        _live.Remove(panel);
        if (!IsInstanceValid(panel)) return;

        // RemoveChild first: a freed node stays a child until the end of the frame, and the stack
        // would keep its gap until then.
        _stack.RemoveChild(panel);
        panel.QueueFree();
    }

    /// <summary>The stripe down the left edge, so the kind reads before the sentence does.</summary>
    private static Color AccentFor(NotificationKind kind) => kind switch
    {
        NotificationKind.Transaction => new Color(0.44f, 0.82f, 0.44f),
        NotificationKind.Invitation => new Color(0.36f, 0.66f, 0.94f),
        NotificationKind.Group => new Color(0.78f, 0.62f, 0.95f),
        _ => new Color(0.95f, 0.72f, 0.35f),
    };
}
