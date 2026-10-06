using System;
using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// Firestorm-parity horizontal favorites bar docked beneath the top menu bar (FEAT-UI-67).
/// Displays quick-teleport landmark buttons, handles drag-and-drop from inventory and landmarks window,
/// and persists favorites per resident.
/// </summary>
public partial class FavoritesBar : PanelContainer
{
    private string? _agentId;
    private LandmarkFavoritesList _list = new();

    private HBoxContainer _itemsHBox = null!;
    private ScrollContainer _scroll = null!;
    private Label _emptyLabel = null!;
    private Button _starBtn = null!;
    private Button _addBtn = null!;
    private Button _closeBtn = null!;
    private PopupMenu _itemContextMenu = null!;
    private PopupMenu _addMenu = null!;
    private PopupMenu _barContextMenu = null!;
    private LandmarkFavoriteItem? _contextItem;

    public Action<Guid, Guid, string>? OnTeleportRequested;
    public Action? OnOpenLandmarksWindow;
    public Action? OnAddCurrentLocation;
    public Action<string>? OnToast;

    public LandmarkFavoritesList FavoritesList => _list;

    public override void _Ready()
    {
        base._Ready();

        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.02f, 0.04f, 0.07f, 0.45f),
            BorderWidthBottom = 1,
            BorderColor = new Color(0.15f, 0.6f, 0.9f, 0.15f),
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 1,
            ContentMarginBottom = 1
        });

        var mainHBox = new HBoxContainer();
        mainHBox.AddThemeConstantOverride("separation", 4);
        AddChild(mainHBox);

        _starBtn = new Button
        {
            Text = "★",
            Flat = true,
            FocusMode = FocusModeEnum.None,
            TooltipText = L10n.Tr("ui.favorites_bar.open_landmarks_tooltip"),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        _starBtn.AddThemeFontSizeOverride("font_size", 11);
        _starBtn.AddThemeColorOverride("font_color", new Color(0.85f, 0.75f, 0.35f, 0.8f));
        _starBtn.AddThemeColorOverride("font_hover_color", new Color(1.0f, 0.95f, 0.6f, 1.0f));
        _starBtn.Pressed += () => OnOpenLandmarksWindow?.Invoke();
        mainHBox.AddChild(_starBtn);

        _scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(0, 20)
        };
        mainHBox.AddChild(_scroll);

        _itemsHBox = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        _itemsHBox.AddThemeConstantOverride("separation", 2);
        _scroll.AddChild(_itemsHBox);

        _emptyLabel = new Label
        {
            Text = L10n.Tr("ui.favorites_bar.empty_hint"),
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        _emptyLabel.AddThemeFontSizeOverride("font_size", 10);
        _emptyLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.6f, 0.7f, 0.5f));
        _itemsHBox.AddChild(_emptyLabel);

        _addBtn = new Button
        {
            Text = "+",
            Flat = true,
            FocusMode = FocusModeEnum.None,
            TooltipText = L10n.Tr("ui.favorites_bar.add_tooltip"),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        _addBtn.AddThemeFontSizeOverride("font_size", 11);
        _addBtn.AddThemeColorOverride("font_color", new Color(0.6f, 0.75f, 0.9f, 0.75f));
        _addBtn.AddThemeColorOverride("font_hover_color", new Color(1.0f, 1.0f, 1.0f, 1.0f));
        _addBtn.Pressed += OnAddBtnPressed;
        mainHBox.AddChild(_addBtn);

        _closeBtn = new Button
        {
            Text = "×",
            Flat = true,
            FocusMode = FocusModeEnum.None,
            TooltipText = L10n.Tr("ui.favorites_bar.hide"),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        _closeBtn.AddThemeFontSizeOverride("font_size", 11);
        _closeBtn.AddThemeColorOverride("font_color", new Color(0.55f, 0.65f, 0.75f, 0.65f));
        _closeBtn.AddThemeColorOverride("font_hover_color", new Color(1.0f, 0.45f, 0.45f, 1.0f));
        _closeBtn.Pressed += () => SetVisibleState(false);
        mainHBox.AddChild(_closeBtn);

        _itemContextMenu = new PopupMenu();
        _itemContextMenu.AddItem(L10n.Tr("ui.favorites_bar.teleport"), 0);
        _itemContextMenu.AddItem(L10n.Tr("ui.favorites_bar.copy_slurl"), 1);
        _itemContextMenu.AddSeparator();
        _itemContextMenu.AddItem(L10n.Tr("ui.favorites_bar.remove"), 2);
        _itemContextMenu.IdPressed += OnItemContextIdPressed;
        AddChild(_itemContextMenu);

        _addMenu = new PopupMenu();
        _addMenu.AddItem(L10n.Tr("ui.favorites_bar.add_current_location"), 0);
        _addMenu.AddItem(L10n.Tr("ui.favorites_bar.manage_landmarks"), 1);
        _addMenu.IdPressed += (id) =>
        {
            if (id == 0) OnAddCurrentLocation?.Invoke();
            else if (id == 1) OnOpenLandmarksWindow?.Invoke();
        };
        AddChild(_addMenu);

        _barContextMenu = new PopupMenu();
        _barContextMenu.AddItem(L10n.Tr("ui.favorites_bar.manage_landmarks"), 0);
        _barContextMenu.AddItem(L10n.Tr("ui.favorites_bar.hide"), 1);
        _barContextMenu.IdPressed += (id) =>
        {
            if (id == 0) OnOpenLandmarksWindow?.Invoke();
            else if (id == 1) SetVisibleState(false);
        };
        AddChild(_barContextMenu);

        GuiInput += (ev) =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right)
            {
                _barContextMenu.Position = (Vector2I)GetGlobalMousePosition();
                _barContextMenu.Popup();
                AcceptEvent();
            }
        };

        Initialize(null);
    }

    public void Initialize(string? agentId)
    {
        _agentId = agentId;
        _list = LandmarkFavoritesStore.Load(agentId);
        Visible = LandmarkFavoritesStore.LoadVisible(agentId);
        RebuildItems();
    }

    public void SetVisibleState(bool visible)
    {
        Visible = visible;
        LandmarkFavoritesStore.SaveVisible(_agentId, visible);
    }

    public bool AddFavorite(Guid itemId, Guid assetId, string name)
    {
        var item = new LandmarkFavoriteItem(itemId, assetId, name);
        if (_list.Add(item))
        {
            LandmarkFavoritesStore.Save(_agentId, _list);
            RebuildItems();
            return true;
        }
        return false;
    }

    public bool RemoveFavorite(Guid id)
    {
        if (_list.Remove(id))
        {
            LandmarkFavoritesStore.Save(_agentId, _list);
            RebuildItems();
            return true;
        }
        return false;
    }

    public void RebuildItems()
    {
        if (_itemsHBox == null) return;

        foreach (var child in _itemsHBox.GetChildren())
        {
            if (child != _emptyLabel)
                child.QueueFree();
        }

        bool hasItems = _list.Items.Count > 0;
        _emptyLabel.Visible = !hasItems;

        foreach (var item in _list.Items)
        {
            var btn = CreateFavoriteButton(item);
            _itemsHBox.AddChild(btn);
        }
    }

    private Button CreateFavoriteButton(LandmarkFavoriteItem item)
    {
        string labelText = item.Name.Length > 24 ? item.Name[..22] + "…" : item.Name;
        var btn = new Button
        {
            Text = $"📍 {labelText}",
            Flat = true,
            FocusMode = FocusModeEnum.None,
            TooltipText = L10n.TrFormat("ui.favorites_bar.teleport_to", item.Name),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        btn.AddThemeFontSizeOverride("font_size", 11);
        btn.AddThemeColorOverride("font_color", new Color(0.82f, 0.9f, 0.98f, 0.85f));
        btn.AddThemeColorOverride("font_hover_color", new Color(1.0f, 1.0f, 1.0f, 1.0f));
        btn.AddThemeColorOverride("font_pressed_color", new Color(0.4f, 0.8f, 1.0f, 1.0f));

        var captured = item;
        btn.Pressed += () => OnTeleportRequested?.Invoke(captured.AssetId, captured.ItemId, captured.Name);

        btn.GuiInput += (ev) =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right)
            {
                _contextItem = captured;
                _itemContextMenu.Position = (Vector2I)btn.GetGlobalMousePosition();
                _itemContextMenu.Popup();
                btn.AcceptEvent();
            }
        };

        return btn;
    }

    private void OnAddBtnPressed()
    {
        _addMenu.Position = (Vector2I)_addBtn.GetGlobalMousePosition();
        _addMenu.Popup();
    }

    private void OnItemContextIdPressed(long id)
    {
        if (_contextItem == null) return;

        if (id == 0) // Teleport
        {
            OnTeleportRequested?.Invoke(_contextItem.AssetId, _contextItem.ItemId, _contextItem.Name);
        }
        else if (id == 1) // Copy SLurl
        {
            string slurl = $"secondlife:///{Uri.EscapeDataString(_contextItem.Name)}";
            DisplayServer.ClipboardSet(slurl);
            OnToast?.Invoke(L10n.TrFormat("ui.topmenu.slurl_copied", slurl));
        }
        else if (id == 2) // Remove
        {
            string name = _contextItem.Name;
            Guid removeId = _contextItem.ItemId != Guid.Empty ? _contextItem.ItemId : _contextItem.AssetId;
            if (RemoveFavorite(removeId))
            {
                OnToast?.Invoke(L10n.TrFormat("ui.favorites_bar.removed_toast", name));
            }
        }
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        if (data.VariantType != Variant.Type.String) return false;
        string s = data.AsString();
        if (s.StartsWith("slng_landmark|")) return true;
        if (s.StartsWith("slng_item|"))
        {
            var parts = s.Split('|');
            if (parts.Length >= 6)
            {
                bool isFolder = bool.TryParse(parts[4], out var f) && f;
                int assetType = int.TryParse(parts[5], out var at) ? at : -1;
                return !isFolder && assetType == AssetTypeIds.Landmark;
            }
        }
        return false;
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        string s = data.AsString();
        if (s.StartsWith("slng_landmark|"))
        {
            // Format: slng_landmark|itemId|assetId|name
            var parts = s.Split('|');
            if (parts.Length >= 4)
            {
                Guid.TryParse(parts[1], out var itemId);
                Guid.TryParse(parts[2], out var assetId);
                string name = parts[3];
                if (AddFavorite(itemId, assetId, name))
                {
                    OnToast?.Invoke(L10n.TrFormat("ui.favorites_bar.added_toast", name));
                }
            }
        }
        else if (s.StartsWith("slng_item|"))
        {
            // Format: slng_item|itemId|name|canTransfer|isFolder|assetType|sourceFolder
            var parts = s.Split('|');
            if (parts.Length >= 6)
            {
                Guid.TryParse(parts[1], out var itemId);
                string name = parts[2];
                if (AddFavorite(itemId, Guid.Empty, name))
                {
                    OnToast?.Invoke(L10n.TrFormat("ui.favorites_bar.added_toast", name));
                }
            }
        }
    }
}
