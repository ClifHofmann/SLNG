using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SLNG.Core.Landmarks;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// Dedicated dialog for reviewing and cleaning up landmark duplicates (FEAT-UI-68).
/// Detects exact asset and name duplicates, provides smart keep/delete recommendations,
/// and safely moves selected duplicates to the Trash folder via MoveToTrashAsync.
/// </summary>
public partial class LandmarkDedupWindow : SLNGWindow
{
    private GridSession? _session;
    private ISet<Guid>? _favoriteIds;
    private List<LandmarkInventoryItem> _rawLandmarks = new();
    private List<LandmarkDuplicateGroup> _groups = new();
    private readonly HashSet<Guid> _selectedToDelete = new();

    private Label _summaryLabel = null!;
    private Tree _tree = null!;
    private Button _selectAllBtn = null!;
    private Button _resetBtn = null!;
    private Button _cleanBtn = null!;
    private Button _refreshBtn = null!;
    private PopupMenu _contextMenu = null!;
    private LandmarkInventoryItem? _contextLandmark;
    private LandmarkDuplicateGroup? _contextGroup;

    public Action? OnDuplicatesRemoved;
    public Action<string>? OnToast;

    public override void _Ready()
    {
        base._Ready();

        PersistId = "landmark_dedup";
        Title = L10n.Tr("ui.landmarks.dedup_title");
        CustomMinimumSize = new Vector2(560, 480);
        Size = CustomMinimumSize;
        Visible = false;
        OnCloseRequested = () => Visible = false;

        var vbox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        vbox.AddThemeConstantOverride("separation", 8);
        ContentContainer.AddChild(vbox);

        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(topRow);

        _summaryLabel = new Label
        {
            Text = L10n.Tr("ui.landmarks.loading"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center
        };
        _summaryLabel.AddThemeFontSizeOverride("font_size", 12);
        topRow.AddChild(_summaryLabel);

        _refreshBtn = new Button
        {
            Text = "🔄",
            TooltipText = L10n.Tr("ui.landmarks.refresh"),
            FocusMode = FocusModeEnum.None
        };
        _refreshBtn.Pressed += () => _ = ScanDuplicatesAsync();
        topRow.AddChild(_refreshBtn);

        _tree = new Tree
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HideRoot = true,
            Columns = 1,
            FocusMode = FocusModeEnum.None,
            AllowRmbSelect = true,
            SelectMode = Tree.SelectModeEnum.Row,
            EnableDragUnfolding = false
        };
        _tree.ItemEdited += OnTreeItemEdited;
        _tree.GuiInput += OnTreeGuiInput;
        vbox.AddChild(_tree);

        var bottomRow = new HBoxContainer();
        bottomRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(bottomRow);

        _selectAllBtn = new Button
        {
            Text = L10n.Tr("ui.landmarks.select_all_duplicates"),
            FocusMode = FocusModeEnum.None
        };
        _selectAllBtn.Pressed += OnSelectAllPressed;
        bottomRow.AddChild(_selectAllBtn);

        _resetBtn = new Button
        {
            Text = L10n.Tr("ui.landmarks.reset_recommendations"),
            FocusMode = FocusModeEnum.None
        };
        _resetBtn.Pressed += OnResetPressed;
        bottomRow.AddChild(_resetBtn);

        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bottomRow.AddChild(spacer);

        _cleanBtn = new Button
        {
            Text = L10n.Tr("ui.landmarks.move_selected_to_trash"),
            Disabled = true,
            FocusMode = FocusModeEnum.None
        };
        _cleanBtn.AddThemeColorOverride("font_color", new Color(1.0f, 0.45f, 0.4f, 1.0f));
        _cleanBtn.Pressed += OnCleanPressed;
        bottomRow.AddChild(_cleanBtn);

        _contextMenu = new PopupMenu();
        _contextMenu.IdPressed += OnContextMenuIdPressed;
        AddChild(_contextMenu);

        CallDeferred(MethodName.ApplyFirstOpenDefaultIfNeeded);
    }

    private void ApplyFirstOpenDefaultIfNeeded()
    {
        if (GeometryRestored) return;
        Position = new Vector2(140, Mathf.Max(TopInset + 20f, 70f));
    }

