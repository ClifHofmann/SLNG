using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// Bottom-docked row of icon-only toggle buttons. Deliberately NOT an SLNGWindow: unlike
/// Camera HUD / Inventory / Preferences, this is a persistent, fixed, non-draggable-as-a-whole
/// toolbar -- only its individual buttons reorder among themselves. Which items appear and in
/// what order comes from ToolbarSettings, shared with the Preferences "Toolbar" tab so a
/// checkbox there and a drag here update the same persisted state.
///
/// Drag-to-reorder follows the same hand-rolled GuiInput pattern as SLNGWindow header
/// drag/resize (OnHeaderGuiInput/OnResizeGuiInput) rather than Godot native
/// _GetDragData/_DropData: each button tracks its own press/motion/release, and on motion we
/// compare the pointer global X against the midpoint of the adjacent button, swapping
/// (HBoxContainer.MoveChild) when it crosses -- one swap per motion event is enough since motion
/// events fire continuously while the mouse moves; a fast jump across more than one neighbor in
/// a single event just resolves over the next couple of events instead of one. Godot keeps
/// delivering GuiInput to whichever control was pressed for the rest of the gesture even once
/// the pointer wanders over a neighbor (implicit capture until button-up), so one handler per
/// dragged button is enough -- no manual mouse capture needed.
/// </summary>
public partial class ButtonBar : Control
{
    private IReadOnlyList<ToolbarItemDefinition> _items = Array.Empty<ToolbarItemDefinition>();
    private ToolbarSettings _settings = null!;
    private HBoxContainer _bottomBox = null!;
    private HBoxContainer _topBox = null!;
    private VBoxContainer _leftBox = null!;
    private VBoxContainer _rightBox = null!;
    private PanelContainer _bottomPanel = null!;
    private PanelContainer _topPanel = null!;
    private PanelContainer _leftPanel = null!;
    private PanelContainer _rightPanel = null!;

    private Button? _draggingButton;
    private Vector2 _dragStartPos;
    private bool _isDragging;

    private const string MetaKey = "toolbar_item_id";

    private Font _iconFont = null!;

