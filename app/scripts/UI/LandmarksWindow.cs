using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SLNG.Core.Landmarks;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// Dedicated floating Landmarks window for browsing, filtering and teleporting to saved landmarks (FEAT-UI-67 / FEAT-UI-68).
/// Supports folder-tree categorization, flat list mode, duplicate cleaning dialog,
/// search filter, double-click teleportation, and adding to Favorites Bar.
/// </summary>
public partial class LandmarksWindow : SLNGWindow
{
    private GridSession? _session;
    private readonly List<LandmarkInventoryItem> _landmarks = new();
    private bool _folderViewMode = true;

    private LineEdit _searchEdit = null!;
    private Button _viewModeBtn = null!;
    private Button _cleanDuplicatesBtn = null!;
    private Button _refreshBtn = null!;
    private Button _createBtn = null!;
    private Button _newFolderBtn = null!;
    private LandmarkTree _tree = null!;
    private Label _statusLabel = null!;
    private Button _addToFavoritesBtn = null!;
    private Button _teleportBtn = null!;
    private PopupMenu _contextMenu = null!;

    public Action<Guid, Guid, string>? OnTeleportRequested;
    public Action<Guid, Guid, string>? OnAddToFavoritesRequested;
    public Action? OnCreateLandmarkRequested;
    public Action? OnOpenDedupRequested;
    public Action<string>? OnToast;

    private sealed partial class LandmarkTree : Tree
    {
        public GridSession? Session { get; set; }
        public Action<Guid, string, Guid, string>? OnDropLandmark;

        public override Variant _GetDragData(Vector2 atPosition)
        {
            var item = GetItemAtPosition(atPosition) ?? GetSelected();
            if (item == null) return default;
            var meta = item.GetMetadata(0).AsString();
            if (string.IsNullOrEmpty(meta) || meta.StartsWith("folder|")) return default;

            var parts = meta.Split('|');
            if (parts.Length < 3) return default;

            string name = parts[2];
            string parentId = parts.Length > 3 ? parts[3] : "";
            string folderPath = parts.Length > 4 ? parts[4] : "";

            var preview = new Label { Text = $"📍 {name}" };
            SetDragPreview(preview);
            return $"slng_landmark|{parts[0]}|{parts[1]}|{name}|{parentId}|{folderPath}";
        }

        public override bool _CanDropData(Vector2 atPosition, Variant data)
        {
            return TryReadDrop(atPosition, data, out _, out _, out _, out _);
        }

        public override void _DropData(Vector2 atPosition, Variant data)
        {
            if (TryReadDrop(atPosition, data, out var itemId, out var name, out var targetFolderId, out var targetPath))
            {
                OnDropLandmark?.Invoke(itemId, name, targetFolderId, targetPath);
            }
        }

