using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// Dedicated floating Landmarks window for browsing, filtering and teleporting to saved landmarks (FEAT-UI-67).
/// Inherits SLNGWindow, supports double-click teleportation, search filter, and adding to Favorites Bar.
/// </summary>
public partial class LandmarksWindow : SLNGWindow
{
    private GridSession? _session;
    private readonly List<InventoryEntry> _landmarks = new();

    private LineEdit _searchEdit = null!;
    private Button _refreshBtn = null!;
    private Button _createBtn = null!;
    private LandmarkTree _tree = null!;
    private Label _statusLabel = null!;
    private Button _addToFavoritesBtn = null!;
    private Button _teleportBtn = null!;
    private PopupMenu _contextMenu = null!;

    public Action<Guid, Guid, string>? OnTeleportRequested;
    public Action<Guid, Guid, string>? OnAddToFavoritesRequested;
    public Action? OnCreateLandmarkRequested;
    public Action<string>? OnToast;

    private sealed partial class LandmarkTree : Tree
    {
        public override Variant _GetDragData(Vector2 atPosition)
        {
            var item = GetItemAtPosition(atPosition) ?? GetSelected();
            if (item == null) return default;
            var meta = item.GetMetadata(0).AsString();
            if (string.IsNullOrEmpty(meta)) return default;

            var parts = meta.Split('|');
            if (parts.Length < 3) return default;

            string name = parts[2];
            var preview = new Label { Text = $"📍 {name}" };
            SetDragPreview(preview);
            return $"slng_landmark|{parts[0]}|{parts[1]}|{name}";
        }
    }

