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

    public const float MinUiScale = 0.8f;
    public const float MaxUiScale = 1.6f;
    private static float _globalUiScale = 1.0f;

    /// <summary>Current global window/HUD scale (FEAT-UI-07), shared by every SLNGWindow
    /// instance. UiSettings owns persistence; this is just the live broadcast value.</summary>
    public static float GlobalUiScale => _globalUiScale;

    /// <summary>Raised after a change so already-open windows rescale live instead of only
    /// picking up the new value on next open.</summary>
    public static event Action<float>? GlobalUiScaleChanged;

    /// <summary>Sets the scale every SLNGWindow (current and future) renders at. Clamped to
    /// [MinUiScale, MaxUiScale] -- callers (UiSettings) don't need to duplicate the range.</summary>
    public static void SetGlobalUiScale(float scale)
    {
        scale = Mathf.Clamp(scale, MinUiScale, MaxUiScale);
        if (Mathf.IsEqualApprox(scale, _globalUiScale)) return;
        _globalUiScale = scale;
        GlobalUiScaleChanged?.Invoke(scale);
    }

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

        // Stop (not the container-default Pass) so every click/scroll landing anywhere inside
        // the window's rect -- including blank padding and gaps a child Control doesn't cover,
        // e.g. between Tree rows -- is consumed here instead of leaking through to whatever's
        // rendered behind (CameraHUD buttons, the 3D viewport's own input/camera zoom). Pass
        // would still let children handle their own input first, but afterward continues the
        // event to siblings/behind regardless, which is exactly the "clicks go through the
        // window" bug this fixes.
        MouseFilter = MouseFilterEnum.Stop;

        // FEAT-UI-07: scale grows from the top-left (PivotOffset default (0,0)), so Position
        // keeps meaning "where the window's corner sits" regardless of scale.
        Scale = new Vector2(_globalUiScale, _globalUiScale);
        GlobalUiScaleChanged += OnGlobalUiScaleChanged;

        // Add a drop shadow or outline margin around the actual panel
        AddThemeConstantOverride("margin_left", 8);
        AddThemeConstantOverride("margin_right", 8);
        AddThemeConstantOverride("margin_top", 8);
        AddThemeConstantOverride("margin_bottom", 8);

        var bgPanel = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
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

    public override void _ExitTree()
    {
        GlobalUiScaleChanged -= OnGlobalUiScaleChanged;
        base._ExitTree();
    }

    /// <summary>Raises this window above every other overlapping SLNGWindow the instant it's
    /// clicked -- anywhere inside it, not just the header. Deliberately uses <c>_Input</c> (fires
    /// for every event, before GUI dispatch/consumption) rather than a GuiInput/_gui_input hook:
    /// a click on an interactive child (a Tree row, a Button) is consumed right there and never
    /// bubbles up to this window's own gui_input, so a bubble-based hook would miss most clicks.
    /// <c>GuiGetHoveredControl()</c> reflects true rendered stacking order (unlike a raw rect
    /// check, which can't tell two overlapping windows apart), so only the window actually under
    /// the cursor raises itself -- no fighting between overlapping windows on the same click.</summary>
    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true }) return;
        var hovered = GetViewport().GuiGetHoveredControl();
        if (hovered != this && (hovered == null || !IsAncestorOf(hovered))) return;

        // Raise this window AND every ancestor Control up to the CanvasLayer -- a window nested
        // inside another SLNGWindow (e.g. ItemPropertiesWindow, added as a child of InventoryPanel
        // rather than a top-level hudLayer sibling) would otherwise only reorder itself among its
        // immediate parent's children, never actually bringing InventoryPanel itself above its
        // own top-level siblings (ChatWindow, PreferencesWindow, ...). MoveToFront() on a Control
        // that's already frontmost among its siblings is a cheap no-op, so walking the whole
        // chain unconditionally costs nothing extra in the common (non-nested) case.
        for (Node? n = this; n is Control c; n = n.GetParent())
            c.MoveToFront();
    }

    private void OnGlobalUiScaleChanged(float scale) => Scale = new Vector2(scale, scale);

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
                // GlobalPosition/mouseMotion.GlobalPosition are in screen space; Size is the
                // pre-scale local rect, so the screen-space delta must be un-scaled before it's
                // assigned back, or dragging at e.g. 1.5x UI scale would resize 1.5x faster than
                // the cursor moves (FEAT-UI-07).
                var newSize = (mouseMotion.GlobalPosition - GlobalPosition) / Scale;
                newSize.X = Mathf.Max(newSize.X, CustomMinimumSize.X > 0 ? CustomMinimumSize.X : 100);
                newSize.Y = Mathf.Max(newSize.Y, CustomMinimumSize.Y > 0 ? CustomMinimumSize.Y : 100);
                Size = newSize;
            }
        }
    }
}