        private bool TryReadDrop(Vector2 atPosition, Variant data, out Guid itemId, out string name, out Guid targetFolderId, out string targetPath)
        {
            itemId = Guid.Empty;
            name = "";
            targetFolderId = Guid.Empty;
            targetPath = "";

            if (data.VariantType != Variant.Type.String) return false;
            string payload = data.AsString();

            Guid sourceParentId = Guid.Empty;

            if (payload.StartsWith("slng_landmark|"))
            {
                var parts = payload.Split('|');
                if (parts.Length < 4) return false;
                if (!Guid.TryParse(parts[1], out itemId)) return false;
                name = parts[3];
                if (parts.Length >= 5 && Guid.TryParse(parts[4], out var spId))
                {
                    sourceParentId = spId;
                }
            }
            else if (payload.StartsWith("slng_item|"))
            {
                var parts = payload.Split('|');
                if (parts.Length < 6) return false;
                if (!Guid.TryParse(parts[1], out itemId)) return false;
                name = parts[2];
                if (bool.TryParse(parts[4], out bool isFolder) && isFolder) return false;
                if (int.TryParse(parts[5], out int assetType) && assetType != 3) return false;
                if (parts.Length >= 7 && Guid.TryParse(parts[6], out var spId))
                {
                    sourceParentId = spId;
                }
            }
            else
            {
                return false;
            }

            var hoverItem = GetItemAtPosition(atPosition);
            if (hoverItem == null)
            {
                // Blank space in tree drops to Landmarks root
                if (Session?.LandmarksFolderId is { } lmRootId && lmRootId != Guid.Empty)
                {
                    targetFolderId = lmRootId;
                    targetPath = "Landmarks";
                }
                else
                {
                    return false;
                }
            }
            else
            {
                var meta = hoverItem.GetMetadata(0).AsString();
                if (string.IsNullOrEmpty(meta)) return false;

                if (meta.StartsWith("folder|"))
                {
                    var fParts = meta.Split('|');
                    if (fParts.Length < 3) return false;
                    if (!Guid.TryParse(fParts[1], out targetFolderId) || targetFolderId == Guid.Empty) return false;
                    targetPath = fParts[2];
                }
                else
                {
                    var itemParts = meta.Split('|');
                    if (itemParts.Length < 5) return false;
                    if (!Guid.TryParse(itemParts[3], out targetFolderId) || targetFolderId == Guid.Empty) return false;
                    targetPath = itemParts[4];
                }
            }

            if (sourceParentId != Guid.Empty && targetFolderId == sourceParentId)
            {
                return false;
            }

            return true;
        }
    }

    public override void _Ready()
    {
        base._Ready();

        PersistId = "landmarks";
        Title = L10n.Tr("ui.landmarks.title");
        CustomMinimumSize = new Vector2(400, 500);
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

        _viewModeBtn = new Button
        {
            Text = _folderViewMode ? "📂" : "📋",
            TooltipText = L10n.Tr("ui.landmarks.toggle_view_mode_tooltip"),
            FocusMode = FocusModeEnum.None
        };
        _viewModeBtn.Pressed += OnToggleViewMode;
        searchRow.AddChild(_viewModeBtn);

        _cleanDuplicatesBtn = new Button
        {
            Text = "🧹",
            TooltipText = L10n.Tr("ui.landmarks.clean_duplicates_btn"),
            FocusMode = FocusModeEnum.None
        };
        _cleanDuplicatesBtn.Pressed += () => OnOpenDedupRequested?.Invoke();
        searchRow.AddChild(_cleanDuplicatesBtn);

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

        _newFolderBtn = new Button
        {
            Text = "📁+",
            TooltipText = L10n.Tr("ui.landmarks.new_folder_tooltip"),
            FocusMode = FocusModeEnum.None
        };
        _newFolderBtn.Pressed += OnNewFolderPressed;
        searchRow.AddChild(_newFolderBtn);

        _tree = new LandmarkTree
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HideRoot = true,
            AllowRmbSelect = true,
            SelectMode = Tree.SelectModeEnum.Row,
            DropModeFlags = (int)Tree.DropModeFlagsEnum.OnItem
        };
        _tree.Session = _session;
        _tree.OnDropLandmark = (itemId, name, targetFolderId, targetPath) =>
        {
            _ = MoveLandmarkToFolderAsync(itemId, name, targetFolderId, targetPath);
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
        _contextMenu.AddSeparator();
        _contextMenu.AddItem(L10n.Tr("ui.landmarks.move_to_folder_action"), 4);
        _contextMenu.AddItem(L10n.Tr("ui.landmarks.delete_to_trash"), 3);
        _contextMenu.IdPressed += OnContextMenuIdPressed;
        AddChild(_contextMenu);

        CallDeferred(MethodName.ApplyFirstOpenDefaultIfNeeded);
    }

    private void ApplyFirstOpenDefaultIfNeeded()
    {
        if (Position != Vector2.Zero) return;
        Size = new Vector2(400, 500);
        Position = new Vector2(180, Mathf.Max(TopInset + 20f, 80f));
    }