    public void Initialize(GridSession? session, ISet<Guid>? favoriteIds)
    {
        _session = session;
        _favoriteIds = favoriteIds;
    }

    public void OpenAndScan()
    {
        Visible = true;
        BringToFront();
        _ = ScanDuplicatesAsync();
    }

    public async Task ScanDuplicatesAsync()
    {
        if (_session == null || !IsInstanceValid(this)) return;

        _summaryLabel.Text = L10n.Tr("ui.landmarks.loading");
        _cleanBtn.Disabled = true;

        try
        {
            var items = await _session.GetLandmarksWithFoldersAsync().ConfigureAwait(false);
            Callable.From(() =>
            {
                if (!IsInstanceValid(this)) return;
                _rawLandmarks = items.ToList();
                _groups = LandmarkDuplicateDetector.FindDuplicates(_rawLandmarks, _favoriteIds);
                ApplyDefaultRecommendations();
                RebuildTree();
            }).CallDeferred();
        }
        catch (Exception ex)
        {
            Callable.From(() =>
            {
                if (!IsInstanceValid(this)) return;
                _summaryLabel.Text = ex.Message;
            }).CallDeferred();
        }
    }

    private void ApplyDefaultRecommendations()
    {
        _selectedToDelete.Clear();
        foreach (var group in _groups)
        {
            foreach (var item in group.Items)
            {
                if (item.Id != group.RecommendedKeepItemId)
                {
                    _selectedToDelete.Add(item.Id);
                }
            }
        }
    }

    private void RebuildTree()
    {
        _tree.Clear();
        var root = _tree.CreateItem();

        if (_groups.Count == 0)
        {
            _summaryLabel.Text = L10n.Tr("ui.landmarks.dedup_no_duplicates");
            _cleanBtn.Disabled = true;
            _selectAllBtn.Disabled = true;
            _resetBtn.Disabled = true;
            return;
        }

        _selectAllBtn.Disabled = false;
        _resetBtn.Disabled = false;

        int totalDuplicates = _groups.Sum(g => g.Items.Count - 1);
        _summaryLabel.Text = L10n.TrFormat("ui.landmarks.dedup_summary", _groups.Count, totalDuplicates);

        foreach (var group in _groups)
        {
            var groupNode = _tree.CreateItem(root);
            string matchKindBadge = group.MatchKind == LandmarkDuplicateMatchKind.ExactAsset
                ? L10n.Tr("ui.landmarks.match_kind_asset")
                : L10n.Tr("ui.landmarks.match_kind_name");

            groupNode.SetText(0, $"📁 {group.CanonicalName} ({group.Items.Count}) [{matchKindBadge}]");
            groupNode.SetCustomColor(0, new Color(0.85f, 0.92f, 1.0f, 0.95f));

            foreach (var item in group.Items)
            {
                var itemNode = _tree.CreateItem(groupNode);
                itemNode.SetCellMode(0, TreeItem.TreeCellMode.Check);
                itemNode.SetEditable(0, true);

                bool isSelectedToDelete = _selectedToDelete.Contains(item.Id);
                itemNode.SetChecked(0, isSelectedToDelete);

                bool isKeep = item.Id == group.RecommendedKeepItemId;
                string favMarker = _favoriteIds != null && (_favoriteIds.Contains(item.Id) || _favoriteIds.Contains(item.AssetId)) ? " ★" : "";
                string dateStr = item.CreationDate != default ? $" ({item.CreationDate:d})" : "";
                string statusText = isKeep ? $" [{L10n.Tr("ui.landmarks.recommended_keep")}]" : "";

                itemNode.SetText(0, $"📍 {item.Name} — 📂 {item.FolderPath}{dateStr}{favMarker}{statusText}");
                itemNode.SetMetadata(0, item.Id.ToString());

                if (isKeep)
                {
                    itemNode.SetCustomColor(0, new Color(0.5f, 0.9f, 0.6f, 0.9f));
                }
                else
                {
                    itemNode.SetCustomColor(0, new Color(0.9f, 0.8f, 0.8f, 0.85f));
                }
            }
        }

        UpdateCleanButtonState();
    }

