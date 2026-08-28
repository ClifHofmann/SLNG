using Godot;
using System;

namespace SLNG.App.UI;

public partial class SLNGWindow : MarginContainer
{
    private Label _titleLabel = null!;
    private MarginContainer _contentContainer = null!;
    private PanelContainer _headerPanel = null!;
    private Button _minimizeButton = null!;
    private Button _closeButton = null!;

    private bool _isDragging = false;
    private Vector2 _dragOffset;
    private Vector2 _preMinimizeSize;
    private Vector2 _preMinimizeMinSize;
    private bool _isMinimized;
    private Viewport? _viewport;

    [Flags]
    private enum ResizeEdge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    // All 4 edges + 4 corners, not just a single bottom-right corner -- see AddResizeHandles'
    // doc comment for why a coupled single handle was a real bug, not just a UX nicety.
    private readonly System.Collections.Generic.List<Control> _resizeHandles = new();
    private bool _isResizing;
    private ResizeEdge _resizingEdges;
    private Vector2 _resizeStartMouseGlobal;
    private Vector2 _resizeStartPos;
    private Vector2 _resizeStartSize;

    public const float MinUiScale = 0.8f;
    public const float MaxUiScale = 1.6f;
    private static float _globalUiScale = 1.0f;

    private const string GeometryConfigPath = "user://preferences.cfg";
    private const string GeometrySection = "window_geometry";

    /// <summary>Set by a subclass (e.g. "camera_hud") to opt this window into position/size
    /// persistence across sessions. Left null, a window keeps today's behavior -- reopens at
    /// whatever Position/CustomMinimumSize its own _Ready sets every time.</summary>
    protected string? PersistId;

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

        // FEAT-UI-11: pull this window back into view whenever the main viewport shrinks, so a
        // resize can't strand a floating window off-screen. Cached because GetViewport() is not
        // reliable from _ExitTree, and the subscription must be removed there (the root Viewport
        // outlives every window, so a leaked delegate would fire on a freed object).
        _viewport = GetViewport();
        if (_viewport != null) _viewport.SizeChanged += OnViewportSizeChanged;

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
        headerMargin.AddThemeConstantOverride("margin_left", 12);
        headerMargin.AddThemeConstantOverride("margin_right", 12);
        headerMargin.AddThemeConstantOverride("margin_top", 6);
        headerMargin.AddThemeConstantOverride("margin_bottom", 6);
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

        _minimizeButton = new Button
        {
            Text = "_",
            Flat = true,
            CustomMinimumSize = new Vector2(16, 16),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            FocusMode = FocusModeEnum.None
        };
        _minimizeButton.AddThemeFontSizeOverride("font_size", 12);
        _minimizeButton.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
        _minimizeButton.AddThemeColorOverride("font_hover_color", new Color(1f, 1f, 1f));
        _minimizeButton.Pressed += ToggleMinimize;
        headerHBox.AddChild(_minimizeButton);

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
        // Content that is larger than the window must be cut off at the frame, not painted over the
        // 3D world beyond it. Without this a page whose minimum size exceeds the window simply
        // overflows and draws outside -- which is exactly what the Preferences dialog did once the
        // Graphics tab grew. Clipping here fixes it for every window rather than one page at a time.
        _contentContainer.ClipContents = true;
        vbox.AddChild(_contentContainer);

        AddResizeHandles();

