using System;
using System.Collections.Generic;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// Autocomplete popup for @ mentions in the chat input bar.
/// Lists matching avatars (nearby avatars, friends, or conversation participants).
/// </summary>
public partial class ChatMentionPicker : PanelContainer
{
    private readonly VBoxContainer _itemsContainer = new();
    private readonly List<string> _candidates = new();
    private readonly List<Button> _buttons = new();
    private int _selectedIndex = 0;

    public Action<string>? OnMentionSelected { get; set; }

    public bool IsActive => Visible && _candidates.Count > 0;

    public override void _Ready()
    {
        TopLevel = true;
        Visible = false;

        var styleBox = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.12f, 0.14f, 0.95f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            BorderWidthBottom = 1,
            BorderWidthTop = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderColor = new Color(0.3f, 0.3f, 0.35f, 0.8f),
            ShadowColor = new Color(0, 0, 0, 0.5f),
            ShadowSize = 6,
            ShadowOffset = new Vector2(0, 3)
        };
        AddThemeStyleboxOverride("panel", styleBox);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 4);
        margin.AddThemeConstantOverride("margin_right", 4);
        margin.AddThemeConstantOverride("margin_top", 4);
        margin.AddThemeConstantOverride("margin_bottom", 4);
        AddChild(margin);

        _itemsContainer.AddThemeConstantOverride("separation", 2);
        margin.AddChild(_itemsContainer);
    }

    public void ShowCandidates(Vector2 screenPosition, IEnumerable<string> names)
    {
        _candidates.Clear();
        _buttons.Clear();
        foreach (var child in _itemsContainer.GetChildren())
        {
            _itemsContainer.RemoveChild(child);
            child.QueueFree();
        }

        int count = 0;
        foreach (var name in names)
        {
            if (count >= 8) break; // Maximum 8 suggestions
            _candidates.Add(name);
            count++;
        }

        if (_candidates.Count == 0)
        {
            Visible = false;
            return;
        }

        _selectedIndex = 0;

        for (int i = 0; i < _candidates.Count; i++)
        {
            string cand = _candidates[i];
            int index = i;
            var btn = new Button
            {
                Text = $"@{cand}",
                Alignment = HorizontalAlignment.Left,
                Flat = true,
                FocusMode = FocusModeEnum.None,
            };
            btn.AddThemeFontSizeOverride("font_size", 12);
            btn.Pressed += () =>
            {
                OnMentionSelected?.Invoke(cand);
                HidePicker();
            };
            _buttons.Add(btn);
            _itemsContainer.AddChild(btn);
        }

        UpdateHighlight();

        Visible = true;
        ResetSize();
        var minSize = GetCombinedMinimumSize();
        GlobalPosition = new Vector2(screenPosition.X, Mathf.Max(SLNGWindow.TopInset, screenPosition.Y - minSize.Y - 4f));
    }

    public void HidePicker()
    {
        Visible = false;
        _candidates.Clear();
        _buttons.Clear();
    }

    public void SelectNext()
    {
        if (_candidates.Count == 0) return;
        _selectedIndex = (_selectedIndex + 1) % _candidates.Count;
        UpdateHighlight();
    }

    public void SelectPrevious()
    {
        if (_candidates.Count == 0) return;
        _selectedIndex = (_selectedIndex - 1 + _candidates.Count) % _candidates.Count;
        UpdateHighlight();
    }

    public string? GetSelectedMention()
    {
        if (_selectedIndex >= 0 && _selectedIndex < _candidates.Count)
        {
            return _candidates[_selectedIndex];
        }
        return null;
    }

    public bool ConfirmSelected()
    {
        var mention = GetSelectedMention();
        if (mention != null)
        {
            OnMentionSelected?.Invoke(mention);
            HidePicker();
            return true;
        }
        return false;
    }

    private void UpdateHighlight()
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            var btn = _buttons[i];
            if (i == _selectedIndex)
            {
                btn.AddThemeColorOverride("font_color", new Color(0.2f, 0.8f, 1f));
            }
            else
            {
                btn.RemoveThemeColorOverride("font_color");
            }
        }
    }
}