    private void OnTreeItemEdited()
    {
        var item = _tree.GetEdited();
        if (item == null) return;

        string meta = item.GetMetadata(0).AsString();
        if (Guid.TryParse(meta, out var itemId))
        {
            if (item.IsChecked(0))
                _selectedToDelete.Add(itemId);
            else
                _selectedToDelete.Remove(itemId);

            UpdateCleanButtonState();
        }
    }

    private void UpdateCleanButtonState()
    {
        int count = _selectedToDelete.Count;
        _cleanBtn.Disabled = count == 0;
        _cleanBtn.Text = count > 0
            ? L10n.TrFormat("ui.landmarks.move_selected_to_trash_count", count)
            : L10n.Tr("ui.landmarks.move_selected_to_trash");
    }

    private void OnSelectAllPressed()
    {
        _selectedToDelete.Clear();
        foreach (var group in _groups)
        {
            foreach (var item in group.Items)
            {
                if (item.Id != group.RecommendedKeepItemId)
                    _selectedToDelete.Add(item.Id);
            }
        }
        RebuildTree();
    }

    private void OnResetPressed()
    {
        ApplyDefaultRecommendations();
        RebuildTree();
    }

    private void OnCleanPressed()
    {
        int count = _selectedToDelete.Count;
        if (count == 0 || _session == null) return;

        var confirm = new ConfirmWindow();
        var host = GetTree()?.Root?.GetNodeOrNull<CanvasLayer>("Boot/HudLayer")
                   ?? (Node?)GetParent() ?? this;
        host.AddChild(confirm);
        confirm.Initialize(
            title: L10n.Tr("ui.landmarks.clean_confirm_title"),
            question: L10n.TrFormat("ui.landmarks.clean_confirm_question", count),
            confirmLabel: L10n.Tr("ui.landmarks.clean_confirm_btn"),
            danger: true);

        confirm.Confirmed += () => _ = ExecuteDeleteAsync();
    }

    private async Task ExecuteDeleteAsync()
    {
        if (_session == null || !IsInstanceValid(this)) return;

        var toDelete = _selectedToDelete.ToList();
        int deleted = 0;

        _summaryLabel.Text = L10n.Tr("ui.landmarks.cleaning_in_progress");
        _cleanBtn.Disabled = true;

        foreach (var id in toDelete)
        {
            try
            {
                await _session.MoveToTrashAsync(id, isFolder: false).ConfigureAwait(false);
                deleted++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Dedup] MoveToTrashAsync failed for {id}: {ex.Message}");
            }
        }

