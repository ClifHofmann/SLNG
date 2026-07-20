using System;
using Godot;

namespace SLNG.App.UI;

public partial class CameraHUD : Window
{
    private AvatarController? _cameraController;
    private string _activeAction = "";
    private Label _statusLabel;

    public override void _Ready()
    {
        Title = "Camera Controls";
        Size = new Vector2I(320, 220);
        MinSize = new Vector2I(300, 200);
        WrapControls = true;
        Unresizable = false;
        
        CloseRequested += Hide;

        // Dark theme panel
        var panel = new PanelContainer();
        panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var styleBox = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.12f, 0.12f, 0.95f),
            BorderWidthBottom = 1,
            BorderWidthTop = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderColor = new Color(0.3f, 0.3f, 0.3f, 0.8f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4
        };
        panel.AddThemeStyleboxOverride("panel", styleBox);
        AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        panel.AddChild(margin);

        var mainVBox = new VBoxContainer { ThemeConstants = { { "separation", 8 } } };
        margin.AddChild(mainVBox);

        _statusLabel = new Label { Text = "HUD Init...", HorizontalAlignment = HorizontalAlignment.Center };
        mainVBox.AddChild(_statusLabel);

        var hbox = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, ThemeConstants = { { "separation", 20 } } };
        mainVBox.AddChild(hbox);

        // 1. Rotation Pad
        var rotGrid = new GridContainer { Columns = 3 };
        hbox.AddChild(rotGrid);
        rotGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(32, 32) });
        rotGrid.AddChild(CreateHoldButton("^", "rot_up", new Godot.Vector2(32, 32)));
        rotGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(32, 32) });
        rotGrid.AddChild(CreateHoldButton("<", "rot_left", new Godot.Vector2(32, 32)));
        rotGrid.AddChild(new Label { Text = "ROT", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        rotGrid.AddChild(CreateHoldButton(">", "rot_right", new Godot.Vector2(32, 32)));
        rotGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(32, 32) });
        rotGrid.AddChild(CreateHoldButton("v", "rot_down", new Godot.Vector2(32, 32)));
        rotGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(32, 32) });

        // 2. Zoom Buttons
        var zoomVBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        hbox.AddChild(zoomVBox);
        zoomVBox.AddChild(CreateHoldButton("+", "zoom_in", new Godot.Vector2(32, 44)));
        zoomVBox.AddChild(CreateHoldButton("-", "zoom_out", new Godot.Vector2(32, 44)));

        // 3. Pan Pad
        var panGrid = new GridContainer { Columns = 3 };
        hbox.AddChild(panGrid);
        panGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(32, 32) });
        panGrid.AddChild(CreateHoldButton("^", "pan_up", new Godot.Vector2(32, 32)));
        panGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(32, 32) });
        panGrid.AddChild(CreateHoldButton("<", "pan_left", new Godot.Vector2(32, 32)));
        panGrid.AddChild(new Label { Text = "PAN", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        panGrid.AddChild(CreateHoldButton(">", "pan_right", new Godot.Vector2(32, 32)));
        panGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(32, 32) });
        panGrid.AddChild(CreateHoldButton("v", "pan_down", new Godot.Vector2(32, 32)));
        panGrid.AddChild(new Control { CustomMinimumSize = new Godot.Vector2(32, 32) });

        // 4. Presets
        var presetsHBox = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        mainVBox.AddChild(presetsHBox);
        var btnFront = new Button { Text = "Front" };
        var btnSide = new Button { Text = "Side" };
        var btnRear = new Button { Text = "Rear" };
        presetsHBox.AddChild(btnFront);
        presetsHBox.AddChild(btnSide);
        presetsHBox.AddChild(btnRear);

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
