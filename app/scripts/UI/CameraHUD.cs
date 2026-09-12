using System;
using Godot;

namespace SLNG.App.UI;

public partial class CameraHUD : SLNGWindow
{
    private AvatarController? _cameraController;
    private string _activeAction = "";
    private Label _statusLabel = null!;
    private bool _isLinked = false;
    private Font _iconFont = null!;

    private const float MinContentScale = 0.5f;
    private const float MaxContentScale = 1.5f;

    // Built-in per-frame pad steps at 100% (CameraSettings multiplies these live). OrbitStep was
    // 0.05 and felt fast even at the default; 0.035 is calmer and the Preferences slider still
    // reaches 3x. PanStep was 1.0 (~60 m/s held at 60 fps -- the avatar shot off-screen on a tap).
    private const float OrbitStep = 0.035f;
    private const float PanStep = 0.1f;
    private const float ZoomStep = 0.5f;

    /// <summary>Speed multipliers from the Preferences "Camera" tab. Null until Boot wires it;
    /// treated as 1.0 (built-in defaults) while unset.</summary>
    private CameraSettings? _cameraSettings;
    public void SetCameraSettings(CameraSettings settings) => _cameraSettings = settings;
    private Control _scaleHost = null!;
    private VBoxContainer _mainVBox = null!;
    private Vector2 _referenceSize;