        Callable.From(() =>
        {
            if (!IsInstanceValid(this)) return;
            OnToast?.Invoke(L10n.TrFormat("ui.landmarks.dedup_completed_toast", deleted));
            OnDuplicatesRemoved?.Invoke();
            _ = ScanDuplicatesAsync();
        }).CallDeferred();
    }

    private void OnTreeGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right)
        {
            var item = _tree.GetItemAtPosition(mb.Position);
            if (item != null)
            {
                _tree.SetSelected(item, 0);
                ShowContextMenuForSelected();
                _tree.AcceptEvent();
            }
        }
    }

    private void ShowContextMenuForSelected()
    {
        if (_session == null) return;
        var selected = _tree.GetSelected();
        if (selected == null) return;

        string meta = selected.GetMetadata(0).AsString();
        if (!Guid.TryParse(meta, out var itemId)) return;

        _contextLandmark = _rawLandmarks.FirstOrDefault(l => l.Id == itemId);
        if (_contextLandmark == null) return;
        _contextGroup = _groups.FirstOrDefault(g => g.Items.Any(i => i.Id == itemId));

        _contextMenu.Clear();

        // Option 0: Open MoveLandmarkWindow (choose destination folder in Landmarks)
        _contextMenu.AddItem(L10n.Tr("ui.landmarks.move_to_folder_action"), 0);

        // Option 1: Quick shortcut: move to the folder where the other duplicate is located
        if (_contextGroup != null)
        {
            var otherItem = _contextGroup.Items.FirstOrDefault(i =>
                i.Id != _contextLandmark.Id &&
                i.ParentId != _contextLandmark.ParentId &&
                i.ParentId != Guid.Empty &&
                !string.IsNullOrEmpty(i.FolderName));

            if (otherItem != null)
            {
                _contextMenu.AddItem(L10n.TrFormat("ui.landmarks.move_to_other_copy_folder", otherItem.FolderName), 1);
            }
        }

        // Option 2: Quick shortcut: move to main 'Landmarks' folder
        bool notInRoot = !string.Equals(_contextLandmark.FolderPath, "Landmarks", StringComparison.OrdinalIgnoreCase);
        if (notInRoot)
        {
            _contextMenu.AddItem(L10n.Tr("ui.landmarks.move_to_landmarks_root"), 2);
        }

        _contextMenu.Position = (Vector2I)GetGlobalMousePosition();
        _contextMenu.Popup();
    }

    private void OnContextMenuIdPressed(long id)
    {
        if (_contextLandmark == null || _session == null) return;

        if (id == 0) // Open MoveLandmarkWindow dialog
        {
            var win = new MoveLandmarkWindow();
            var host = GetTree()?.Root?.GetNodeOrNull<CanvasLayer>("Boot/HudLayer")
                       ?? (Node?)GetParent() ?? this;
            host.AddChild(win);

            Guid? suggestedFolder = null;
            if (_contextGroup != null)
            {
                var otherItem = _contextGroup.Items.FirstOrDefault(i =>
                    i.Id != _contextLandmark.Id &&
                    i.ParentId != _contextLandmark.ParentId &&
                    i.ParentId != Guid.Empty);
                if (otherItem != null) suggestedFolder = otherItem.ParentId;
            }

            win.Initialize(
                _session,
                _contextLandmark.Id,
                _contextLandmark.Name,
                _contextLandmark.FolderPath,
                suggestedFolder,
                (targetFolderId, targetPath) =>
                {
                    OnItemMovedLocally(_contextLandmark.Id, targetFolderId, targetPath);
                });
        }
        else if (id == 1) // Quick move to other copy's folder
        {
            if (_contextGroup != null)
            {
                var otherItem = _contextGroup.Items.FirstOrDefault(i =>
                    i.Id != _contextLandmark.Id &&
                    i.ParentId != _contextLandmark.ParentId &&
                    i.ParentId != Guid.Empty);

                if (otherItem != null)
                {
                    _ = MoveItemToFolderDirectlyAsync(_contextLandmark.Id, otherItem.ParentId, otherItem.FolderPath);
                }
            }
        }
        else if (id == 2) // Quick move to Landmarks root
        {
            if (_session.LandmarksFolderId is { } lmFolderId)
            {
                _ = MoveItemToFolderDirectlyAsync(_contextLandmark.Id, lmFolderId, "Landmarks");
            }
        }
    }

    private async Task MoveItemToFolderDirectlyAsync(Guid itemId, Guid targetFolderId, string targetPath)
    {
        if (_session == null || itemId == Guid.Empty || targetFolderId == Guid.Empty) return;

        try
        {
            await _session.MoveInventoryAsync(itemId, targetFolderId, isFolder: false, targetPath).ConfigureAwait(false);
            Callable.From(() => OnItemMovedLocally(itemId, targetFolderId, targetPath)).CallDeferred();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Dedup] Move failed: {ex.Message}");
        }
    }

    private void OnItemMovedLocally(Guid itemId, Guid targetFolderId, string targetPath)
    {
        if (!IsInstanceValid(this)) return;

        var rawIdx = _rawLandmarks.FindIndex(l => l.Id == itemId);
        if (rawIdx >= 0)
        {
            var old = _rawLandmarks[rawIdx];
            string newFolderName = targetPath.Contains('/') ? targetPath.Split('/').Last() : targetPath;
            _rawLandmarks[rawIdx] = old with
            {
                ParentId = targetFolderId,
                FolderName = newFolderName,
                FolderPath = targetPath
            };
        }

        _groups = LandmarkDuplicateDetector.FindDuplicates(_rawLandmarks, _favoriteIds);
        RebuildTree();

        OnToast?.Invoke(L10n.TrFormat("ui.landmarks.moved_to_folder_toast", targetPath));
        OnDuplicatesRemoved?.Invoke();
    }
}
