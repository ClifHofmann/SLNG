using Godot;
using CoreJoystick = SLNG.Core.Camera.CameraJoystick;

namespace SLNG.App.UI;

/// <summary>
/// The Camera Controls window, laid out like the reference viewer's <c>floater_camera.xml</c> (FEAT-UI-74): the
/// view presets and a reset in a column, a round orbit trackball, a zoom column (+, slider, -) and a square pan
/// pad, with the focus-point checkbox below. Every rate is per second, so it is the same at 30 and 144 fps.
/// </summary>
public partial class CameraHUD : SLNGWindow
{
    private AvatarController? _cameraController;
    private Label _statusLabel = null!;
    private bool _isLinked = false;
    private Font _iconFont = null!;

    private CameraJoystick _orbitPad = null!;
    private CameraJoystick _panPad = null!;
    private VSlider _zoomSlider = null!;
    private bool _sliderDragging;

    /// <summary>+1 while the "-" (zoom out) button is held, -1 while "+" (zoom in) is, else 0.</summary>
    private int _zoomHeld;
    private ulong _zoomHeldSinceMsec;

    /// <summary>The cluster never draws smaller than this. The window opens at 1.0 (see <c>_Ready</c>), and
    /// SLNGWindow clamps a saved size to <see cref="Control.CustomMinimumSize"/>, which is derived from this.</summary>
    private const float MinContentScale = 0.8f;
    private const float MaxContentScale = 1.5f;

    /// <summary>What SLNGWindow puts around its ContentContainer besides the inset: the 8 px shadow
    /// margin and the 1 px border on each side, plus the title bar above. Measured; if these change,
    /// the cluster merely gains or loses a few px of gap at the default size.</summary>
    private const float FrameWidth = 18f;
    private const float FrameAndTitleBarHeight = 56f;

    // The viewer's rates at full push (llagentcamera.cpp:1271-1273, 1291-1297), per SECOND; CameraSettings
    // multiplies them live. They were per frame (0.035 rad, 0.1 m, 0.5 m), which is a different speed at every
    // frame rate: 120 deg/s, 6 m/s and 30 m/s at 60 fps, and twice that at 120.
    private const float OrbitDegreesPerSecond = 90f;
    private const float PanMetresPerSecond = 5f;
    /// <summary>Zoom buttons cover the current distance to the focus once per second ("distance meters per
    /// second", llagentcamera.cpp:1291), so a close camera creeps and a far one is not a crawl.</summary>
    private const float ZoomDistancesPerSecond = 1f;

    private const float TargetHeight = 28f; // every button; the pads are CoreJoystick.ReferenceSize (100)

    /// <summary>Speed multipliers and camera preferences from the Preferences "Camera" tab. Null until Boot wires it;
    /// treated as 1.0 (built-in defaults) while unset.</summary>
    private CameraSettings? _cameraSettings;
    public void SetCameraSettings(CameraSettings settings) => _cameraSettings = settings;

    private DofSettings? _dofSettings;
    private CheckBox? _focusMarkerCheck;

    public void SetDofSettings(DofSettings settings)
    {
        if (_dofSettings != null) _dofSettings.Changed -= OnDofSettingsChanged;
        _dofSettings = settings;
        if (_dofSettings != null)
        {
            _dofSettings.Changed += OnDofSettingsChanged;
            OnDofSettingsChanged();
        }
    }

    private void OnDofSettingsChanged()
    {
        if (_focusMarkerCheck != null && _dofSettings != null)
        {
            _focusMarkerCheck.SetPressedNoSignal(_dofSettings.ShowFocusMarker);
        }
    }

    private Control _scaleHost = null!;
    private VBoxContainer _mainVBox = null!;
    private Vector2 _referenceSize;

