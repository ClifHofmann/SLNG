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

    private const string MetaKey = "toolbar_item_id";

    public override void _Ready()
    {
        SetAnchorsPreset(Control.LayoutPreset.FullRect);
        MouseFilter = Control.MouseFilterEnum.Ignore; // avoid blocking world/camera clicks outside the pill

        var barPanel = new PanelContainer();
        barPanel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        barPanel.GrowHorizontal = Control.GrowDirection.Both;
        barPanel.GrowVertical = Control.GrowDirection.Begin;
        barPanel.OffsetBottom = -16; // float above the true screen edge, matching the SLNGWindow shadow margin feel
        barPanel.MouseFilter = Control.MouseFilterEnum.Stop;

        var styleBox = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.5f),
            CornerRadiusTopLeft = 20,
            CornerRadiusTopRight = 20,
            CornerRadiusBottomLeft = 20,
            CornerRadiusBottomRight = 20,
            BorderWidthBottom = 1,
            BorderWidthTop = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderColor = new Color(1, 1, 1, 0.1f),
            ShadowColor = new Color(0, 0, 0, 0.25f),
            ShadowSize = 12,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
        };
        barPanel.AddThemeStyleboxOverride("panel", styleBox);
        AddChild(barPanel);

        _hbox = new HBoxContainer();
        _hbox.AddThemeConstantOverride("separation", 6);
        barPanel.AddChild(_hbox);
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
            CustomMinimumSize = new Vector2(44, 44),
            TooltipText = def.Label,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        btn.SetMeta(MetaKey, def.Id);
        btn.AddThemeFontSizeOverride("font_size", 14);
        btn.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.85f));
        btn.AddThemeColorOverride("font_pressed_color", new Color(1f, 1f, 1f));

        var normalStyle = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.05f), CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12, CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12 };
        var hoverStyle = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.15f), CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12, CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12 };
        var pressedStyle = new StyleBoxFlat { BgColor = new Color(0.3f, 0.6f, 0.9f, 0.4f), CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12, CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12 };
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
                btn.Modulate = new Color(1, 1, 1, 0.6f);
            }
            else if (_draggingButton == btn)
            {
                EndDrag(btn);
            }
        }
        else if (@event is InputEventMouseMotion mm && _draggingButton == btn)
        {
            TryReorder(btn, mm.GlobalPosition.X);
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
        btn.Modulate = Colors.White;
        _draggingButton = null;

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
            EndDrag(_draggingButton);
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
