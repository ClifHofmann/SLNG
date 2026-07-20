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
    private HBoxContainer _hbox = null!;

    private Button? _draggingButton;
    private Vector2 _dragStartPos;
    private bool _isDragging;

    private const string MetaKey = "toolbar_item_id";

    private Font _iconFont = null!;

    public override void _Ready()
    {
        _iconFont = GD.Load<Font>("res://assets/fonts/MaterialSymbolsOutlined.ttf");
        MouseFilter = Control.MouseFilterEnum.Ignore; // avoid blocking world/camera clicks outside the pill

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_bottom", 0);
        margin.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(margin);

        var vbox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        vbox.MouseFilter = Control.MouseFilterEnum.Ignore;
        margin.AddChild(vbox);

        var barPanel = new PanelContainer();
        barPanel.MouseFilter = Control.MouseFilterEnum.Stop;
        barPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        vbox.AddChild(barPanel);

        var styleBox = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.12f, 0.12f, 0.95f),
            CornerRadiusTopLeft = 0,
            CornerRadiusTopRight = 0,
            CornerRadiusBottomLeft = 0,
            CornerRadiusBottomRight = 0,
            BorderWidthTop = 1,
            BorderColor = new Color(0.3f, 0.3f, 0.3f, 0.5f),
            ShadowColor = new Color(0, 0, 0, 0.25f),
            ShadowSize = 4,
            ContentMarginLeft = 4,
            ContentMarginRight = 4,
            ContentMarginTop = 2,
            ContentMarginBottom = 2,
        };
        barPanel.AddThemeStyleboxOverride("panel", styleBox);

        var split = new HSplitContainer();
        barPanel.AddChild(split);

        _chatContainer = new MarginContainer();
        _chatContainer.CustomMinimumSize = new Vector2(100, 0);
        _chatContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _chatContainer.SizeFlagsStretchRatio = 0.25f; // ~20% width
        split.AddChild(_chatContainer);

        var rightBox = new HBoxContainer();
        rightBox.Alignment = BoxContainer.AlignmentMode.End;
        rightBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        rightBox.SizeFlagsStretchRatio = 0.75f; // ~80% width
        split.AddChild(rightBox);

        _hbox = new HBoxContainer();
        _hbox.AddThemeConstantOverride("separation", 2);
        rightBox.AddChild(_hbox);
    }

    private MarginContainer _chatContainer = null!;

    public void AttachChatBox(Control chatBox)
    {
        chatBox.GetParent()?.RemoveChild(chatBox);
        _chatContainer.AddChild(chatBox);
        chatBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        chatBox.SizeFlagsVertical = Control.SizeFlags.Fill;
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
        var stale = new List<Node>();
        foreach (Node child in _hbox.GetChildren()) stale.Add(child);
        foreach (var child in stale)
        {
            _hbox.RemoveChild(child); // detach synchronously so the loop below sees a clean container
            child.QueueFree();
        }

        foreach (var id in _settings.Order)
        {
            if (!_settings.IsEnabled(id)) continue;
            var def = _items.FirstOrDefault(i => i.Id == id);
            if (def == null) continue; // id from a build that no longer registers it -- ignore
            _hbox.AddChild(BuildButton(def));
        }
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
                TryReorder(btn, mm.GlobalPosition.X);
            }
        }
    }

    private void TryReorder(Button dragged, float mouseGlobalX)
    {
        int idx = dragged.GetIndex();
        if (idx < _hbox.GetChildCount() - 1 && _hbox.GetChild(idx + 1) is Control right)
        {
            float rightMid = right.GlobalPosition.X + right.Size.X / 2f;
            if (mouseGlobalX > rightMid)
            {
                _hbox.MoveChild(dragged, idx + 1);
                return;
            }
        }
        if (idx > 0 && _hbox.GetChild(idx - 1) is Control left)
        {
            float leftMid = left.GlobalPosition.X + left.Size.X / 2f;
            if (mouseGlobalX < leftMid)
            {
                _hbox.MoveChild(dragged, idx - 1);
            }
        }
    }

    private void EndDrag(Button btn)
    {
        var newEnabledOrder = new List<string>();
        foreach (Node child in _hbox.GetChildren())
            if (child.HasMeta(MetaKey))
                newEnabledOrder.Add((string)child.GetMeta(MetaKey));

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

        // Sync the pressed look of every button with the target panel visibility -- the panel
        // can also be closed via its own SLNGWindow close button, not only from here.
        foreach (Node child in _hbox.GetChildren())
        {
            if (child is not Button btn || !btn.HasMeta(MetaKey)) continue;
            var id = (string)btn.GetMeta(MetaKey);
            var def = _items.FirstOrDefault(i => i.Id == id);
            if (def?.IsActive != null) btn.ButtonPressed = def.IsActive();
        }
    }
}
