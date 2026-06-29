using System;
using Godot;

namespace SLNG.App.UI;

public partial class CameraHUD : Control
{
    private AvatarController? _cameraController;

    private string _activeAction = "";

    private Label _statusLabel;

    public override void _Ready()
    {
        // Setup layout
        SetAnchorsPreset(LayoutPreset.BottomLeft);
        Position = new Godot.Vector2(20, -220);

        var mainVBox = new VBoxContainer();
        AddChild(mainVBox);

        _statusLabel = new Label { Text = "HUD Init..." };
        _statusLabel.AddThemeColorOverride("font_color", new Color(1, 1, 0)); // Yellow text
        mainVBox.AddChild(_statusLabel);

        var hbox = new HBoxContainer();
        mainVBox.AddChild(hbox);

        // 1. Rotation Pad
        var rotGrid = new GridContainer { Columns = 3 };
        hbox.AddChild(rotGrid);

        rotGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(40, 40) });
        rotGrid.AddChild(CreateHoldButton("^", "rot_up"));
        rotGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(40, 40) });

        rotGrid.AddChild(CreateHoldButton("<", "rot_left"));
        rotGrid.AddChild(new Label { Text = "ROT", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        rotGrid.AddChild(CreateHoldButton(">", "rot_right"));

        rotGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(40, 40) });
        rotGrid.AddChild(CreateHoldButton("v", "rot_down"));
        rotGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(40, 40) });

        hbox.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(20, 0) });

        // 2. Zoom Buttons
        var zoomVBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        hbox.AddChild(zoomVBox);
        zoomVBox.AddChild(CreateHoldButton("+", "zoom_in", new Godot.Vector2(40, 40)));
        zoomVBox.AddChild(new Label { Text = "ZOOM", HorizontalAlignment = HorizontalAlignment.Center });
        zoomVBox.AddChild(CreateHoldButton("-", "zoom_out", new Godot.Vector2(40, 40)));

        hbox.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(20, 0) });

        // 3. Pan Pad
        var panGrid = new GridContainer { Columns = 3 };
        hbox.AddChild(panGrid);

        panGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(40, 40) });
        panGrid.AddChild(CreateHoldButton("^", "pan_up"));
        panGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(40, 40) });

        panGrid.AddChild(CreateHoldButton("<", "pan_left"));
        panGrid.AddChild(new Label { Text = "PAN", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        panGrid.AddChild(CreateHoldButton(">", "pan_right"));

        panGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(40, 40) });
        panGrid.AddChild(CreateHoldButton("v", "pan_down"));
        panGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(40, 40) });

        hbox.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(20, 0) });

        // 4. Presets
        var presetVBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        hbox.AddChild(presetVBox);

        var btnFront = new Button { Text = "Front", CustomMinimumSize = new Godot.Vector2(60, 30) };
        btnFront.Pressed += () => _cameraController?.SetPresetView("front");
        presetVBox.AddChild(btnFront);

        var btnSide = new Button { Text = "Side", CustomMinimumSize = new Godot.Vector2(60, 30) };
        btnSide.Pressed += () => _cameraController?.SetPresetView("side");
        presetVBox.AddChild(btnSide);

        var btnRear = new Button { Text = "Rear", CustomMinimumSize = new Godot.Vector2(60, 30) };
        btnRear.Pressed += () => _cameraController?.SetPresetView("rear");
        presetVBox.AddChild(btnRear);
    }

    private Button CreateHoldButton(string text, string actionName, Godot.Vector2 minSize = default)
    {
        if (minSize == default) minSize = new Godot.Vector2(40, 40);
        var btn = new Button { Text = text, CustomMinimumSize = minSize };
        btn.ButtonDown += () => _activeAction = actionName;
        btn.ButtonUp += () =>
        {
            if (_activeAction == actionName) _activeAction = "";
        };
        return btn;
    }

    public override void _Process(double delta)
    {
        if (_cameraController == null)
        {
            // Try to find it if we haven't yet. Since Boot is the parent:
            _cameraController = GetParent()?.GetNodeOrNull<AvatarController>("AvatarController");
            if (_cameraController == null)
            {
                if (_statusLabel != null) _statusLabel.Text = "Waiting for Camera...";
                return;
            }
            GD.Print("[CameraHUD] AvatarController found and linked!");
        }

        if (_statusLabel != null)
        {
            _statusLabel.Text = string.IsNullOrEmpty(_activeAction) ? "Linked: Idle" : $"Linked: Active ({_activeAction})";
        }

        if (string.IsNullOrEmpty(_activeAction)) return;

        float rotSpeed = 2.0f * (float)delta;
        float panSpeed = 2.0f * (float)delta;
        float zoomSpeed = 15.0f * (float)delta;

        // GD.Print($"[CameraHUD] Active Action: {_activeAction}");

        switch (_activeAction)
        {
            case "rot_up": _cameraController.RotateCamera(new Godot.Vector2(0, rotSpeed)); break;
            case "rot_down": _cameraController.RotateCamera(new Godot.Vector2(0, -rotSpeed)); break;
            case "rot_left": _cameraController.RotateCamera(new Godot.Vector2(rotSpeed, 0)); break;
            case "rot_right": _cameraController.RotateCamera(new Godot.Vector2(-rotSpeed, 0)); break;

            case "pan_up": _cameraController.PanCamera(new Godot.Vector2(0, panSpeed)); break;
            case "pan_down": _cameraController.PanCamera(new Godot.Vector2(0, -panSpeed)); break;
            case "pan_left": _cameraController.PanCamera(new Godot.Vector2(-panSpeed, 0)); break;
            case "pan_right": _cameraController.PanCamera(new Godot.Vector2(panSpeed, 0)); break;

            case "zoom_in": _cameraController.ZoomCamera(-zoomSpeed); break;
            case "zoom_out": _cameraController.ZoomCamera(zoomSpeed); break;
        }
    }
}