    public override void _Ready()
    {
        base._Ready(); // Set up SLNGWindow styling

        PersistId = "camera_hud"; // opt into position/size persistence (SLNGWindow)
        _iconFont = GD.Load<Font>("res://assets/fonts/MaterialSymbolsOutlined.ttf");

        Title = L10n.Tr("ui.camera.title");
        Visible = false;
        // Room for the captioned groups and the preset row at 1:1 -- below this RescaleContent
        // starts shrinking the pads, which is a fallback, not the intended size.
        CustomMinimumSize = new Vector2(210, 190);
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
        mainVBox.AddThemeConstantOverride("separation", 12);
        _scaleHost.AddChild(mainVBox);
        _mainVBox = mainVBox;

        _statusLabel = new Label { Text = L10n.Tr("ui.camera.waiting"), HorizontalAlignment = HorizontalAlignment.Center };
        _statusLabel.AddThemeFontSizeOverride("font_size", 12);
        _statusLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        mainVBox.AddChild(_statusLabel);

        var hbox = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        hbox.AddThemeConstantOverride("separation", 10);
        mainVBox.AddChild(hbox);

        // Pad background style (circle for rotation)
        var circleStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.15f, 0.15f, 0.16f, 0.9f),
            CornerRadiusTopLeft = 100, CornerRadiusTopRight = 100, CornerRadiusBottomLeft = 100, CornerRadiusBottomRight = 100,
            ShadowColor = new Color(0, 0, 0, 0.3f), ShadowSize = 4
        };

        // Pad background style (rounded rect for pan)
        var squareStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.15f, 0.15f, 0.16f, 0.9f),
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ShadowColor = new Color(0, 0, 0, 0.3f), ShadowSize = 4
        };

        // 1. Rotation Pad. Each group is caption + control in its own column: three unlabelled
        // pads next to each other were guesswork, and "which one pans again?" is not something a
        // camera window should make you find out by trying.
        var rotGroup = CreateGroup(L10n.Tr("ui.camera.orbit"), hbox);

        var rotPanel = new PanelContainer();
        rotPanel.AddThemeStyleboxOverride("panel", circleStyle);
        rotGroup.AddChild(rotPanel);

        var rotGrid = new GridContainer { Columns = 3 };
        rotPanel.AddChild(rotGrid);
        rotGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(28, 28) });
        rotGrid.AddChild(CreatePadButton("keyboard_arrow_up", "rot_up"));
        rotGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(28, 28) });
        rotGrid.AddChild(CreatePadButton("keyboard_arrow_left", "rot_left"));
        rotGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(28, 28) }); // Center empty
        rotGrid.AddChild(CreatePadButton("keyboard_arrow_right", "rot_right"));
        rotGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(28, 28) });
        rotGrid.AddChild(CreatePadButton("keyboard_arrow_down", "rot_down"));
        rotGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(28, 28) });

        // 2. Zoom. Given the same panel backing as the two pads -- it used to be two loose
        // buttons floating between them, which read as leftovers rather than as a third control.
        var zoomGroup = CreateGroup(L10n.Tr("ui.camera.zoom"), hbox);

        var zoomPanel = new PanelContainer();
        zoomPanel.AddThemeStyleboxOverride("panel", squareStyle);
        zoomGroup.AddChild(zoomPanel);

        var zoomVBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        zoomVBox.AddThemeConstantOverride("separation", 4);
        zoomPanel.AddChild(zoomVBox);
        zoomVBox.AddChild(CreatePadButton("add", "zoom_in"));
        zoomVBox.AddChild(CreatePadButton("remove", "zoom_out"));

        // 3. Pan Pad
        var panGroup = CreateGroup(L10n.Tr("ui.camera.pan"), hbox);

        var panPanel = new PanelContainer();
        panPanel.AddThemeStyleboxOverride("panel", squareStyle);
        panGroup.AddChild(panPanel);

        var panGrid = new GridContainer { Columns = 3 };
        panPanel.AddChild(panGrid);
        panGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(28, 28) });
        panGrid.AddChild(CreatePadButton("keyboard_arrow_up", "pan_up"));
        panGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(28, 28) });
        panGrid.AddChild(CreatePadButton("keyboard_arrow_left", "pan_left"));
        panGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(28, 28) }); // Center empty
        panGrid.AddChild(CreatePadButton("keyboard_arrow_right", "pan_right"));
        panGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(28, 28) });
        panGrid.AddChild(CreatePadButton("keyboard_arrow_down", "pan_down"));
        panGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(28, 28) });

        // 4. Presets, under their own caption and a hairline rule, because they are a different
        // kind of thing from the pads above: one-shot jumps, not held movement.
        var presetCaption = new Label
        {
            Text = L10n.Tr("ui.camera.views"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        presetCaption.AddThemeFontSizeOverride("font_size", 10);
        presetCaption.AddThemeColorOverride("font_color", CaptionColor);
        mainVBox.AddChild(new HSeparator());
        mainVBox.AddChild(presetCaption);

        var presetsHBox = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        presetsHBox.AddThemeConstantOverride("separation", 6);
        mainVBox.AddChild(presetsHBox);
        
        // Use sleek text buttons instead of problematic emojis
        var btnFront = CreateModernButton(L10n.Tr("ui.camera.view_front"), "", new Godot.Vector2(58, 26), 12);
        btnFront.TooltipText = L10n.Tr("ui.camera.view_front_tooltip");
        var btnSide = CreateModernButton(L10n.Tr("ui.camera.view_side"), "", new Godot.Vector2(58, 26), 12);
        btnSide.TooltipText = L10n.Tr("ui.camera.view_side_tooltip");
        var btnRear = CreateModernButton(L10n.Tr("ui.camera.view_rear"), "", new Godot.Vector2(58, 26), 12);
        btnRear.TooltipText = L10n.Tr("ui.camera.view_rear_tooltip");
        
        presetsHBox.AddChild(btnFront);
        presetsHBox.AddChild(btnSide);
        presetsHBox.AddChild(btnRear);

        btnFront.Pressed += () => _cameraController?.SetPresetView("Front");
        btnSide.Pressed += () => _cameraController?.SetPresetView("Side");
        btnRear.Pressed += () => _cameraController?.SetPresetView("Rear");

        // Natural, unscaled size of the whole button cluster -- the scale ratio in
        // RescaleContent is always relative to this, never to the previous frame's size.
        _referenceSize = mainVBox.GetCombinedMinimumSize();
        mainVBox.Size = _referenceSize;
        CallDeferred(nameof(RescaleContent));
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

    // One muted grey for every caption in this window, so the labels read as annotation and the
    // controls stay the thing your eye lands on.
    private static readonly Color CaptionColor = new(0.58f, 0.62f, 0.68f);

    /// <summary>A captioned column: the small grey label on top, the caller's control below,
    /// centred. Returns the column to add that control to.</summary>
    private static VBoxContainer CreateGroup(string caption, Control parent)
    {
        var column = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        column.AddThemeConstantOverride("separation", 4);
        parent.AddChild(column);

        var label = new Label { Text = caption, HorizontalAlignment = HorizontalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", 10);
        label.AddThemeColorOverride("font_color", CaptionColor);
        column.AddChild(label);

        return column;
    }

    private Button CreatePadButton(string text, string actionName)
    {
        var btn = new Button { Text = text, CustomMinimumSize = new Godot.Vector2(28, 28), FocusMode = Control.FocusModeEnum.None };
        
        var normalStyle = new StyleBoxEmpty();
        var hoverStyle = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.1f), CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14, CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14 };
        var pressedStyle = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.2f), CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14, CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14 };

        btn.AddThemeStyleboxOverride("normal", normalStyle);
        btn.AddThemeStyleboxOverride("hover", hoverStyle);
        btn.AddThemeStyleboxOverride("pressed", pressedStyle);
        btn.AddThemeFontOverride("font", _iconFont);
        btn.AddThemeFontSizeOverride("font_size", 20);
        btn.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.85f));

        btn.ButtonDown += () => _activeAction = actionName;
        btn.ButtonUp += () => { if (_activeAction == actionName) _activeAction = ""; };
        return btn;
    }

    private Button CreateModernButton(string text, string actionName, Godot.Vector2 minSize, int fontSize, bool useIconFont = false)
    {
        var btn = new Button { Text = text, CustomMinimumSize = minSize, FocusMode = Control.FocusModeEnum.None };

        var normalStyle = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.05f), CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6 };
        var hoverStyle = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.15f), CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6 };
        var pressedStyle = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.25f), CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6 };

        btn.AddThemeStyleboxOverride("normal", normalStyle);
        btn.AddThemeStyleboxOverride("hover", hoverStyle);
        btn.AddThemeStyleboxOverride("pressed", pressedStyle);
        if (useIconFont) btn.AddThemeFontOverride("font", _iconFont);
        btn.AddThemeFontSizeOverride("font_size", fontSize);
        btn.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f));

        if (!string.IsNullOrEmpty(actionName))
        {
            btn.ButtonDown += () => _activeAction = actionName;
            btn.ButtonUp += () => { if (_activeAction == actionName) _activeAction = ""; };
        }
        return btn;
    }

    /// <summary>Flips visibility -- mirrors InventoryPanel.Toggle() so ButtonBar (and any
    /// other future caller) has one consistent toggle entry point across SLNGWindow panels.</summary>
    public void Toggle() => Visible = !Visible;

    public override void _Process(double delta)
    {
        if (_cameraController == null)
        {
            _cameraController = GetTree().Root.FindChild("AvatarController", true, false) as AvatarController;
            if (_cameraController != null)
            {
                if (!_isLinked)
                {
                    _isLinked = true;
                    _statusLabel.Visible = false; // Hide label to save space once connected
                    CustomMinimumSize = new Vector2(210, 172); // Shrink window to fit tightly once the status line goes
                    Logger.Debug("[CameraHUD] AvatarController found and linked!");
                }
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(_activeAction))
            {
                float orbit = OrbitStep * (_cameraSettings?.OrbitSpeed ?? 1f);
                float pan = PanStep * (_cameraSettings?.PanSpeed ?? 1f);
                float zoom = ZoomStep * (_cameraSettings?.ZoomSpeed ?? 1f);

                switch (_activeAction)
                {
                    case "rot_left": _cameraController.RotateCamera(new Godot.Vector2(orbit, 0)); break;
                    case "rot_right": _cameraController.RotateCamera(new Godot.Vector2(-orbit, 0)); break;
                    case "rot_up": _cameraController.RotateCamera(new Godot.Vector2(0, orbit)); break;
                    case "rot_down": _cameraController.RotateCamera(new Godot.Vector2(0, -orbit)); break;
                    case "zoom_in": _cameraController.ZoomCamera(-zoom); break;
                    case "zoom_out": _cameraController.ZoomCamera(zoom); break;
                    // BUG-UI-02: pan left/right were inverted. PanCamera feeds _panOffset, which
                    // AvatarController applies as `targetPos += Basis.X * _panOffset.X` -- +X moves
                    // the camera screen-RIGHT, so "pan_left" must be -X. Up/down were already right.
                    case "pan_left": _cameraController.PanCamera(new Godot.Vector2(-pan, 0)); break;
                    case "pan_right": _cameraController.PanCamera(new Godot.Vector2(pan, 0)); break;
                    case "pan_up": _cameraController.PanCamera(new Godot.Vector2(0, pan)); break;
                    case "pan_down": _cameraController.PanCamera(new Godot.Vector2(0, -pan)); break;
                }
            }
        }
    }
}