    public override void _Ready()
    {
        base._Ready();

        PersistId = "landmarks";
        Title = L10n.Tr("ui.landmarks.title");
        CustomMinimumSize = new Vector2(380, 480);
        Size = CustomMinimumSize;
        Visible = false;
        OnCloseRequested = () => Visible = false;

        var vbox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        vbox.AddThemeConstantOverride("separation", 6);
        ContentContainer.AddChild(vbox);

        var searchRow = new HBoxContainer();
        searchRow.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(searchRow);

        _searchEdit = new LineEdit
        {
            PlaceholderText = L10n.Tr("ui.landmarks.search_placeholder"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _searchEdit.TextChanged += _ => FilterList();
        searchRow.AddChild(_searchEdit);

        _refreshBtn = new Button
        {
            Text = "🔄",
            TooltipText = L10n.Tr("ui.landmarks.refresh"),
            FocusMode = FocusModeEnum.None
        };
        _refreshBtn.Pressed += () => _ = RefreshLandmarksAsync();
        searchRow.AddChild(_refreshBtn);

        _createBtn = new Button
        {
            Text = "📍+",
            TooltipText = L10n.Tr("ui.landmarks.create_here"),
            FocusMode = FocusModeEnum.None
        };
        _createBtn.Pressed += () => OnCreateLandmarkRequested?.Invoke();
        searchRow.AddChild(_createBtn);

        _tree = new LandmarkTree
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HideRoot = true,
            AllowRmbSelect = true,
            SelectMode = Tree.SelectModeEnum.Row
        };
        _tree.ItemActivated += OnItemActivated;
        _tree.ItemSelected += OnItemSelected;
        _tree.GuiInput += OnTreeGuiInput;
        vbox.AddChild(_tree);

        var bottomRow = new HBoxContainer();
        bottomRow.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(bottomRow);

        _statusLabel = new Label
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Text = "",
            VerticalAlignment = VerticalAlignment.Center
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 11);
        _statusLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.7f, 0.8f, 0.8f));
        bottomRow.AddChild(_statusLabel);

        _addToFavoritesBtn = new Button
        {
            Text = L10n.Tr("ui.landmarks.add_to_favorites_btn"),
            Disabled = true,
            FocusMode = FocusModeEnum.None
        };
        _addToFavoritesBtn.Pressed += OnAddToFavoritesPressed;
        bottomRow.AddChild(_addToFavoritesBtn);

        _teleportBtn = new Button
        {
            Text = L10n.Tr("ui.landmarks.teleport"),
            Disabled = true,
            FocusMode = FocusModeEnum.None
        };
        _teleportBtn.Pressed += OnTeleportPressed;
        bottomRow.AddChild(_teleportBtn);

        _contextMenu = new PopupMenu();
        _contextMenu.AddItem(L10n.Tr("ui.landmarks.teleport"), 0);
        _contextMenu.AddItem(L10n.Tr("ui.landmarks.add_to_favorites_btn"), 1);
        _contextMenu.AddItem(L10n.Tr("ui.landmarks.copy_slurl"), 2);
        _contextMenu.IdPressed += OnContextMenuIdPressed;
        AddChild(_contextMenu);

        CallDeferred(MethodName.ApplyFirstOpenDefaultIfNeeded);
    }

    private void ApplyFirstOpenDefaultIfNeeded()
    {
        if (Position != Vector2.Zero) return;
        Size = new Vector2(380, 480);
        Position = new Vector2(180, 80);
    }

    public void Initialize(GridSession? session)
    {
        _session = session;
        _landmarks.Clear();
        UpdateTree();
    }

    public void Toggle()
    {
        Visible = !Visible;
        if (Visible)
        {
            BringToFront();
            if (_landmarks.Count == 0 && _session != null)
            {
                _ = RefreshLandmarksAsync();
            }
        }
    }

    public async Task RefreshLandmarksAsync()
    {
        if (_session == null || !IsInstanceValid(this)) return;

        _statusLabel.Text = L10n.Tr("ui.landmarks.loading");
        try
        {
            var landmarks = await _session.GetLandmarksAsync().ConfigureAwait(false);
            Callable.From(() =>
            {
                if (!IsInstanceValid(this)) return;
                _landmarks.Clear();
                _landmarks.AddRange(landmarks);
                FilterList();
            }).CallDeferred();
        }
        catch (Exception ex)
        {
            Callable.From(() =>
            {
                if (!IsInstanceValid(this)) return;
                _statusLabel.Text = ex.Message;
            }).CallDeferred();
        }
    }

    private void FilterList()
    {
        if (_tree == null) return;
        UpdateTree();
    }

    private void UpdateTree()
    {
        _tree.Clear();
        var root = _tree.CreateItem();
        string filter = _searchEdit?.Text?.Trim() ?? "";

        var matching = string.IsNullOrEmpty(filter)
            ? _landmarks
            : _landmarks.Where(l => l.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var lm in matching)
        {
            var item = _tree.CreateItem(root);
            item.SetText(0, $"📍 {lm.Name}");
            // Metadata format: itemId|assetId|name
            item.SetMetadata(0, $"{lm.Id}|{lm.AssetId}|{lm.Name}");
        }

        if (_landmarks.Count == 0)
        {
            _statusLabel.Text = L10n.Tr("ui.landmarks.none_found");
        }
        else
        {
            _statusLabel.Text = L10n.TrFormat("ui.landmarks.count", matching.Count);
        }

        UpdateActionButtons();
    }

    private void OnItemSelected()
    {
        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        var selected = _tree.GetSelected();
        bool hasSelection = selected != null;
        _teleportBtn.Disabled = !hasSelection;
        _addToFavoritesBtn.Disabled = !hasSelection;
    }

    private (Guid ItemId, Guid AssetId, string Name)? GetSelectedLandmark()
    {
        var selected = _tree.GetSelected();
        if (selected == null) return null;
        var meta = selected.GetMetadata(0).AsString();
        if (string.IsNullOrEmpty(meta)) return null;

        var parts = meta.Split('|');
        if (parts.Length < 3) return null;

        if (Guid.TryParse(parts[0], out var itemId) && Guid.TryParse(parts[1], out var assetId))
        {
            return (itemId, assetId, parts[2]);
        }
        return null;
    }

    private void OnItemActivated()
    {
        OnTeleportPressed();
    }

    private void OnTeleportPressed()
    {
        if (GetSelectedLandmark() is { } lm)
        {
            OnTeleportRequested?.Invoke(lm.AssetId, lm.ItemId, lm.Name);
        }
    }

    private void OnAddToFavoritesPressed()
    {
        if (GetSelectedLandmark() is { } lm)
        {
            OnAddToFavoritesRequested?.Invoke(lm.ItemId, lm.AssetId, lm.Name);
        }
    }

    private void OnTreeGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right)
        {
            var item = _tree.GetItemAtPosition(mb.Position);
            if (item != null)
            {
                _tree.SetSelected(item, 0);
                UpdateActionButtons();
                _contextMenu.Position = (Vector2I)GetGlobalMousePosition();
                _contextMenu.Popup();
                _tree.AcceptEvent();
            }
        }
    }

    private void OnContextMenuIdPressed(long id)
    {
        if (GetSelectedLandmark() is not { } lm) return;

        if (id == 0) // Teleport
        {
            OnTeleportRequested?.Invoke(lm.AssetId, lm.ItemId, lm.Name);
        }
        else if (id == 1) // Add to favorites
        {
            OnAddToFavoritesRequested?.Invoke(lm.ItemId, lm.AssetId, lm.Name);
        }
        else if (id == 2) // Copy SLurl
        {
            string slurl = $"secondlife:///{Uri.EscapeDataString(lm.Name)}";
            DisplayServer.ClipboardSet(slurl);
            OnToast?.Invoke(L10n.TrFormat("ui.topmenu.slurl_copied", slurl));
        }
    }
}
