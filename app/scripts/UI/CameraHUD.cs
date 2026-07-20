using System;
using Godot;

namespace SLNG.App.UI;

public partial class CameraHUD : Control
{
    private AvatarController? _cameraController;
    private string _activeAction = "";
    private Label _statusLabel;
    private VBoxContainer _mainVBox;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        
        _mainVBox = new VBoxContainer();
        AddChild(_mainVBox);

        _statusLabel = new Label { Text = "HUD Init..." };
        _mainVBox.AddChild(_statusLabel);

        var hbox = new HBoxContainer();
        _mainVBox.AddChild(hbox);

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
        var presetsVBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        hbox.AddChild(presetsVBox);
        var btnFront = new Button { Text = "Front" };
        var btnSide = new Button { Text = "Side" };
        var btnRear = new Button { Text = "Rear" };
        presetsVBox.AddChild(btnFront);
        presetsVBox.AddChild(btnSide);
        presetsVBox.AddChild(btnRear);

        btnFront.Pressed += () => _cameraController?.SetPresetView("Front");
        btnSide.Pressed += () => _cameraController?.SetPresetView("Side");
        btnRear.Pressed += () => _cameraController?.SetPresetView("Rear");
    }

    private Button CreateHoldButton(string text, string actionName, Godot.Vector2 minSize = default)
    {
        if (minSize == default) minSize = new Godot.Vector2(40, 40);
        var btn = new Button { Text = text, CustomMinimumSize = minSize };
        btn.ButtonDown += () => _activeAction = actionName;
        btn.ButtonUp += () => { if (_activeAction == actionName) _activeAction = ""; };
        return btn;
    }

    public override void _Process(double delta)
    {
        var vpSize = GetViewportRect().Size;
        var mySize = _mainVBox.Size;
        // Absolute foolproof bottom-left positioning!
        _mainVBox.Position = new Godot.Vector2(20, vpSize.Y - mySize.Y - 20);

        if (_cameraController == null)
        {
            _cameraController = GetTree().Root.FindChild("AvatarController", true, false) as AvatarController;
            if (_cameraController != null)
            {
                _statusLabel.Text = "Camera linked";
                _statusLabel.RemoveThemeColorOverride("font_color");
                GD.Print("[CameraHUD] AvatarController found and linked!");
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(_activeAction))
            {
                switch (_activeAction)
                {
                    case "rot_left": _cameraController.RotateCamera(new Godot.Vector2(0.05f, 0)); break;
                    case "rot_right": _cameraController.RotateCamera(new Godot.Vector2(-0.05f, 0)); break;
                    case "rot_up": _cameraController.RotateCamera(new Godot.Vector2(0, 0.05f)); break;
                    case "rot_down": _cameraController.RotateCamera(new Godot.Vector2(0, -0.05f)); break;
                    case "zoom_in": _cameraController.ZoomCamera(-0.5f); break;
                    case "zoom_out": _cameraController.ZoomCamera(0.5f); break;
                    case "pan_left": _cameraController.PanCamera(new Godot.Vector2(1, 0)); break;
                    case "pan_right": _cameraController.PanCamera(new Godot.Vector2(-1, 0)); break;
                    case "pan_up": _cameraController.PanCamera(new Godot.Vector2(0, 1)); break;
                    case "pan_down": _cameraController.PanCamera(new Godot.Vector2(0, -1)); break;
                }
            }
        }
    }
}
