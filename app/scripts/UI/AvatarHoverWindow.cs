using System;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-AVATAR-03: hover-height slider, opened from the new Avatar menu. A small persistent
/// toggle window (like <see cref="EnvironmentWindow"/>), not a one-shot dialog -- the user may
/// want to nudge it while walking around rather than set-and-forget.
///
/// This window owns no state of its own beyond the control values; <see cref="AvatarHoverSettings"/>
/// is the source of truth and Boot.cs is what actually applies a change (local self-avatar render
/// offset + outbound network send), same split as SnapshotWindow/DofSettings/DepthOfFieldController.
/// </summary>
public partial class AvatarHoverWindow : SLNGWindow
{
    private AvatarHoverSettings? _settings;
    private HSlider _slider = null!;
    private Label _valueLabel = null!;

    // Guards ValueChanged/Toggled handlers while Refresh pushes a settings value INTO the
    // controls, so that doesn't loop back out as a user-initiated change (same guard pattern as
    // SnapshotWindow's _refreshingDof).
    private bool _refreshing;

    /// <summary>(value, persist) -- persist is false while dragging, true on release/reset. Boot.cs
    /// wires this to update the local self-avatar render immediately and send the new value to the
    /// sim; AvatarHoverSettings persistence happens here regardless of what the callback does.</summary>
    public Action<float, bool>? OnHoverChanged;

    public override void _Ready()
    {
        PersistId = "avatar_hover";
        base._Ready();

        Title = L10n.Tr("ui.avatar_hover.title");
        CustomMinimumSize = new Vector2(320, 140);
        Size = new Vector2(320, 140);
        Position = new Vector2(200, 160);
        Visible = false;

        // Inset comes from SLNGWindow.ContentContainer now (FEAT-UI-26); this container is kept
        // only because the layout below hangs off it.
        var margin = new MarginContainer();
        ContentContainer.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(vbox);

        var description = new Label
        {
            Text = L10n.Tr("ui.avatar_hover.description"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        description.AddThemeFontSizeOverride("font_size", 11);
        description.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        vbox.AddChild(description);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(row);

        _slider = new HSlider
        {
            MinValue = AvatarHoverSettings.MinHoverHeight,
            MaxValue = AvatarHoverSettings.MaxHoverHeight,
            Step = 0.01,
            Value = AvatarHoverSettings.DefaultHoverHeight,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(140, 0),
        };
        _slider.ValueChanged += v =>
        {
            _valueLabel.Text = FormatMeters(v);
            if (_refreshing) return;
            OnHoverChanged?.Invoke((float)v, false);
        };
        _slider.DragEnded += _ =>
        {
            if (_refreshing) return;
            OnHoverChanged?.Invoke((float)_slider.Value, true);
        };
        row.AddChild(_slider);

        _valueLabel = new Label
        {
            Text = FormatMeters(AvatarHoverSettings.DefaultHoverHeight),
            CustomMinimumSize = new Vector2(56, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _valueLabel.AddThemeFontSizeOverride("font_size", 11);
        _valueLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        row.AddChild(_valueLabel);

        var resetButton = new Button
        {
            Text = L10n.Tr("ui.avatar_hover.reset"),
            FocusMode = FocusModeEnum.None,
        };
        resetButton.Pressed += () =>
        {
            _slider.Value = AvatarHoverSettings.DefaultHoverHeight;
            OnHoverChanged?.Invoke(AvatarHoverSettings.DefaultHoverHeight, true);
        };
        vbox.AddChild(resetButton);
    }

    /// <summary>Boot hands over the persisted settings once, after login -- same handover shape as
    /// SnapshotWindow.Initialize for DofSettings.</summary>
    public void Initialize(AvatarHoverSettings settings)
    {
        _settings = settings;
        Refresh();
    }

    /// <summary>Pushes the current settings value into the slider without re-triggering
    /// OnHoverChanged. Called on Initialize and whenever something else changes the value (the
    /// Reset button already updates the slider itself, so this covers external callers only).</summary>
    public void Refresh()
    {
        if (_settings == null) return;
        _refreshing = true;
        _slider.Value = _settings.HoverHeight;
        _valueLabel.Text = FormatMeters(_settings.HoverHeight);
        _refreshing = false;
    }

    public void Toggle() => Visible = !Visible;

    private static string FormatMeters(double v) => $"{v:0.00} m";
}
