using Godot;
using SLNG.Net;
using System;
using System.Collections.Generic;

namespace SLNG.App.UI;

/// <summary>
/// Read-only inventory browser (v1), toggled with Ctrl+I (see Boot._Input). Lazily fetches ONE
/// folder per expansion via <see cref="GridSession.FetchInventoryChildrenAsync"/> — never
/// recurses the tree: an inventory can hold tens of thousands of items, and per-folder CAPS
/// fetches on expand are how real viewers populate the tree too. Fetches complete on worker
/// threads; every Tree mutation is marshalled back to the main thread (CallDeferred), per the
/// project threading rule. Wearing/attaching/moving items is not implemented yet — browsing only.
/// </summary>
public partial class InventoryPanel : SLNGWindow
{
    private GridSession? _session;
    private Tree _tree = null!;
    private Label _status = null!;
    // Folders already fetched (or currently fetching) — the expand signal fires on every
    // re-expand, and a re-fetch would duplicate the subtree under the item.
    private readonly HashSet<Guid> _loadedFolders = new();
    // Every folder row currently in the tree, keyed by folder id -- lets an external caller
    // (e.g. CreateLandmarkWindow after saving) refresh a specific already-expanded folder
    // without needing to walk the Tree itself. Cleared alongside the tree in PopulateRoots.
    private readonly Dictionary<Guid, TreeItem> _folderItems = new();
    private bool _rootsPopulated;
    private PopupMenu _contextMenu = null!;
    private LineEdit _searchBox = null!;

    public override void _Ready()
    {
        base._Ready(); // Setup SLNGWindow styling

        Title = "INVENTORY";
        Visible = false;
        
        CustomMinimumSize = new Vector2(360, 500);
        Size = new Vector2(360, 500);
        
        // Try to position it on the right side
        // GetViewportRect().Size is not ready in _Ready usually if not in tree, but we can set a decent default position
        Position = new Vector2(800, 100); 

        OnCloseRequested = Hide;

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 0);
        ContentContainer.AddChild(vbox);

        var searchContainer = new MarginContainer();
        searchContainer.AddThemeConstantOverride("margin_left", 12);
        searchContainer.AddThemeConstantOverride("margin_right", 12);
        searchContainer.AddThemeConstantOverride("margin_top", 12);
        searchContainer.AddThemeConstantOverride("margin_bottom", 12);
        