    private void OnToggleViewMode()
    {
        _folderViewMode = !_folderViewMode;
        _viewModeBtn.Text = _folderViewMode ? "📂" : "📋";
        UpdateTree();
    }

    public void Initialize(GridSession? session)
    {
        _session = session;
        if (_tree != null) _tree.Session = session;
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
            var landmarks = await _session.GetLandmarksWithFoldersAsync().ConfigureAwait(false);
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
            : _landmarks.Where(l =>
                l.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                l.FolderPath.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (_folderViewMode)
        {
            var knownFolders = _session?.GetLandmarkFolders() ?? Array.Empty<(Guid Id, string Path, string Name)>();
            var folderIdMap = knownFolders.ToDictionary(f => f.Path, f => f.Id, StringComparer.OrdinalIgnoreCase);

            var landmarksRoot = new FolderNode(L10n.Tr("ui.landmarks.folder_landmarks"), "Landmarks")
            {
                FolderId = _session?.LandmarksFolderId ?? Guid.Empty
            };
            var otherRoot = new FolderNode(L10n.Tr("ui.landmarks.other_folders"), "");

            // 1. Populate all known landmark folders from inventory into landmarksRoot
            foreach (var kf in knownFolders)
            {
                if (kf.Id == landmarksRoot.FolderId) continue;
                string subPath = kf.Path.StartsWith("Landmarks/", StringComparison.OrdinalIgnoreCase)
                    ? kf.Path.Substring("Landmarks/".Length)
                    : kf.Path;

                var segments = subPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var curr = landmarksRoot;
                string currPath = "Landmarks";
                for (int i = 0; i < segments.Length; i++)
                {
                    currPath += "/" + segments[i];
                    if (!curr.Subfolders.TryGetValue(segments[i], out var childNode))
                    {
                        var fId = (i == segments.Length - 1) ? kf.Id : (folderIdMap.TryGetValue(currPath, out var id) ? id : Guid.Empty);
                        childNode = new FolderNode(segments[i], currPath) { FolderId = fId };
                        curr.Subfolders[segments[i]] = childNode;
                    }
                    curr = childNode;
                }
            }

            foreach (var lm in matching)
            {
                string path = lm.FolderPath?.Trim() ?? "";
                bool isLandmarksHierarchy = string.IsNullOrEmpty(path) ||
                    string.Equals(path, "Landmarks", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("Landmarks/", StringComparison.OrdinalIgnoreCase);

                if (isLandmarksHierarchy)
                {
                    if (string.IsNullOrEmpty(path) || string.Equals(path, "Landmarks", StringComparison.OrdinalIgnoreCase))
                    {
                        landmarksRoot.Landmarks.Add(lm);
                    }
                    else
                    {
                        string subPath = path.Substring("Landmarks/".Length);
                        var segments = subPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        var curr = landmarksRoot;
                        string currPath = "Landmarks";
                        foreach (var seg in segments)
                        {
                            currPath += "/" + seg;
                            if (!curr.Subfolders.TryGetValue(seg, out var childNode))
                            {
                                var fId = folderIdMap.TryGetValue(currPath, out var id) ? id : Guid.Empty;
                                childNode = new FolderNode(seg, currPath) { FolderId = fId };
                                curr.Subfolders[seg] = childNode;
                            }
                            curr = childNode;
                        }
                        if (curr.FolderId == Guid.Empty) curr.FolderId = lm.ParentId;
                        curr.Landmarks.Add(lm);
                    }
                }
                else
                {
                    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var curr = otherRoot;
                    string currPath = "";
                    foreach (var seg in segments)
                    {
                        currPath = string.IsNullOrEmpty(currPath) ? seg : currPath + "/" + seg;
                        if (!curr.Subfolders.TryGetValue(seg, out var childNode))
                        {
                            childNode = new FolderNode(seg, currPath);
                            curr.Subfolders[seg] = childNode;
                        }
                        curr = childNode;
                    }
                    if (curr.FolderId == Guid.Empty) curr.FolderId = lm.ParentId;
                    curr.Landmarks.Add(lm);
                }
            }

            // Render main Landmarks root node
            if (landmarksRoot.TotalLandmarksCount > 0 || landmarksRoot.Subfolders.Count > 0 || string.IsNullOrEmpty(filter))
            {
                var lmRootItem = _tree.CreateItem(root);
                lmRootItem.SetText(0, $"📁 {L10n.Tr("ui.landmarks.folder_landmarks")} ({landmarksRoot.TotalLandmarksCount})");
                lmRootItem.SetSelectable(0, true);
                lmRootItem.SetMetadata(0, $"folder|{landmarksRoot.FolderId}|Landmarks");
                lmRootItem.Collapsed = false; // Expanded by default so categories are immediately visible
                lmRootItem.SetCustomColor(0, new Color(0.85f, 0.93f, 1.0f, 0.95f));

                // 1. "Allgemeine Landmarken" (Direct root-level landmarks in Landmarks folder)
                // Clear, distinct visual separation from categorized subfolders
                if (landmarksRoot.Landmarks.Count > 0 || string.IsNullOrEmpty(filter))
                {
                    var genItem = _tree.CreateItem(lmRootItem);
                    genItem.SetText(0, $"📂 {L10n.Tr("ui.landmarks.general_landmarks")} ({landmarksRoot.Landmarks.Count})");
                    genItem.SetSelectable(0, true);
                    genItem.SetMetadata(0, $"folder|{landmarksRoot.FolderId}|Landmarks");
                    genItem.SetCustomColor(0, new Color(1.0f, 0.85f, 0.55f, 0.95f));
                    genItem.Collapsed = string.IsNullOrEmpty(filter);

                    foreach (var lm in landmarksRoot.Landmarks.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase))
                    {
                        var itemNode = _tree.CreateItem(genItem);
                        itemNode.SetText(0, $"📍 {lm.Name}");
                        itemNode.SetMetadata(0, $"{lm.Id}|{lm.AssetId}|{lm.Name}|{lm.ParentId}|{lm.FolderPath}");
                    }
                }

                // 2. Subfolders inside Landmarks in alphabetical tree order
                foreach (var sub in landmarksRoot.Subfolders.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    RenderSubfolderTree(lmRootItem, sub, filter);
                }
            }

            // Render other folders outside Landmarks (e.g. Received Items, Objects)
            if (otherRoot.TotalLandmarksCount > 0)
            {
                var otherRootItem = _tree.CreateItem(root);
                otherRootItem.SetText(0, $"📁 {L10n.Tr("ui.landmarks.other_folders")} ({otherRoot.TotalLandmarksCount})");
                otherRootItem.SetSelectable(0, false);
                otherRootItem.SetCustomColor(0, new Color(0.8f, 0.8f, 0.85f, 0.9f));
                otherRootItem.Collapsed = string.IsNullOrEmpty(filter);

                foreach (var sub in otherRoot.Subfolders.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    RenderSubfolderTree(otherRootItem, sub, filter);
                }
            }
        }
        else
        {
            foreach (var lm in matching.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = _tree.CreateItem(root);
                item.SetText(0, $"📍 {lm.Name} ({lm.FolderName})");
                item.SetMetadata(0, $"{lm.Id}|{lm.AssetId}|{lm.Name}|{lm.ParentId}|{lm.FolderPath}");
            }
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
        bool hasLandmarkSelection = GetSelectedLandmark() != null;
        _teleportBtn.Disabled = !hasLandmarkSelection;
        _addToFavoritesBtn.Disabled = !hasLandmarkSelection;
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

                string meta = item.GetMetadata(0).AsString();
                bool isFolder = meta.StartsWith("folder|");
                var lm = GetSelectedLandmark();

                if (isFolder || lm != null)
                {
                    _contextMenu.Clear();
                    if (lm != null)
                    {
                        _contextMenu.AddItem(L10n.Tr("ui.landmarks.teleport"), 0);
                        _contextMenu.AddItem(L10n.Tr("ui.landmarks.add_to_favorites_btn"), 1);
                        _contextMenu.AddItem(L10n.Tr("ui.landmarks.copy_slurl"), 2);
                        _contextMenu.AddSeparator();
                        _contextMenu.AddItem(L10n.Tr("ui.landmarks.move_to_folder_action"), 4);
                        _contextMenu.AddItem(L10n.Tr("ui.landmarks.new_folder_btn"), 10);
                        _contextMenu.AddSeparator();
                        _contextMenu.AddItem(L10n.Tr("ui.landmarks.delete_to_trash"), 3);
                    }
                    else
                    {
                        _contextMenu.AddItem(L10n.Tr("ui.landmarks.new_folder_btn"), 10);
                    }

                    _contextMenu.Position = (Vector2I)GetGlobalMousePosition();
                    _contextMenu.Popup();
                    _tree.AcceptEvent();
                }
            }
        }
    }