    public override void _Ready()
    {
        _iconFont = GD.Load<Font>("res://assets/fonts/MaterialSymbolsOutlined.ttf");
        MouseFilter = Control.MouseFilterEnum.Ignore; // avoid blocking world/camera clicks outside the pill
        MakeFullRect(this);

        var styleBox = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.12f, 0.12f, 0.95f),
            BorderColor = new Color(0.3f, 0.3f, 0.3f, 0.5f),
            ShadowColor = new Color(0, 0, 0, 0.25f),
            ShadowSize = 4,
            ContentMarginLeft = 4,
            ContentMarginRight = 4,
            ContentMarginTop = 2,
            ContentMarginBottom = 2,
        };

        // Bottom
        var bottomMargin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(bottomMargin);
        MakeFullRect(bottomMargin);
        var bottomVBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = Control.MouseFilterEnum.Ignore };
        bottomMargin.AddChild(bottomVBox);
        _bottomPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var bStyle = (StyleBoxFlat)styleBox.Duplicate();
        bStyle.BorderWidthTop = 1;
        _bottomPanel.AddThemeStyleboxOverride("panel", bStyle);
        bottomVBox.AddChild(_bottomPanel);
        _bottomBox = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _bottomBox.AddThemeConstantOverride("separation", 2);
        _bottomPanel.AddChild(_bottomBox);

        // Top
        var topMargin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(topMargin);
        MakeFullRect(topMargin);
        topMargin.AddThemeConstantOverride("margin_top", 36);
        var topVBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Begin, MouseFilter = Control.MouseFilterEnum.Ignore };
        topMargin.AddChild(topVBox);
        _topPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var tStyle = (StyleBoxFlat)styleBox.Duplicate();
        tStyle.BorderWidthBottom = 1;
        _topPanel.AddThemeStyleboxOverride("panel", tStyle);
        topVBox.AddChild(_topPanel);
        _topBox = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _topBox.AddThemeConstantOverride("separation", 2);
        _topPanel.AddChild(_topBox);

        // Left
        var leftMargin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(leftMargin);
        MakeFullRect(leftMargin);
        leftMargin.AddThemeConstantOverride("margin_top", 36);
        var leftHBox = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Begin, MouseFilter = Control.MouseFilterEnum.Ignore };
        leftMargin.AddChild(leftHBox);
        _leftPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var lStyle = (StyleBoxFlat)styleBox.Duplicate();
        lStyle.BorderWidthRight = 1;
        _leftPanel.AddThemeStyleboxOverride("panel", lStyle);
        leftHBox.AddChild(_leftPanel);
        _leftBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _leftBox.AddThemeConstantOverride("separation", 2);
        _leftPanel.AddChild(_leftBox);

        // Right
        var rightMargin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(rightMargin);
        MakeFullRect(rightMargin);
        rightMargin.AddThemeConstantOverride("margin_top", 36);
        var rightHBox = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = Control.MouseFilterEnum.Ignore };
        rightMargin.AddChild(rightHBox);
        _rightPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var rStyle = (StyleBoxFlat)styleBox.Duplicate();
        rStyle.BorderWidthLeft = 1;
        _rightPanel.AddThemeStyleboxOverride("panel", rStyle);
        rightHBox.AddChild(_rightPanel);
        _rightBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _rightBox.AddThemeConstantOverride("separation", 2);
        _rightPanel.AddChild(_rightBox);
    }

    private void MakeFullRect(Control c)
    {
        c.SetAnchor(Side.Left, 0);
        c.SetAnchor(Side.Top, 0);
        c.SetAnchor(Side.Right, 1);
        c.SetAnchor(Side.Bottom, 1);
        c.OffsetLeft = 0;
        c.OffsetTop = 0;
        c.OffsetRight = 0;
        c.OffsetBottom = 0;
    }

    /// <summary>Wires the bar to the full item registry and its persisted enabled/order state.
    /// Call once, after both are constructed (see Boot.SetupHud).</summary>
    public void Initialize(IReadOnlyList<ToolbarItemDefinition> items, ToolbarSettings settings)
    {
        _items = items;
        _settings = settings;
        _settings.Changed += RebuildButtons;
        RebuildButtons();
    }

    private void RebuildButtons()
    {
        var boxes = new Container[] { _bottomBox, _topBox, _leftBox, _rightBox };
        foreach (var box in boxes)
        {
            var stale = new List<Node>();
            foreach (Node child in box.GetChildren()) stale.Add(child);
            foreach (var child in stale)
            {
                box.RemoveChild(child); // detach synchronously so the loop below sees a clean container
                child.QueueFree();
            }
        }

        foreach (var id in _settings.Order)
        {
            if (!_settings.IsEnabled(id)) continue;
            var def = _items.FirstOrDefault(i => i.Id == id);
            if (def == null) continue; // id from a build that no longer registers it -- ignore
            
            var pos = _settings.GetDockPosition(id);
            Container targetBox = pos switch {
                ToolbarDockPosition.Top => _topBox,
                ToolbarDockPosition.Left => _leftBox,
                ToolbarDockPosition.Right => _rightBox,
                _ => _bottomBox
            };
            targetBox.AddChild(BuildButton(def));
        }

        // Hide empty panels
        _bottomPanel.Visible = _bottomBox.GetChildCount() > 0;
        _topPanel.Visible = _topBox.GetChildCount() > 0;
        _leftPanel.Visible = _leftBox.GetChildCount() > 0;
        _rightPanel.Visible = _rightBox.GetChildCount() > 0;
    }

    private Button BuildButton(ToolbarItemDefinition def)
    {
        var btn = new Button
        {
            Text = def.IconGlyph,
            ToggleMode = true,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(48, 32),
            TooltipText = def.Label,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        btn.SetMeta(MetaKey, def.Id);
        btn.AddThemeFontOverride("font", _iconFont);
        btn.AddThemeFontSizeOverride("font_size", 24);
        btn.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.85f));
        btn.AddThemeColorOverride("font_pressed_color", new Color(1f, 1f, 1f));

        var normalStyle = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), CornerRadiusTopLeft = 0, CornerRadiusTopRight = 0, CornerRadiusBottomLeft = 0, CornerRadiusBottomRight = 0 };
        var hoverStyle = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.1f), CornerRadiusTopLeft = 0, CornerRadiusTopRight = 0, CornerRadiusBottomLeft = 0, CornerRadiusBottomRight = 0 };
        var pressedStyle = new StyleBoxFlat { BgColor = new Color(0.2f, 0.4f, 0.6f, 0.6f), CornerRadiusTopLeft = 0, CornerRadiusTopRight = 0, CornerRadiusBottomLeft = 0, CornerRadiusBottomRight = 0 };
        btn.AddThemeStyleboxOverride("normal", normalStyle);
        btn.AddThemeStyleboxOverride("hover", hoverStyle);
        btn.AddThemeStyleboxOverride("pressed", pressedStyle);
        btn.AddThemeStyleboxOverride("hover_pressed", pressedStyle);

        btn.Pressed += () => def.Toggle();
        btn.GuiInput += (@event) => OnButtonGuiInput(btn, @event);
        return btn;
    }

    private void OnButtonGuiInput(Button btn, InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _draggingButton = btn;
                _dragStartPos = mb.GlobalPosition;
                _isDragging = false;
            }
            else if (_draggingButton == btn)
            {
                btn.Modulate = Colors.White;
                if (_isDragging)
                {
                    EndDrag(btn);
                }
                _draggingButton = null;
                _isDragging = false;
            }
        }
        else if (@event is InputEventMouseMotion mm && _draggingButton == btn)
        {
            if (!_isDragging && mm.GlobalPosition.DistanceTo(_dragStartPos) > 4f)
            {
                _isDragging = true;
                btn.Modulate = new Color(1, 1, 1, 0.6f);
            }

            if (_isDragging)
            {
                TryReorder(btn, mm.GlobalPosition.X, mm.GlobalPosition.Y);
            }
        }
    }

    private void TryReorder(Button dragged, float mouseGlobalX, float mouseGlobalY)
    {
        var container = dragged.GetParent() as Container;
        if (container == null) return;

        bool isVertical = container is VBoxContainer;
        int idx = dragged.GetIndex();

        if (idx < container.GetChildCount() - 1 && container.GetChild(idx + 1) is Control next)
        {
            float nextMid = isVertical 
                ? next.GlobalPosition.Y + next.Size.Y / 2f
                : next.GlobalPosition.X + next.Size.X / 2f;
            float mousePos = isVertical ? mouseGlobalY : mouseGlobalX;

            if (mousePos > nextMid)
            {
                container.MoveChild(dragged, idx + 1);
                return;
            }
        }
        if (idx > 0 && container.GetChild(idx - 1) is Control prev)
        {
            float prevMid = isVertical
                ? prev.GlobalPosition.Y + prev.Size.Y / 2f
                : prev.GlobalPosition.X + prev.Size.X / 2f;
            float mousePos = isVertical ? mouseGlobalY : mouseGlobalX;

            if (mousePos < prevMid)
            {
                container.MoveChild(dragged, idx - 1);
            }
        }
    }

    private void EndDrag(Button btn)
    {
        var newEnabledOrder = new List<string>();
        // Gather order from all boxes.
        var boxes = new Container[] { _bottomBox, _rightBox, _topBox, _leftBox };
        foreach (var box in boxes)
        {
            foreach (Node child in box.GetChildren())
                if (child.HasMeta(MetaKey))
                    newEnabledOrder.Add((string)child.GetMeta(MetaKey));
        }

        // Persists and raises Changed -> RebuildButtons picks the (already correctly ordered)
        // buttons back up; harmless since the drag has already ended by this point.
        _settings.SetOrderForEnabled(newEnabledOrder);
    }

    public override void _Process(double delta)
    {
        // Safety net: if a button-up GuiInput was somehow missed (e.g. focus moved to another
        // window mid-drag), avoid leaving the bar permanently stuck thinking it is still dragging.
        if (_draggingButton != null && !Input.IsMouseButtonPressed(MouseButton.Left))
        {
            if (_isDragging)
            {
                EndDrag(_draggingButton);
            }
            _draggingButton.Modulate = Colors.White;
            _draggingButton = null;
            _isDragging = false;
        }

        // Sync the pressed look of every button with the target panel visibility
        var boxes = new Container[] { _bottomBox, _topBox, _leftBox, _rightBox };
        foreach (var box in boxes)
        {
            foreach (Node child in box.GetChildren())
            {
                if (child is not Button btn || !btn.HasMeta(MetaKey)) continue;
                var id = (string)btn.GetMeta(MetaKey);
                var def = _items.FirstOrDefault(i => i.Id == id);
                if (def?.IsActive != null) btn.ButtonPressed = def.IsActive();
            }
        }
    }
}
