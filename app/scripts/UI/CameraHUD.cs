using Godot;
using CoreJoystick = SLNG.Core.Camera.CameraJoystick;

namespace SLNG.App.UI;

/// <summary>
/// The Camera Controls window, laid out like the reference viewer's <c>floater_camera.xml</c> (FEAT-UI-74): one thin
/// row of small icon buttons on top (the Front / Side / Rear presets, a reset and, at the right end, the focus-point
/// toggle), and below it a round orbit trackball, a slim zoom column (+, slider, -) and a square pan pad. The window
/// opens compact (pads 80 px, the reference viewer's size) and the whole cluster scales up if it is dragged larger.
/// Every rate is per second, so it is the same at 30 and 144 fps.
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
    /// <summary>Drag the window larger and the whole cluster follows, up to twice its opening size.</summary>
    private const float MaxContentScale = 2.0f;

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

    // The reference viewer's proportions (floater_camera.xml is 170 x 135 px in all). The pads are
    // CoreJoystick.ReferenceSize (80) square; everything else is small.
    private const float IconButtonSize = 20f; // the top row: Front, Side, Rear, Reset, the focus toggle
    private const float ZoomColumnWidth = 18f; // the +/- buttons are this square, the slider this wide
    private const int IconButtonFontSize = 15;
    private const int ZoomButtonFontSize = 16;
    private const int TopRowSeparation = 3; // between the icon buttons
    private const int ClusterRowSeparation = 6; // between the trackball, the zoom column and the pan pad
    private const int ClusterVerticalSeparation = 4; // between the icon row and the pads
    private const int ZoomColumnSeparation = 2;
    /// <summary>The icon font's line is its size plus this (2 px above and below the em box): measured.</summary>
    private const int IconFontLineGap = 4;

    /// <summary>Speed multipliers and camera preferences from the Preferences "Camera" tab. Null until Boot wires it;
    /// treated as 1.0 (built-in defaults) while unset.</summary>
    private CameraSettings? _cameraSettings;
    public void SetCameraSettings(CameraSettings settings) => _cameraSettings = settings;

    private DofSettings? _dofSettings;
    private Button? _focusMarkerToggle;

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
        if (_focusMarkerToggle != null && _dofSettings != null)
        {
            _focusMarkerToggle.SetPressedNoSignal(_dofSettings.ShowFocusMarker);
        }
    }

    private Control _scaleHost = null!;
    private VBoxContainer _mainVBox = null!;
    private Vector2 _referenceSize;

    public override void _Ready()
    {
        base._Ready(); // Set up SLNGWindow styling

        // Opt into position/size persistence (SLNGWindow). Not "camera_hud": sizes saved for the FEAT-UI-74
        // predecessor are half-scale and would open this layout at its minimum. And not "camera_controls" either:
        // that is the first, chunky FEAT-UI-74 layout (100 px pads, about 365 px wide), and a size saved from it
        // would reopen this compact one at the largest scale it allows.
        PersistId = "camera_controls_v2";
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
        mainVBox.AddThemeConstantOverride("separation", ClusterVerticalSeparation);
        _scaleHost.AddChild(mainVBox);
        _mainVBox = mainVBox;

        // 1. One thin row of small icon buttons, left to right: the view presets, the reset, and (right-aligned)
        // the focus-point toggle. No captions: each button's tooltip says what it does, as in the viewer.
        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", TopRowSeparation);
        mainVBox.AddChild(topRow);

        var btnFront = CreateIconButton("face", L10n.Tr("ui.camera.view_front_tooltip"), IconButtonSize, IconButtonFontSize); // a face, seen from the front
        var btnSide = CreateIconButton("directions_walk", L10n.Tr("ui.camera.view_side_tooltip"), IconButtonSize, IconButtonFontSize); // a figure in profile
        var btnRear = CreateIconButton("person", L10n.Tr("ui.camera.view_rear_tooltip"), IconButtonSize, IconButtonFontSize); // a head and shoulders with no face
        var btnReset = CreateIconButton("restart_alt", L10n.Tr("ui.camera.reset_tooltip"), IconButtonSize, IconButtonFontSize);
        topRow.AddChild(btnFront);
        topRow.AddChild(btnSide);
        topRow.AddChild(btnRear);
        topRow.AddChild(btnReset);
        topRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });

        btnFront.Pressed += () => _cameraController?.SetPresetView("Front");
        btnSide.Pressed += () => _cameraController?.SetPresetView("Side");
        btnRear.Pressed += () => _cameraController?.SetPresetView("Rear");
        btnReset.Pressed += () => _cameraController?.ResetCamera();

        // The focus marker (the camera's Look-At target in the world) is a toggle: on is the accent colour.
        var toggleMarker = CreateIconButton("center_focus_strong", L10n.Tr("ui.camera.show_focus_marker_tooltip"), IconButtonSize, IconButtonFontSize, toggle: true);
        toggleMarker.Toggled += on => _dofSettings?.SetShowFocusMarker(on);
        _focusMarkerToggle = toggleMarker;
        topRow.AddChild(toggleMarker);
        if (_dofSettings != null) toggleMarker.SetPressedNoSignal(_dofSettings.ShowFocusMarker);

        // 2. The pads and the zoom column between them.
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", ClusterRowSeparation);
        mainVBox.AddChild(row);

        // Orbit trackball. The centre dot is the viewer's resetCameraOrbit.
        _orbitPad = new CameraJoystick { Round = true, TooltipText = L10n.Tr("ui.camera.orbit_tooltip"), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _orbitPad.ResetRequested += () => _cameraController?.ResetCamera();
        row.AddChild(_orbitPad);

        // Zoom: + on top (closer), the slider (top = closest), - at the bottom. The slider is not stretched
        // by a container; it takes whatever height the pads give the row.
        var zoom = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(ZoomColumnWidth, 0) };
        zoom.AddThemeConstantOverride("separation", ZoomColumnSeparation);
        row.AddChild(zoom);

        var btnZoomIn = CreateIconButton("add", L10n.Tr("ui.camera.zoom_in_tooltip"), ZoomColumnWidth, ZoomButtonFontSize, round: true);
        var btnZoomOut = CreateIconButton("remove", L10n.Tr("ui.camera.zoom_out_tooltip"), ZoomColumnWidth, ZoomButtonFontSize, round: true);
        btnZoomIn.ButtonDown += () => BeginZoomHold(-1);
        btnZoomOut.ButtonDown += () => BeginZoomHold(1);
        btnZoomIn.ButtonUp += () => EndZoomHold(-1);
        btnZoomOut.ButtonUp += () => EndZoomHold(1);

        _zoomSlider = new VSlider
        {
            MinValue = 0, MaxValue = 1, Step = 0, Value = 0.5,
            CustomMinimumSize = new Vector2(ZoomColumnWidth, 24),
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

        // Pan pad. The centre dot is the viewer's resetCameraPan.
        _panPad = new CameraJoystick { Round = false, TooltipText = L10n.Tr("ui.camera.pan_tooltip"), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _panPad.ResetRequested += () => _cameraController?.ResetPan();
        row.AddChild(_panPad);

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

    /// <summary>A small, borderless icon-font button. <paramref name="size"/> is its side in px; <paramref name="round"/>
    /// makes it a disc, otherwise it has softly rounded corners. A <paramref name="toggle"/> shows its on state with
    /// the accent colour on the glyph (and a faint accent fill), so it is legible without a caption. The caller wires
    /// what it does.</summary>
    private Button CreateIconButton(string glyph, string tooltip, float size, int fontSize, bool round = false, bool toggle = false)
    {
        var btn = new Button
        {
            Text = glyph,
            TooltipText = tooltip,
            CustomMinimumSize = new Vector2(size, size),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ShrinkBegin,
            FocusMode = FocusModeEnum.None,
            ToggleMode = toggle,
        };

        int radius = round ? (int)(size / 2) : 4;
        var grey = new Color(0.88f, 0.88f, 0.88f);

        btn.AddThemeStyleboxOverride("normal", RoundedFill(new Color(1, 1, 1, 0.05f), radius));
        btn.AddThemeStyleboxOverride("hover", RoundedFill(new Color(1, 1, 1, 0.15f), radius));
        // A glyph set larger than the button has room for (16 px in an 18 px disc: the line is 20 px) would push the
        // button taller than asked, so the line gap is trimmed evenly from above and below; the glyph does not move.
        int trim = Mathf.Max(0, fontSize + IconFontLineGap - (int)size);
        btn.AddThemeFontOverride("font", trim == 0 ? _iconFont : new FontVariation { BaseFont = _iconFont, SpacingTop = -(trim / 2), SpacingBottom = -(trim - trim / 2) });
        btn.AddThemeFontSizeOverride("font_size", fontSize);
        btn.AddThemeColorOverride("font_color", grey);

        if (toggle)
        {
            // On: the accent glyph over a faint accent fill, so the state reads without a caption.
            btn.AddThemeStyleboxOverride("pressed", RoundedFill(new Color(UiTheme.Accent, 0.22f), radius));
            btn.AddThemeStyleboxOverride("hover_pressed", RoundedFill(new Color(UiTheme.Accent, 0.32f), radius));
            btn.AddThemeColorOverride("font_pressed_color", UiTheme.Accent);
            btn.AddThemeColorOverride("font_hover_pressed_color", UiTheme.Accent);
        }
        else
        {
            btn.AddThemeStyleboxOverride("pressed", RoundedFill(new Color(1, 1, 1, 0.25f), radius));
        }

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