    public override void _Ready()
    {
        base._Ready(); // Set up SLNGWindow styling

        // Opt into position/size persistence (SLNGWindow). Not the old "camera_hud" key: sizes saved for the
        // FEAT-UI-74 predecessor are half-scale and would open the new layout at its minimum.
        PersistId = "camera_controls";
        _iconFont = GD.Load<Font>("res://assets/fonts/MaterialSymbolsOutlined.ttf");

        Title = L10n.Tr("ui.camera.title");
        Visible = false;
        // CustomMinimumSize and Size are set below, once the cluster has been built and measured.
        Position = new Vector2(100, 100);

        OnCloseRequested = Hide;

        // Inset comes from SLNGWindow.ContentContainer now (FEAT-UI-26); this container is kept
        // only because the layout below hangs off it.
        var margin = new MarginContainer();
        ContentContainer.AddChild(margin);

        // A plain (non-Container) host so mainVBox below keeps its own natural size instead of
        // being stretched to fill -- that natural size becomes _referenceSize, and RescaleContent
        // scales+centers mainVBox to fit whatever room the host actually has, so shrinking the
        // SLNGWindow shrinks the pads/buttons themselves instead of just clipping them.
        _scaleHost = new Control { CustomMinimumSize = new Vector2(100, 80), SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        _scaleHost.Resized += RescaleContent;
        margin.AddChild(_scaleHost);

        var mainVBox = new VBoxContainer();
        mainVBox.AddThemeConstantOverride("separation", 8);
        _scaleHost.AddChild(mainVBox);
        _mainVBox = mainVBox;

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 10);
        mainVBox.AddChild(row);

        // 1. Presets and reset, one column of buttons. No caption or rule: Front/Side/Rear say what they are,
        // and Reset is an icon with a tooltip, as in the viewer.
        var presets = new VBoxContainer();
        presets.AddThemeConstantOverride("separation", 4);
        row.AddChild(presets);

        var btnFront = CreateTextButton(L10n.Tr("ui.camera.view_front"), L10n.Tr("ui.camera.view_front_tooltip"));
        var btnSide = CreateTextButton(L10n.Tr("ui.camera.view_side"), L10n.Tr("ui.camera.view_side_tooltip"));
        var btnRear = CreateTextButton(L10n.Tr("ui.camera.view_rear"), L10n.Tr("ui.camera.view_rear_tooltip"));
        var btnReset = CreateTextButton("restart_alt", L10n.Tr("ui.camera.reset_tooltip"), useIconFont: true);
        presets.AddChild(btnFront);
        presets.AddChild(btnSide);
        presets.AddChild(btnRear);
        presets.AddChild(btnReset);

        btnFront.Pressed += () => _cameraController?.SetPresetView("Front");
        btnSide.Pressed += () => _cameraController?.SetPresetView("Side");
        btnRear.Pressed += () => _cameraController?.SetPresetView("Rear");
        btnReset.Pressed += () => _cameraController?.ResetCamera();

        // 2. Orbit trackball. The centre dot is the viewer's resetCameraOrbit.
        _orbitPad = new CameraJoystick { Round = true, TooltipText = L10n.Tr("ui.camera.orbit_tooltip"), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _orbitPad.ResetRequested += () => _cameraController?.ResetCamera();
        row.AddChild(_orbitPad);

        // 3. Zoom: + on top (closer), the slider (top = closest), - at the bottom. The slider is not stretched
        // by a container; it takes whatever height the preset column gives the row.
        var zoom = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        zoom.AddThemeConstantOverride("separation", 4);
        row.AddChild(zoom);

        var btnZoomIn = CreateIconButton("add", L10n.Tr("ui.camera.zoom_in_tooltip"));
        var btnZoomOut = CreateIconButton("remove", L10n.Tr("ui.camera.zoom_out_tooltip"));
        btnZoomIn.ButtonDown += () => BeginZoomHold(-1);
        btnZoomOut.ButtonDown += () => BeginZoomHold(1);
        btnZoomIn.ButtonUp += () => EndZoomHold(-1);
        btnZoomOut.ButtonUp += () => EndZoomHold(1);

        _zoomSlider = new VSlider
        {
            MinValue = 0, MaxValue = 1, Step = 0, Value = 0.5,
            CustomMinimumSize = new Vector2(TargetHeight, 56),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None, // arrow keys must keep walking, not nudge the slider
            TooltipText = L10n.Tr("ui.camera.zoom_slider_tooltip"),
        };
        _zoomSlider.DragStarted += () => _sliderDragging = true;
        _zoomSlider.DragEnded += _ => _sliderDragging = false;
        // Only the person moves the slider through this signal: following the camera uses SetValueNoSignal.
        _zoomSlider.ValueChanged += value => { if (_cameraController != null) _cameraController.ZoomFraction = (float)value; };
        zoom.AddChild(btnZoomIn);
        zoom.AddChild(_zoomSlider);
        zoom.AddChild(btnZoomOut);

        // 4. Pan pad. The centre dot is the viewer's resetCameraPan.
        _panPad = new CameraJoystick { Round = false, TooltipText = L10n.Tr("ui.camera.pan_tooltip"), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _panPad.ResetRequested += () => _cameraController?.ResetPan();
        row.AddChild(_panPad);

        var focusRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        mainVBox.AddChild(focusRow);

        var chkMarker = new CheckBox
        {
            Text = L10n.Tr("ui.camera.show_focus_marker"),
            TooltipText = L10n.Tr("ui.camera.show_focus_marker_tooltip"),
            FocusMode = FocusModeEnum.None,
        };
        chkMarker.AddThemeFontSizeOverride("font_size", 12);
        chkMarker.Toggled += on => _dofSettings?.SetShowFocusMarker(on);
        _focusMarkerCheck = chkMarker;
        focusRow.AddChild(chkMarker);
        if (_dofSettings != null) chkMarker.SetPressedNoSignal(_dofSettings.ShowFocusMarker);

        // Until the camera controller is found the controls do nothing: say so over a dimmed cluster instead of
        // reserving a line of the window for it (the old status line shifted the layout when it went away).
        _statusLabel = new Label
        {
            Text = L10n.Tr("ui.camera.waiting"),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            GrowHorizontal = GrowDirection.Both,
            GrowVertical = GrowDirection.Both,
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 12);
        _statusLabel.AddThemeColorOverride("font_color", Colors.White);
        _statusLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        _statusLabel.AddThemeConstantOverride("outline_size", 4);
        _scaleHost.AddChild(_statusLabel);
        _statusLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        _mainVBox.Modulate = new Color(1, 1, 1, 0.4f);

        // Natural, unscaled size of the whole button cluster -- the scale ratio in
        // RescaleContent is always relative to this, never to the previous frame's size.
        _referenceSize = mainVBox.GetCombinedMinimumSize();
        mainVBox.Size = _referenceSize;
        // The smallest the window may be is the cluster at MinContentScale, in the cluster's own aspect plus the
        // standard inset and the frame (a minimum of a different shape left the centred cluster floating off the
        // frame). It OPENS at scale 1.0, which is where the targets are comfortable; a size saved by SLNGWindow
        // is clamped to the minimum on restore, so it can never bring the cluster back below MinContentScale.
        CustomMinimumSize = ContentSizeToWindow(_referenceSize * MinContentScale, ceil: true);
        Size = ContentSizeToWindow(_referenceSize, ceil: false);
        CallDeferred(nameof(RescaleContent));
    }

    /// <summary>The window size that gives a cluster of <paramref name="content"/> its room: the standard inset
    /// and the frame around it.</summary>
    private static Vector2 ContentSizeToWindow(Vector2 content, bool ceil)
    {
        if (ceil) content = new Vector2(Mathf.Ceil(content.X), Mathf.Ceil(content.Y));
        return new Vector2(
            content.X + 2 * DefaultContentMarginH + FrameWidth,
            content.Y + 2 * DefaultContentMarginV + FrameAndTitleBarHeight);
    }

    public override void _ExitTree()
    {
        if (_dofSettings != null) _dofSettings.Changed -= OnDofSettingsChanged;
        base._ExitTree();
    }

    /// <summary>Fits mainVBox to whatever room _scaleHost currently has, scaling (not just
    /// clipping) the pads/buttons so dragging the SLNGWindow resize handle smaller or larger
    /// visibly shrinks/grows the controls themselves.</summary>
    private void RescaleContent()
    {
        if (_referenceSize.X <= 0 || _referenceSize.Y <= 0) return;
        var avail = _scaleHost.Size;
        if (avail.X <= 0 || avail.Y <= 0) return;

        float scale = Mathf.Min(avail.X / _referenceSize.X, avail.Y / _referenceSize.Y);
        scale = Mathf.Clamp(scale, MinContentScale, MaxContentScale);

        _mainVBox.Scale = new Vector2(scale, scale);
        _mainVBox.Position = ((avail - _referenceSize * scale) / 2f).Round();
    }

    /// <summary>A flat rounded button that does one thing when pressed (a view preset, the reset).</summary>
    private Button CreateTextButton(string text, string tooltip, bool useIconFont = false)
    {
        var btn = new Button
        {
            Text = text,
            TooltipText = tooltip,
            CustomMinimumSize = new Vector2(60, TargetHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
        };

        var normalStyle = RoundedFill(new Color(1, 1, 1, 0.05f), 6);
        var hoverStyle = RoundedFill(new Color(1, 1, 1, 0.15f), 6);
        var pressedStyle = RoundedFill(new Color(1, 1, 1, 0.25f), 6);

        btn.AddThemeStyleboxOverride("normal", normalStyle);
        btn.AddThemeStyleboxOverride("hover", hoverStyle);
        btn.AddThemeStyleboxOverride("pressed", pressedStyle);
        if (useIconFont) btn.AddThemeFontOverride("font", _iconFont);
        btn.AddThemeFontSizeOverride("font_size", useIconFont ? 20 : 12);
        btn.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f));
        return btn;
    }

    /// <summary>A borderless round icon button for the zoom column; the caller wires the hold.</summary>
    private Button CreateIconButton(string glyph, string tooltip)
    {
        var btn = new Button
        {
            Text = glyph,
            TooltipText = tooltip,
            CustomMinimumSize = new Vector2(TargetHeight, TargetHeight),
            FocusMode = FocusModeEnum.None,
        };

        btn.AddThemeStyleboxOverride("normal", RoundedFill(new Color(1, 1, 1, 0.05f), (int)(TargetHeight / 2)));
        btn.AddThemeStyleboxOverride("hover", RoundedFill(new Color(1, 1, 1, 0.15f), (int)(TargetHeight / 2)));
        btn.AddThemeStyleboxOverride("pressed", RoundedFill(new Color(1, 1, 1, 0.25f), (int)(TargetHeight / 2)));
        btn.AddThemeFontOverride("font", _iconFont);
        btn.AddThemeFontSizeOverride("font_size", 20);
        btn.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.85f));
        return btn;
    }

    private static StyleBoxFlat RoundedFill(Color fill, int radius) => new()
    {
        BgColor = fill,
        CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
    };

    private void BeginZoomHold(int direction)
    {
        _zoomHeld = direction;
        _zoomHeldSinceMsec = Time.GetTicksMsec();
    }

    private void EndZoomHold(int direction)
    {
        if (_zoomHeld == direction) _zoomHeld = 0;
    }

    /// <summary>Flips visibility -- mirrors InventoryPanel.Toggle() so ButtonBar (and any
    /// other future caller) has one consistent toggle entry point across SLNGWindow panels.</summary>
    public void Toggle() => Visible = !Visible;

    public override void _Process(double delta)
    {
        // A hidden window does nothing; a zoom button held when it was hidden must not keep zooming.
        if (!Visible)
        {
            _zoomHeld = 0;
            return;
        }

        using var _phase = MainThreadPhase.Enter("ui.camera-hud"); // BUG-PERF-05
        if (_cameraController == null)
        {
            _cameraController = GetTree().Root.FindChild("AvatarController", true, false) as AvatarController;
            if (_cameraController == null) return;

            if (!_isLinked)
            {
                _isLinked = true;
                _statusLabel.Visible = false;
                _mainVBox.Modulate = Colors.White;
                Logger.Debug("[CameraHUD] AvatarController found and linked!");
            }
        }

        float dt = (float)delta;

        // Orbit. RotateCamera's X is the opposite of the pad's (pressing LEFT turns +X), Y is the same (up is +).
        var orbit = _orbitPad.Rate;
        if (orbit != Vector2.Zero)
        {
            float step = Mathf.DegToRad(OrbitDegreesPerSecond) * (_cameraSettings?.OrbitSpeed ?? 1f) * dt;
            _cameraController.RotateCamera(new Vector2(-orbit.X, orbit.Y) * step);
        }

        // BUG-UI-02: pan left/right were inverted. PanCamera feeds _panOffset, which AvatarController applies as
        // `targetPos += Basis.X * _panOffset.X` -- +X moves the camera screen-RIGHT, so a push to the left is -X,
        // which is the pad's own sign. Up/down likewise (+Y is up).
        var pan = _panPad.Rate;
        if (pan != Vector2.Zero)
        {
            float step = PanMetresPerSecond * (_cameraSettings?.PanSpeed ?? 1f) * dt;
            _cameraController.PanCamera(pan * step);
        }

        // Zoom buttons: the distance changes by a fixed fraction per second, so it is exponential and the same
        // at any frame rate (a linear distance * dt would go negative on a long frame), ramped like the pads.
        // Safety net: the button's own ButtonUp is what normally ends it.
        if (_zoomHeld != 0 && !Input.IsMouseButtonPressed(MouseButton.Left)) _zoomHeld = 0;
        if (_zoomHeld != 0)
        {
            float ramp = CoreJoystick.Ramp((Time.GetTicksMsec() - _zoomHeldSinceMsec) / 1000f);
            float rate = ramp * ZoomDistancesPerSecond * (_cameraSettings?.ZoomSpeed ?? 1f);
            _cameraController.ZoomCamera(_cameraController.ZoomDistance * (Mathf.Exp(_zoomHeld * rate * dt) - 1f));
        }

        // The slider follows the camera (wheel, keys, buttons, presets) except while it is being dragged.
        if (!_sliderDragging)
        {
            float fraction = _cameraController.ZoomFraction;
            if (!Mathf.IsEqualApprox((float)_zoomSlider.Value, fraction)) _zoomSlider.SetValueNoSignal(fraction);
        }
    }
}