        _searchBox = new LineEdit
        {
            PlaceholderText = "Suchen / Filtern...",
            ClearButtonEnabled = true
        };
        var searchStyle = new StyleBoxFlat
        {
            BgColor = new Color(1, 1, 1, 0.05f),
            CornerRadiusTopLeft = 16,
            CornerRadiusTopRight = 16,
            CornerRadiusBottomLeft = 16,
            CornerRadiusBottomRight = 16,
            BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = new Color(1, 1, 1, 0.1f),
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        _searchBox.AddThemeStyleboxOverride("normal", searchStyle);
        _searchBox.AddThemeStyleboxOverride("focus", searchStyle);
        _searchBox.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f));
        _searchBox.AddThemeColorOverride("font_placeholder_color", new Color(0.5f, 0.5f, 0.5f));
        _searchBox.TextChanged += OnSearchTextChanged;
        
        searchContainer.AddChild(_searchBox);
        vbox.AddChild(searchContainer);

        _status = new Label();
        var statusMargin = new MarginContainer();
        statusMargin.AddThemeConstantOverride("margin_left", 12);
        statusMargin.AddChild(_status);
        vbox.AddChild(statusMargin);

        _contextMenu = new PopupMenu();
        _contextMenu.AddItem("Wear", 0);
        _contextMenu.AddItem("Copy", 1);
        _contextMenu.AddItem("Edit", 2);
        _contextMenu.AddItem("Export (Full Perm)", 3);
        _contextMenu.AddItem("Delete", 4);
        _contextMenu.AddItem("Teleport", 5);
        _contextMenu.IdPressed += OnContextMenuIdPressed;
        
        _tree = new Tree 
        { 
            SizeFlagsVertical = SizeFlags.ExpandFill, 
            HideRoot = true, 
            FocusMode = FocusModeEnum.None,
            AllowRmbSelect = true 
        };
        _tree.AddChild(_contextMenu);
        
        _tree.ItemCollapsed += OnItemCollapsed;
        _tree.GuiInput += OnTreeGuiInput;
        vbox.AddChild(_tree);
    }

    public void Initialize(GridSession session) => _session = session;

    public void Toggle()
    {
        Visible = !Visible;
        if (Visible) PopulateRoots();
    }

    private void PopulateRoots()
    {
        if (_rootsPopulated) return;
        if (_session?.InventoryRootId is not { } rootId)
        {
            _status.Text = "Not connected yet.";
            return;
        }
        _rootsPopulated = true;
        _status.Text = "";

        _tree.Clear();
        _folderItems.Clear();
        var hidden = _tree.CreateItem();

        var myInv = AddFolderItem(hidden, rootId, "My Inventory");
        LoadFolder(myInv, rootId);
        myInv.Collapsed = false;

        if (_session.LibraryRootId is { } libraryId)
            AddFolderItem(hidden, libraryId, "Library");
    }

    private void OnSearchTextChanged(string newText)
    {
        var root = _tree.GetRoot();
        if (root == null) return;
        FilterTree(root, newText.ToLowerInvariant());
    }

    private bool FilterTree(TreeItem item, string query)
    {
        bool anyChildVisible = false;
        var children = item.GetChildren();
        foreach (var child in children)
        {
            bool childVisible = FilterTree(child, query);
            anyChildVisible |= childVisible;
        }

        bool match = string.IsNullOrEmpty(query) || item.GetText(0).ToLowerInvariant().Contains(query);
        bool isVisible = match || anyChildVisible;
        item.Visible = isVisible;
        
        if (anyChildVisible && !string.IsNullOrEmpty(query))
            item.Collapsed = false;

        return isVisible;
    }

    /// <summary>Adds a folder row with a "…" placeholder child, so the expander arrow shows
    /// before the folder's real contents have ever been fetched.</summary>
    private TreeItem AddFolderItem(TreeItem parent, Guid folderId, string name)
    {
        var item = _tree.CreateItem(parent);
        item.SetText(0, name);
        item.SetMetadata(0, folderId.ToString());
        item.Collapsed = true;
        _folderItems[folderId] = item;
        var placeholder = _tree.CreateItem(item);
        placeholder.SetText(0, "…");
        return item;
    }

    /// <summary>Re-fetches a folder's contents if it's currently present (and already loaded) in
    /// the tree -- a no-op otherwise (the folder isn't open, so there's nothing stale to show;
    /// the next manual expand fetches fresh anyway). Used to reflect an item created elsewhere
    /// (e.g. Create Landmark) without requiring the user to collapse/re-expand by hand.
    ///
    /// <paramref name="knownItemId"/>/<paramref name="knownAssetId"/> patch that one row's asset
    /// id with a value already known to be correct (e.g. straight from a create call's response),
    /// overriding whatever the fresh fetch reports for it: the server's own folder-contents
    /// listing can briefly report a just-created item's asset id as empty (an indexing lag), and
    /// InventoryPanel's context menu refuses to enable "Teleport" on an empty asset id (that's
    /// not a UI nicety -- LandmarkID==null/zero in the SL teleport wire protocol means "teleport
    /// home", so treating an unresolved id as "no landmark" is the only safe default).</summary>
    public void RefreshFolder(Guid folderId, Guid? knownItemId = null, Guid? knownAssetId = null)
    {
        if (!_folderItems.TryGetValue(folderId, out var item) || !IsInstanceValid(item)) return;
        if (!_loadedFolders.Contains(folderId)) return; // never expanded -- nothing to refresh
        LoadFolder(item, folderId, force: true, knownItemId, knownAssetId);
    }

    private void OnItemCollapsed(TreeItem item)
    {
        if (item.Collapsed) return; // fires for both directions; only expansion loads
        var metaStr = item.GetMetadata(0).AsString();
        var idStr = metaStr.Contains(',') ? metaStr.Split(',')[0] : metaStr;
        if (!Guid.TryParse(idStr, out var folderId)) return;
        LoadFolder(item, folderId);
    }

    private void OnTreeGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right)
        {
            var item = _tree.GetItemAtPosition(mb.Position);
            if (item != null)
            {
                var metaStr = item.GetMetadata(0).AsString();
                var parts = metaStr.Split(',');
                if (parts.Length == 6)
                {
                    bool canCopy = bool.Parse(parts[1]);
                    bool canModify = bool.Parse(parts[2]);
                    bool canTransfer = bool.Parse(parts[3]);
                    int assetType = int.Parse(parts[4]);
                    // Links (e.g. Current Outfit entries) carry AssetId == Guid.Empty -- their
                    // target item, not this row, holds the real landmark asset -- so exclude
                    // them even if AssetType happens to read as Landmark.
                    bool isLandmark = assetType == SLNG.Core.AssetTypeIds.Landmark
                        && Guid.TryParse(parts[5], out var landmarkAssetId) && landmarkAssetId != Guid.Empty;

                    _contextMenu.SetItemDisabled(0, false); // Wear
                    _contextMenu.SetItemDisabled(1, !canCopy); // Copy
                    _contextMenu.SetItemDisabled(2, !canModify); // Edit
                    _contextMenu.SetItemDisabled(3, !(canCopy && canModify && canTransfer)); // Export
                    _contextMenu.SetItemDisabled(4, false); // Delete
                    _contextMenu.SetItemDisabled(5, !isLandmark); // Teleport

                    _contextMenu.Position = (Vector2I)GetGlobalMousePosition();
                    _contextMenu.Popup();
                }
            }
        }
    }

    private void OnContextMenuIdPressed(long id)
    {
        var item = _tree.GetSelected();
        if (item == null || _session == null) return;

        var metaStr = item.GetMetadata(0).AsString();
        var idStr = metaStr.Contains(',') ? metaStr.Split(',')[0] : metaStr;
        if (!Guid.TryParse(idStr, out var itemId)) return;

        bool isFolder = !metaStr.Contains(',');

        if (id == 4) // Delete
        {
            _ = _session.MoveToTrashAsync(itemId, isFolder);
            item.Free(); // Remove from UI immediately for responsiveness
        }
        else if (id == 1) // Copy
        {
            if (isFolder) return; // Currently only implementing copying of items
            
            var parentItem = item.GetParent();
            if (parentItem == null) return;
            
            var parentMetaStr = parentItem.GetMetadata(0).AsString();
            var parentIdStr = parentMetaStr.Contains(',') ? parentMetaStr.Split(',')[0] : parentMetaStr;
            if (!Guid.TryParse(parentIdStr, out var parentId)) return;
            
            _ = _session.CopyItemAsync(itemId, parentId, item.GetText(0));
            // Trigger a refresh of the parent folder to show the new item
            LoadFolder(parentItem, parentId, force: true); 
        }
        else if (id == 2) // Edit
        {
            if (isFolder) return;
            
            var win = new ItemPropertiesWindow();
            AddChild(win);
            win.Initialize(_session, itemId, () => {
                // On save, reload the folder to update name/permissions in the tree
                var parentItem = item.GetParent();
                if (parentItem == null) return;
                var parentMetaStr = parentItem.GetMetadata(0).AsString();
                var parentIdStr = parentMetaStr.Contains(',') ? parentMetaStr.Split(',')[0] : parentMetaStr;
                if (Guid.TryParse(parentIdStr, out var parentId))
                {
                    LoadFolder(parentItem, parentId, force: true);
                }
            });
            win.Show();
        }
        else if (id == 5) // Teleport (landmark items only -- context menu already disables this otherwise)
        {
            if (isFolder) return;

            var parts = metaStr.Split(',');
            if (parts.Length != 6 || !Guid.TryParse(parts[5], out var assetId)) return;

            _status.Text = "Teleporting…";
            _ = TeleportAsync(assetId);
        }
    }

    private async System.Threading.Tasks.Task TeleportAsync(Guid landmarkAssetId)
    {
        var result = await _session!.TeleportToLandmarkAsync(landmarkAssetId).ConfigureAwait(false);
        Callable.From(() =>
        {
            if (!IsInstanceValid(this)) return;
            _status.Text = result.Success
                ? string.Empty
                : $"Teleport failed{(string.IsNullOrEmpty(result.Message) ? "." : $": {result.Message}")}";
        }).CallDeferred();
    }

    private void LoadFolder(TreeItem item, Guid folderId, bool force = false, Guid? knownItemId = null, Guid? knownAssetId = null)
    {
        if (_session == null || (!_loadedFolders.Add(folderId) && !force)) return;
        _status.Text = "Loading…";
        _ = FetchAsync(item, folderId, knownItemId, knownAssetId);
    }

    private async System.Threading.Tasks.Task FetchAsync(TreeItem item, Guid folderId, Guid? knownItemId = null, Guid? knownAssetId = null)
    {
        try
        {
            var children = await _session!.FetchInventoryChildrenAsync(folderId).ConfigureAwait(false);
            Callable.From(() => Populate(item, children, knownItemId, knownAssetId)).CallDeferred();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Inventory] fetch {folderId} failed: {ex.Message}");
            Callable.From(() =>
            {
                _loadedFolders.Remove(folderId); // allow a retry on the next expand
                _status.Text = "Fetch failed — collapse and expand to retry.";
            }).CallDeferred();
        }
    }

    private void Populate(TreeItem item, IReadOnlyList<SLNG.Core.InventoryEntry> children, Guid? knownItemId = null, Guid? knownAssetId = null)
    {
        if (!IsInstanceValid(_tree) || !IsInstanceValid(this)) return;

        // Drop the "…" placeholder (and anything else stale under this folder).
        var child = item.GetFirstChild();
        while (child != null)
        {
            var next = child.GetNext();
            child.Free();
            child = next;
        }

        // Folders first, then items — each group keeps the server's by-name order.
        foreach (var entry in children)
            if (entry.IsFolder)
                AddFolderItem(item, entry.Id, entry.Name);
        foreach (var entry in children)
        {
            if (entry.IsFolder) continue;
            var row = _tree.CreateItem(item);
            // Links (Current Outfit etc.) point at another inventory item — mark them so an
            // apparently duplicated item is readable as the link it is.
            string text = entry.IsLink ? entry.Name + "  ⇢" : entry.Name;
            text += entry.GetPermissionSuffix();
            row.SetText(0, text);
            // See RefreshFolder's doc comment: a just-created item's own known-good asset id
            // (from the create response, not this fetch) wins over whatever this listing reports.
            var assetId = entry.Id == knownItemId && knownAssetId is { } known ? known : entry.AssetId;
            row.SetMetadata(0, $"{entry.Id},{entry.CanCopy},{entry.CanModify},{entry.CanTransfer},{entry.AssetType},{assetId}");
        }

        if (children.Count == 0)
        {
            var empty = _tree.CreateItem(item);
            empty.SetText(0, "(empty)");
            empty.SetCustomColor(0, new Color(1, 1, 1, 0.4f));
        }

        _status.Text = "";
    }
}