    private void OnContextMenuIdPressed(long id)
    {
        if (id == 10) // + Neuer Ordner / + New Folder
        {
            OnNewFolderPressed();
            return;
        }

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
        else if (id == 3) // Move to Trash
        {
            ConfirmDeleteSingleLandmark(lm.ItemId, lm.Name);
        }
        else if (id == 4) // Move landmark to folder...
        {
            var win = new MoveLandmarkWindow();
            var host = GetTree()?.Root?.GetNodeOrNull<CanvasLayer>("Boot/HudLayer")
                       ?? (Node?)GetParent() ?? this;
            host.AddChild(win);
            win.Initialize(
                _session,
                lm.ItemId,
                lm.Name,
                "",
                _session?.LandmarksFolderId,
                (targetFolderId, targetPath) =>
                {
                    OnToast?.Invoke(L10n.TrFormat("ui.landmarks.moved_to_folder_toast", targetPath));
                    _ = RefreshLandmarksAsync();
                });
        }
    }

    private (Guid FolderId, string FolderName)? GetSelectedFolder()
    {
        var selected = _tree.GetSelected();
        if (selected == null) return null;
        string meta = selected.GetMetadata(0).AsString();
        if (string.IsNullOrEmpty(meta)) return null;

        if (meta.StartsWith("folder|"))
        {
            var parts = meta.Split('|');
            if (parts.Length >= 3 && Guid.TryParse(parts[1], out var fid) && fid != Guid.Empty)
            {
                string folderName = parts[2].Contains('/') ? parts[2].Split('/').Last() : parts[2];
                return (fid, folderName);
            }
        }
        else
        {
            var parts = meta.Split('|');
            if (parts.Length >= 5 && Guid.TryParse(parts[3], out var fid) && fid != Guid.Empty)
            {
                string folderName = parts[4].Contains('/') ? parts[4].Split('/').Last() : parts[4];
                return (fid, folderName);
            }
        }

        return null;
    }

