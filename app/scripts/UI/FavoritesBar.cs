using System;
using System.Threading.Tasks;
using Godot;
using SLNG.Core;
using SLNG.Core.Landmarks;

namespace SLNG.App.UI;

/// <summary>
/// Firestorm-parity horizontal favorites bar docked beneath the top menu bar (FEAT-UI-67).
/// Displays quick-teleport landmark buttons and handles drag-and-drop from inventory and landmarks window.
///
/// <para>Since FEAT-UI-72 phase 2 it is a VIEW of the inventory's Favorites folder, the same list the login screen and
/// other viewers use: it shows <see cref="FavoritesService.Items"/> and every add or remove goes through the service, which
/// copies into or trashes from that folder. SLNG's old local list is no longer read, and nothing was migrated. Only
/// whether the bar is shown is kept here, per resident.</para>
/// </summary>
public partial class FavoritesBar : PanelContainer
{
    private string? _gridSlug;
    private string? _agentId;
    private FavoritesService? _service;

    /// <summary>The bar of this client, for the drag card (<see cref="LandmarkDragPreview"/>) to ask what a drop would do.</summary>
    public static FavoritesBar? Current { get; private set; }

    private StyleBoxFlat _normalStyle = null!;
    private StyleBoxFlat _dropStyle = null!;
    private bool _dropHover;

    private HBoxContainer _itemsHBox = null!;
    private ScrollContainer _scroll = null!;
    private Label _emptyLabel = null!;
    private Button _starBtn = null!;
    private Button _addBtn = null!;
    private Button _closeBtn = null!;
    private PopupMenu _itemContextMenu = null!;
    private PopupMenu _addMenu = null!;
    private PopupMenu _barContextMenu = null!;
    private LandmarkInventoryItem? _contextItem;

    public Action<Guid, Guid, string>? OnTeleportRequested;
    public Action? OnOpenLandmarksWindow;
    public Action? OnAddCurrentLocation;
    public Action<string>? OnToast;