        // Deferred so it runs after the calling subclass's _Ready has finished setting its own
        // default Position/CustomMinimumSize (base._Ready() always runs first in those overrides)
        // -- otherwise the subclass's defaults would stomp the restored geometry right back.
        CallDeferred(nameof(RestorePersistedGeometry));
    }

    /// <summary>All 4 edges + 4 corners get their own drag handle, not just a single bottom-right
    /// corner. A single corner handle COUPLES width and height on every drag -- there is no way
    /// to narrow the window without also dragging its height, or vice versa. That coupling was
    /// live-tested to actually corrupt saved geometry: an attempt to narrow a window overshot
    /// vertically and saved a 480x1236 window, which then reopened at that broken size on every
    /// subsequent launch forever (nothing ever re-checked it against a sane maximum -- see
    /// RestorePersistedGeometry's matching fix). Corners are added AFTER edges so they win the
    /// hit-test at the exact pixel where an edge and a corner handle would otherwise overlap.</summary>
    private void AddResizeHandles()
    {
        const float edgeThickness = 8f;
        const float cornerSize = 16f;

        AddResizeHandle(ResizeEdge.Top, SizeFlags.ExpandFill, SizeFlags.ShrinkBegin, new Vector2(0, edgeThickness), CursorShape.Vsize);
        AddResizeHandle(ResizeEdge.Bottom, SizeFlags.ExpandFill, SizeFlags.ShrinkEnd, new Vector2(0, edgeThickness), CursorShape.Vsize);
        AddResizeHandle(ResizeEdge.Left, SizeFlags.ShrinkBegin, SizeFlags.ExpandFill, new Vector2(edgeThickness, 0), CursorShape.Hsize);
        AddResizeHandle(ResizeEdge.Right, SizeFlags.ShrinkEnd, SizeFlags.ExpandFill, new Vector2(edgeThickness, 0), CursorShape.Hsize);
        AddResizeHandle(ResizeEdge.Top | ResizeEdge.Left, SizeFlags.ShrinkBegin, SizeFlags.ShrinkBegin, new Vector2(cornerSize, cornerSize), CursorShape.Fdiagsize);
        AddResizeHandle(ResizeEdge.Top | ResizeEdge.Right, SizeFlags.ShrinkEnd, SizeFlags.ShrinkBegin, new Vector2(cornerSize, cornerSize), CursorShape.Bdiagsize);
        AddResizeHandle(ResizeEdge.Bottom | ResizeEdge.Left, SizeFlags.ShrinkBegin, SizeFlags.ShrinkEnd, new Vector2(cornerSize, cornerSize), CursorShape.Bdiagsize);
        var seCorner = AddResizeHandle(ResizeEdge.Bottom | ResizeEdge.Right, SizeFlags.ShrinkEnd, SizeFlags.ShrinkEnd, new Vector2(cornerSize, cornerSize), CursorShape.Fdiagsize);

        // Keep the original visual grip on the bottom-right corner -- the one spot users actually
        // look for a resize affordance; the other seven are invisible-but-functional, matching how
        // most desktop apps only decorate the one conventional corner.
        seCorner.Draw += () =>
        {
            var points = new[] { new Vector2(cornerSize, cornerSize - 6), new Vector2(cornerSize, cornerSize), new Vector2(cornerSize - 6, cornerSize) };
            var colors = new[] { new Color(1, 1, 1, 0.1f), new Color(1, 1, 1, 0.1f), new Color(1, 1, 1, 0.1f) };
            seCorner.DrawPolygon(points, colors);
        };
    }

    private Control AddResizeHandle(ResizeEdge edges, SizeFlags h, SizeFlags v, Vector2 minSize, CursorShape cursor)
    {
        var handle = new Control
        {
            CustomMinimumSize = minSize,
            SizeFlagsHorizontal = h,
            SizeFlagsVertical = v,
            MouseFilter = MouseFilterEnum.Stop,
            MouseDefaultCursorShape = cursor,
        };
        handle.GuiInput += e => OnResizeGuiInput(edges, e);
        AddChild(handle);
        _resizeHandles.Add(handle);
        return handle;
    }

    /// <summary>No-op unless a subclass opted in via <see cref="PersistId"/>. Loads the saved
    /// Position/Size from user://preferences.cfg, clamped so a window saved on a larger/different
    /// screen still reopens at least partially on-screen instead of stranded off-viewport, AND
    /// clamped to the current viewport's size on the tall/wide side -- a size saved before the
    /// resize-handle fix above (or from any other future bad drag) self-heals here instead of
    /// reopening broken forever, since this is the one place every restart actually passes through.</summary>
    private void RestorePersistedGeometry()
    {
        if (string.IsNullOrEmpty(PersistId)) return;

        var cfg = new ConfigFile();
        if (cfg.Load(GeometryConfigPath) != Error.Ok) return;

        if (cfg.HasSectionKey(GeometrySection, $"{PersistId}_pos"))
            Position = (Vector2)cfg.GetValue(GeometrySection, $"{PersistId}_pos");
        if (cfg.HasSectionKey(GeometrySection, $"{PersistId}_size"))
        {
            var savedSize = (Vector2)cfg.GetValue(GeometrySection, $"{PersistId}_size");
            var vp = (_viewport ?? GetViewport())?.GetVisibleRect().Size ?? new Vector2(4096, 4096);
            float maxW = Mathf.Max(CustomMinimumSize.X, vp.X / Mathf.Max(Scale.X, 0.01f));
            float maxH = Mathf.Max(CustomMinimumSize.Y, vp.Y / Mathf.Max(Scale.Y, 0.01f));
            Size = new Vector2(
                Mathf.Clamp(savedSize.X, CustomMinimumSize.X, maxW),
                Mathf.Clamp(savedSize.Y, CustomMinimumSize.Y, maxH));
        }

        // Restored from a possibly larger / different-resolution session -- keep it on screen.
        ClampToViewport();
    }

    /// <summary>No-op unless a subclass opted in via <see cref="PersistId"/>. Called after every
    /// drag/resize gesture completes -- persists live rather than only on app exit, so a crash
    /// doesn't lose the last-arranged layout.</summary>
    private void SavePersistedGeometry()
    {
        if (string.IsNullOrEmpty(PersistId)) return;

        var cfg = new ConfigFile();
        cfg.Load(GeometryConfigPath); // preserve sections owned by other features (UiSettings, ToolbarSettings)
        cfg.SetValue(GeometrySection, $"{PersistId}_pos", Position);
        // While minimized the live Size is just the collapsed header; persist the real frame size
        // so the window reopens full-height next session rather than stranded as a tiny bar.
        cfg.SetValue(GeometrySection, $"{PersistId}_size", _isMinimized ? _preMinimizeSize : Size);
        cfg.Save(GeometryConfigPath);
    }

    /// <summary>BUG-UI-01: collapse the whole outer frame to the title bar, not just hide the
    /// content. Setting <see cref="Control.Size"/> alone never worked -- this is a
    /// <see cref="Container"/> (MarginContainer), so its height is floored by
    /// <c>GetCombinedMinimumSize()</c> = max(CustomMinimumSize.Y, inner content min height), and
    /// every subclass sets a CustomMinimumSize (e.g. CameraHUD 160x140, ChatWindow 400x340) that
    /// clamped the "minimized" frame straight back to full height.</summary>
    private void ToggleMinimize()
    {
        if (!_isMinimized)
        {
            _preMinimizeSize = Size;
            _preMinimizeMinSize = CustomMinimumSize;
            _isMinimized = true;

            _contentContainer.Visible = false;
            foreach (var h in _resizeHandles) h.Visible = false;

            // Drop the height floor so the frame can shrink to the header. Keep the width floor --
            // a window that also snapped narrow on minimize would truncate its own title and feel
            // jarring. The actual resize is deferred (ApplyMinimizedSize): hiding the content
            // invalidates the inner layout's minimum size, but that isn't recomputed until the
            // next layout pass, so a synchronous `Size =` here still clamps against the old height.
            CustomMinimumSize = new Vector2(CustomMinimumSize.X, 0);
            CallDeferred(nameof(ApplyMinimizedSize));
        }
        else
        {
            _isMinimized = false;
            CustomMinimumSize = _preMinimizeMinSize;
            _contentContainer.Visible = true;
            foreach (var h in _resizeHandles) h.Visible = true;
            Size = _preMinimizeSize; // Restore the pre-minimize frame size
        }
    }

    /// <summary>Second half of the minimize path, run deferred so the content container's
    /// visibility change has propagated into the layout's minimum-size calculation. Collapses the
    /// outer frame to the header's natural height, leaving the width untouched.</summary>
    private void ApplyMinimizedSize()
    {
        if (!_isMinimized) return; // toggled back open before the deferred call landed
        Size = new Vector2(Size.X, GetCombinedMinimumSize().Y);
    }

    public override void _ExitTree()
    {
        GlobalUiScaleChanged -= OnGlobalUiScaleChanged;
        if (_viewport != null) _viewport.SizeChanged -= OnViewportSizeChanged;
        base._ExitTree();
    }

    /// <summary>FEAT-UI-11: main viewport resized -- re-clamp and, if the window was actually moved
    /// and it persists, save the corrected spot. Guarded on "actually moved" so dragging the app
    /// window's edge doesn't hammer the config file once per resize event per open window.</summary>
    private void OnViewportSizeChanged()
    {
        if (ClampToViewport()) SavePersistedGeometry();
    }

    /// <summary>Pushes the window back inside the current viewport so a >=40 px sliver stays on
    /// screen on each axis and the top edge never goes above the viewport -- i.e. some of the
    /// (full-width) title bar is always visible and grab-able. Footprint is <c>Size * Scale</c>
    /// (FEAT-UI-07). Returns true if it had to move the window.</summary>
    private bool ClampToViewport()
    {
        var vp = (_viewport ?? GetViewport())?.GetVisibleRect().Size ?? Vector2.Zero;
        if (vp.X <= 0f || vp.Y <= 0f) return false;

        float w = Size.X * Scale.X;
        var clamped = new Vector2(
            Mathf.Clamp(Position.X, -w + 40f, Mathf.Max(vp.X - 40f, 0f)),
            Mathf.Clamp(Position.Y, 0f, Mathf.Max(vp.Y - 40f, 0f)));

        if (clamped == Position) return false;
        Position = clamped;
        return true;
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

    private void OnGlobalUiScaleChanged(float scale)
    {
        var globalMouse = GetGlobalMousePosition();
        if (GetGlobalRect().HasPoint(globalMouse))
        {
            var localMouse = GetLocalMousePosition();
            var newScale = new Vector2(scale, scale);
            GlobalPosition = globalMouse - (localMouse * newScale);
            Scale = newScale;
            SavePersistedGeometry();
        }
        else
        {
            Scale = new Vector2(scale, scale);
        }
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
                    SavePersistedGeometry();
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

    /// <summary>Shared by all 8 resize handles, parameterised by which edges the pressed handle
    /// moves. Godot keeps delivering GuiInput to whichever control the mouse button went down on
    /// until it comes back up, regardless of where the cursor wanders in between (the same
    /// property the original single-handle version already relied on), so no per-handle "am I the
    /// active one" check is needed beyond the shared <see cref="_isResizing"/> flag.</summary>
    private void OnResizeGuiInput(ResizeEdge edges, InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseBtn && mouseBtn.ButtonIndex == MouseButton.Left)
        {
            if (mouseBtn.Pressed)
            {
                _isResizing = true;
                _resizingEdges = edges;
                _resizeStartMouseGlobal = mouseBtn.GlobalPosition;
                _resizeStartPos = Position;
                _resizeStartSize = Size;
            }
            else
            {
                _isResizing = false;
                SavePersistedGeometry();
            }
        }
        else if (@event is InputEventMouseMotion mouseMotion && _isResizing)
        {
            ApplyResize(mouseMotion.GlobalPosition);
        }
    }

    /// <summary>Moves whichever edges/corner is being dragged, computed from the drag's START
    /// state (not incrementally frame-to-frame) so small per-frame rounding never accumulates.
    /// GlobalPosition/mouseGlobal are screen space; Size/Position are the pre-scale local rect, so
    /// the screen-space delta must be un-scaled before use, or dragging at e.g. 1.5x UI scale
    /// would resize 1.5x faster than the cursor moves (FEAT-UI-07). Both a minimum (the window's
    /// own CustomMinimumSize) and a MAXIMUM (the current viewport) are enforced -- the missing
    /// maximum on the single old corner handle is what let a stray drag save an unusable
    /// 480x1236 window in the first place (RestorePersistedGeometry has the matching fix for a
    /// value already saved before this existed).</summary>
    private void ApplyResize(Vector2 mouseGlobal)
    {
        var delta = (mouseGlobal - _resizeStartMouseGlobal) / Scale;

        float minW = CustomMinimumSize.X > 0 ? CustomMinimumSize.X : 100;
        float minH = CustomMinimumSize.Y > 0 ? CustomMinimumSize.Y : 100;
        var vp = (_viewport ?? GetViewport())?.GetVisibleRect().Size ?? new Vector2(4096, 4096);
        float maxW = Mathf.Max(minW, vp.X / Mathf.Max(Scale.X, 0.01f));
        float maxH = Mathf.Max(minH, vp.Y / Mathf.Max(Scale.Y, 0.01f));

        float x = _resizeStartPos.X, y = _resizeStartPos.Y;
        float w = _resizeStartSize.X, h = _resizeStartSize.Y;

        if ((_resizingEdges & ResizeEdge.Right) != 0)
            w = Mathf.Clamp(_resizeStartSize.X + delta.X, minW, maxW);
        if ((_resizingEdges & ResizeEdge.Bottom) != 0)
            h = Mathf.Clamp(_resizeStartSize.Y + delta.Y, minH, maxH);
        if ((_resizingEdges & ResizeEdge.Left) != 0)
        {
            w = Mathf.Clamp(_resizeStartSize.X - delta.X, minW, maxW);
            x = _resizeStartPos.X + (_resizeStartSize.X - w);
        }
        if ((_resizingEdges & ResizeEdge.Top) != 0)
        {
            h = Mathf.Clamp(_resizeStartSize.Y - delta.Y, minH, maxH);
            y = _resizeStartPos.Y + (_resizeStartSize.Y - h);
        }

        Position = new Vector2(x, y);
        Size = new Vector2(w, h);
    }
}