    private void OnNewFolderPressed()
    {
        if (_session == null) return;

        Guid targetParentId = _session.LandmarksFolderId ?? Guid.Empty;
        string parentFolderName = L10n.Tr("ui.landmarks.folder_landmarks");

        if (GetSelectedFolder() is { } sel)
        {
            targetParentId = sel.FolderId;
            parentFolderName = sel.FolderName;
        }

        if (targetParentId == Guid.Empty) return;

        var prompt = new TextPromptWindow();
        var host = GetTree()?.Root?.GetNodeOrNull<CanvasLayer>("Boot/HudLayer")
                   ?? (Node?)GetParent() ?? this;
        host.AddChild(prompt);
        prompt.Initialize(
            title: L10n.Tr("ui.landmarks.new_folder_title"),
            prompt: L10n.TrFormat("ui.landmarks.new_folder_prompt", parentFolderName),
            initialText: "",
            confirmLabel: L10n.Tr("ui.landmarks.create_folder_btn"));

        prompt.Confirmed += newFolderName =>
        {
            if (string.IsNullOrWhiteSpace(newFolderName)) return;
            _session.CreateInventoryFolder(targetParentId, newFolderName.Trim());
            OnToast?.Invoke(L10n.TrFormat("ui.landmarks.folder_created_toast", newFolderName.Trim()));
            _ = RefreshLandmarksAsync();
        };
    }