    public override void _Ready()
    {
        base._Ready();

        Current = this;
        TreeExited += () =>
        {
            if (Current == this) Current = null;
        };

        _normalStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.02f, 0.04f, 0.07f, 0.45f),
            BorderWidthBottom = 1,
            BorderColor = new Color(0.15f, 0.6f, 0.9f, 0.15f),
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 1,
            ContentMarginBottom = 1
        };
        // While a landmark is dragged over the bar: a lit outline, so the bar itself says "drop here".
        _dropStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.18f, 0.12f, 0.8f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            BorderColor = new Color(0.25f, 0.85f, 0.4f, 0.9f),
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 1,
            ContentMarginBottom = 1
        };
        AddThemeStyleboxOverride("panel", _normalStyle);

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

        Reset();
    }

    /// <summary>What a drop of a landmark on the bar would do: the text and colours the drag card shows.</summary>
    public readonly record struct DropStatus(string Text, Color TextColor, Color Border);

    /// <summary>The answer for the drag card. <paramref name="assetId"/> is the landmark's asset when the drag source knows it.</summary>
    public DropStatus DropStatusFor(Guid itemId, Guid assetId)
    {
        var good = (new Color(0.35f, 0.95f, 0.45f), new Color(0.25f, 0.85f, 0.4f, 0.9f));
        var warn = (new Color(1.0f, 0.75f, 0.25f), new Color(0.9f, 0.65f, 0.2f, 0.9f));
        var bad = (new Color(0.95f, 0.4f, 0.4f), new Color(0.85f, 0.3f, 0.3f, 0.9f));

        var mode = _service?.Mode ?? FavoritesMode.Loading;
        if (mode == FavoritesMode.NoFolder)
            return new DropStatus(L10n.Tr("ui.favorites_bar.drop_no_folder"), bad.Item1, bad.Item2);
        if (mode != FavoritesMode.Folder)
            return new DropStatus(L10n.Tr("ui.favorites_bar.drop_not_ready"), warn.Item1, warn.Item2);

        foreach (var favorite in _service!.Items)
        {
            if (favorite.Id == itemId || (assetId != Guid.Empty && favorite.AssetId == assetId))
                return new DropStatus(L10n.Tr("ui.favorites_bar.drop_already"), warn.Item1, warn.Item2);
        }
        return new DropStatus(L10n.Tr("ui.favorites_bar.drop_add"), good.Item1, good.Item2);
    }

    public override void _Process(double delta)
    {
        var viewport = GetViewport();
        bool want = Visible && viewport != null && viewport.GuiIsDragging()
            && _CanDropData(Vector2.Zero, viewport.GuiGetDragData())
            && GetGlobalRect().HasPoint(GetGlobalMousePosition());
        if (want == _dropHover) return;
        _dropHover = want;
        AddThemeStyleboxOverride("panel", want ? _dropStyle : _normalStyle);
    }

    public void Initialize(FavoritesService service, string? gridSlug, string? agentId)
    {
        DetachService();
        _service = service;
        _service.Changed += OnServiceChanged;
        _service.OfflineChanged += OnServiceChanged;
        _gridSlug = gridSlug;
        _agentId = agentId;
        Visible = !string.IsNullOrWhiteSpace(gridSlug) && !string.IsNullOrWhiteSpace(agentId)
            && LandmarkFavoritesStore.LoadVisible(gridSlug, agentId);
        RebuildItems();
    }

    public void Reset()
    {
        DetachService();
        _gridSlug = null;
        _agentId = null;
        Visible = false;
        RebuildItems();
    }

    private void DetachService()
    {
        if (_service != null && IsInstanceValid(_service))
        {
            _service.Changed -= OnServiceChanged;
            _service.OfflineChanged -= OnServiceChanged;
        }
        _service = null;
    }

    private void OnServiceChanged() => RebuildItems();

    public void SetVisibleState(bool visible)
    {
        Visible = visible;
        LandmarkFavoritesStore.SaveVisible(_gridSlug, _agentId, visible);
    }

    /// <summary>Makes a landmark a favorite and says what came of it. The copy is requested here; the button appears when the
    /// grid's update arrives.</summary>
    public async Task AddFavoriteAsync(Guid itemId, string name)
    {
        if (_service == null) return;
        var verdict = await _service.AddAsync(itemId);
        OnToast?.Invoke(verdict switch
        {
            FavoriteAddVerdict.Add => L10n.TrFormat("ui.favorites_bar.added_toast", name),
            FavoriteAddVerdict.NoFolder => L10n.Tr("ui.favorites_bar.no_folder"),
            FavoriteAddVerdict.NotCopyable => L10n.TrFormat("ui.favorites_bar.not_copyable", name),
            FavoriteAddVerdict.AlreadyInFolder or FavoriteAddVerdict.AssetAlreadyFavorite
                => L10n.TrFormat("ui.favorites_bar.already", name),
            _ => L10n.TrFormat("ui.favorites_bar.add_failed", name),
        });
    }

    public void RebuildItems()
    {
        if (_itemsHBox == null) return;

        foreach (var child in _itemsHBox.GetChildren())
        {
            if (child != _emptyLabel)
                child.QueueFree();
        }

        var mode = _service?.Mode ?? FavoritesMode.Loading;
        var items = _service?.Items ?? Array.Empty<LandmarkInventoryItem>();
        bool hasItems = mode == FavoritesMode.Folder && items.Count > 0;
        _emptyLabel.Visible = !hasItems;
        _emptyLabel.Text = L10n.Tr(mode switch
        {
            FavoritesMode.Loading => "ui.favorites_bar.loading",
            FavoritesMode.NoFolder => "ui.favorites_bar.no_folder",
            FavoritesMode.Unavailable => "ui.favorites_bar.unavailable",
            _ => "ui.favorites_bar.empty_hint",
        });
        if (_addBtn != null) _addBtn.Disabled = mode == FavoritesMode.NoFolder;

        if (!hasItems) return;
        foreach (var item in items)
        {
            var btn = CreateFavoriteButton(item);
            _itemsHBox.AddChild(btn);
        }
    }

    private Button CreateFavoriteButton(LandmarkInventoryItem item)
    {
        string labelText = item.Name.Length > 24 ? item.Name[..22] + "…" : item.Name;

        // FEAT-UI-77: a favorite whose region did not answer is shown as such, so it is not a surprise after the teleport. It
        // stays clickable: the region may be back, and the teleport says what happened.
        bool offline = _service?.IsOffline(item.AssetId) ?? false;
        var btn = new Button
        {
            Text = $"{(offline ? "⚠" : "📍")} {labelText}",
            Flat = true,
            FocusMode = FocusModeEnum.None,
            TooltipText = L10n.TrFormat(offline ? "ui.favorites_bar.offline_tooltip" : "ui.favorites_bar.teleport_to", item.Name),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        btn.AddThemeFontSizeOverride("font_size", 11);
        btn.AddThemeColorOverride("font_color", offline ? new Color(0.9f, 0.62f, 0.5f, 0.7f) : new Color(0.82f, 0.9f, 0.98f, 0.85f));
        btn.AddThemeColorOverride("font_hover_color", new Color(1.0f, 1.0f, 1.0f, 1.0f));
        btn.AddThemeColorOverride("font_pressed_color", new Color(0.4f, 0.8f, 1.0f, 1.0f));

        var captured = item;
        btn.Pressed += () => OnTeleportRequested?.Invoke(captured.AssetId, captured.Id, captured.Name);

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
            OnTeleportRequested?.Invoke(_contextItem.AssetId, _contextItem.Id, _contextItem.Name);
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
            if (_service != null)
            {
                _ = _service.RemoveAsync(_contextItem.Id);
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
                string name = parts[3];
                _ = AddFavoriteAsync(itemId, name);
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
                _ = AddFavoriteAsync(itemId, name);
            }
        }
    }
}
