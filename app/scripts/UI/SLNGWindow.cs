using Godot;
using System;

namespace SLNG.App.UI;

public partial class SLNGWindow : MarginContainer
{
    private Label _titleLabel = null!;
    private MarginContainer _contentContainer = null!;
    private PanelContainer _headerPanel = null!;
    private Button _closeButton = null!;
    
    private bool _isDragging = false;
    private Vector2 _dragOffset;
    private bool _isResizing = false;
    private Control _resizeHandle = null!;

    public string Title 
    {
        get => _titleLabel?.Text ?? "";
        set 
        {
            if (_titleLabel != null) _titleLabel.Text = value.ToUpperInvariant();
        }
    }

    public MarginContainer ContentContainer => _contentContainer;

    // Triggered when close is requested. By default, hides the window.
    public Action? OnCloseRequested;

    public override void _Ready()
    {
        // Allow free positioning (not constrained by parent containers if placed inside a standard Control)
        SetAnchorsPreset(LayoutPreset.TopLeft);
        
        // Add a drop shadow or outline margin around the actual panel
        AddThemeConstantOverride("margin_left", 8);
        AddThemeConstantOverride("margin_right", 8);
        AddThemeConstantOverride("margin_top", 8);
        AddThemeConstantOverride("margin_bottom", 8);

        var bgPanel = new PanelContainer();
        AddChild(bgPanel);

        var styleBox = new StyleBoxFlat
        {
            // Darker than the original 0.5 -- text contrast against a bright in-world background
            // (sky, snow, a light-colored build) was reported too low across every SLNGWindow.
            BgColor = new Color(0, 0, 0, 0.65f),
            CornerRadiusTopLeft = 16,
            CornerRadiusTopRight = 16,
            CornerRadiusBottomLeft = 16,
            CornerRadiusBottomRight = 16,
            BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = new Color(1, 1, 1, 0.1f),
            ShadowColor = new Color(0, 0, 0, 0.25f),
            ShadowSize = 16,
            ShadowOffset = new Vector2(0, 6)
        };
        bgPanel.AddThemeStyleboxOverride("panel", styleBox);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 0);
        bgPanel.AddChild(vbox);

        // Header
        _headerPanel = new PanelContainer();
        _headerPanel.MouseFilter = MouseFilterEnum.Stop; // To catch drag events
        _headerPanel.GuiInput += OnHeaderGuiInput;
        
        var headerStyle = new StyleBoxFlat
        {
            BgColor = new Color(1, 1, 1, 0.05f),
            CornerRadiusTopLeft = 16,
            CornerRadiusTopRight = 16,
            BorderWidthBottom = 1,
            BorderColor = new Color(1, 1, 1, 0.05f)
        };
        _headerPanel.AddThemeStyleboxOverride("panel", headerStyle);
        vbox.AddChild(_headerPanel);

        var headerMargin = new MarginContainer();
        headerMargin.AddThemeConstantOverride("margin_left", 16);
        headerMargin.AddThemeConstantOverride("margin_right", 16);
        headerMargin.AddThemeConstantOverride("margin_top", 12);
        headerMargin.AddThemeConstantOverride("margin_bottom", 12);
        headerMargin.MouseFilter = MouseFilterEnum.Pass;
        _headerPanel.AddChild(headerMargin);

        var headerHBox = new HBoxContainer();
        headerHBox.MouseFilter = MouseFilterEnum.Pass;
        headerMargin.AddChild(headerHBox);

        _titleLabel = new Label
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Pass
        };
        _titleLabel.AddThemeFontSizeOverride("font_size", 12);
        _titleLabel.AddThemeColorOverride("font_color", new Color(0.88f, 0.88f, 0.88f));
        headerHBox.AddChild(_titleLabel);

        _closeButton = new Button
        {
            Text = "×",
            Flat = true,
            CustomMinimumSize = new Vector2(16, 16),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            FocusMode = FocusModeEnum.None
        };
        _closeButton.AddThemeFontSizeOverride("font_size", 12);
        _closeButton.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
        _closeButton.AddThemeColorOverride("font_hover_color", new Color(1f, 0.4f, 0.4f));
        _closeButton.Pressed += () => {
            if (OnCloseRequested != null) OnCloseRequested.Invoke();
            else Visible = false;
        };
        headerHBox.AddChild(_closeButton);

        // Content
        _contentContainer = new MarginContainer();
        _contentContainer.SizeFlagsVertical = SizeFlags.ExpandFill;
        vbox.AddChild(_contentContainer);

        // Resize Handle
        _resizeHandle = new Control
        {
            CustomMinimumSize = new Vector2(16, 16),
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
            SizeFlagsVertical = SizeFlags.ShrinkEnd,
            MouseFilter = MouseFilterEnum.Stop,
            MouseDefaultCursorShape = CursorShape.Fdiagsize
        };
        _resizeHandle.GuiInput += OnResizeGuiInput;
        _resizeHandle.Draw += () =>
        {
            var points = new Vector2[]
            {
                new Vector2(16, 10),
                new Vector2(16, 16),
                new Vector2(10, 16)
            };
            var colors = new Color[]
            {
                new Color(1, 1, 1, 0.1f),
                new Color(1, 1, 1, 0.1f),
                new Color(1, 1, 1, 0.1f)
            };
            _resizeHandle.DrawPolygon(points, colors);
        };
        AddChild(_resizeHandle);
    }

    private void OnHeaderGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseBtn)
        {
            if (mouseBtn.ButtonIndex == MouseButton.Left)
            {
                if (mouseBtn.Pressed)
                {
                    _isDragging = true;
                    _dragOffset = mouseBtn.GlobalPosition - GlobalPosition;
                }
                else
                {
                    _isDragging = false;
                }
            }
        }
        else if (@event is InputEventMouseMotion mouseMotion)
        {
            if (_isDragging)
            {
                GlobalPosition = mouseMotion.GlobalPosition - _dragOffset;
            }
        }
    }

    private void OnResizeGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseBtn)
        {
            if (mouseBtn.ButtonIndex == MouseButton.Left)
            {
                if (mouseBtn.Pressed)
                {
                    _isResizing = true;
                    _resizeHandle.AcceptEvent(); // Capture mouse drag
                }
                else
                {
                    _isResizing = false;
                }
            }
        }
        else if (@event is InputEventMouseMotion mouseMotion)
        {
            if (_isResizing)
            {
                var newSize = mouseMotion.GlobalPosition - GlobalPosition;
                newSize.X = Mathf.Max(newSize.X, CustomMinimumSize.X > 0 ? CustomMinimumSize.X : 100);
                newSize.Y = Mathf.Max(newSize.Y, CustomMinimumSize.Y > 0 ? CustomMinimumSize.Y : 100);
                Size = newSize;
            }
        }
    }
}