    private void ConfirmDeleteSingleLandmark(Guid itemId, string name)
    {
        if (_session == null) return;
        var confirm = new ConfirmWindow();
        var host = GetTree()?.Root?.GetNodeOrNull<CanvasLayer>("Boot/HudLayer")
                   ?? (Node?)GetParent() ?? this;
        host.AddChild(confirm);
        confirm.Initialize(
            title: L10n.Tr("ui.landmarks.delete_confirm_title"),
            question: L10n.TrFormat("ui.landmarks.delete_confirm_question", name),
            confirmLabel: L10n.Tr("ui.landmarks.delete_to_trash"),
            danger: true);

        confirm.Confirmed += () =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _session.MoveToTrashAsync(itemId, isFolder: false).ConfigureAwait(false);
                    Callable.From(() =>
                    {
                        OnToast?.Invoke(L10n.TrFormat("ui.landmarks.deleted_toast", name));
                        _ = RefreshLandmarksAsync();
                    }).CallDeferred();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[LandmarksWindow] MoveToTrash failed: {ex.Message}");
                }
            });
        };
    }

    private async Task MoveLandmarkToFolderAsync(Guid itemId, string itemName, Guid targetFolderId, string targetPath)
    {
        if (_session == null || itemId == Guid.Empty || targetFolderId == Guid.Empty) return;

        try
        {
            await _session.MoveInventoryAsync(itemId, targetFolderId, isFolder: false, targetPath).ConfigureAwait(false);
            Callable.From(() =>
            {
                OnToast?.Invoke(L10n.TrFormat("ui.landmarks.moved_to_folder_toast", targetPath));
                _ = RefreshLandmarksAsync();
            }).CallDeferred();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LandmarksWindow] MoveLandmarkToFolderAsync failed: {ex.Message}");
        }
    }

    private void RenderSubfolderTree(TreeItem parentItem, FolderNode node, string filter)
    {
        if (!string.IsNullOrEmpty(filter) && node.TotalLandmarksCount == 0 && !node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return;

        var folderItem = _tree.CreateItem(parentItem);
        folderItem.SetText(0, $"📁 {node.Name} ({node.TotalLandmarksCount})");
        folderItem.SetSelectable(0, true);
        folderItem.SetMetadata(0, $"folder|{node.FolderId}|{node.FullPath}");
        folderItem.SetCustomColor(0, new Color(0.85f, 0.92f, 1.0f, 0.95f));

        // Auto-expand if a filter is active
        folderItem.Collapsed = string.IsNullOrEmpty(filter);

        // 1. Subfolders first:
        foreach (var sub in node.Subfolders.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            RenderSubfolderTree(folderItem, sub, filter);
        }

        // 2. Direct landmarks in this subfolder:
        foreach (var lm in node.Landmarks.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var itemNode = _tree.CreateItem(folderItem);
            itemNode.SetText(0, $"📍 {lm.Name}");
            itemNode.SetMetadata(0, $"{lm.Id}|{lm.AssetId}|{lm.Name}|{lm.ParentId}|{lm.FolderPath}");
        }
    }

    private sealed class FolderNode
    {
        public string Name { get; }
        public string FullPath { get; }
        public Guid FolderId { get; set; } = Guid.Empty;
        public Dictionary<string, FolderNode> Subfolders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<LandmarkInventoryItem> Landmarks { get; } = new();

        public FolderNode(string name, string fullPath)
        {
            Name = name;
            FullPath = fullPath;
        }

        public int TotalLandmarksCount =>
            Landmarks.Count + Subfolders.Values.Sum(s => s.TotalLandmarksCount);
    }
}
