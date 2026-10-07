using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// Dialog allowing user to select a destination folder inside Landmarks hierarchy
/// and move an inventory landmark there (FEAT-UI-68).
/// </summary>
public partial class MoveLandmarkWindow : SLNGWindow
{
    private GridSession? _session;
    private Guid _itemId;
    private string _itemName = "";
    private string _currentPath = "";
    private Action<Guid, string>? _onMoved;

    private Label _infoLabel = null!;
    private Tree _folderTree = null!;
    private Button _newFolderBtn = null!;
    private Button _moveBtn = null!;
    private Button _cancelBtn = null!;

    private Guid _selectedFolderId;
    private string _selectedFolderPath = "Landmarks";
    private string _selectedFolderName = "Landmarks";

    private sealed class FolderItemNode
    {
        public Guid Id { get; set; }
        public string Name { get; }
        public string Path { get; }
        public Dictionary<string, FolderItemNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FolderItemNode(Guid id, string name, string path)
        {
            Id = id;
            Name = name;
            Path = path;
        }
    }

    public override void _Ready()
    {
        base._Ready();

        Title = L10n.Tr("ui.landmarks.move_dialog_title");
        CustomMinimumSize = new Vector2(350, 180);
        Size = CustomMinimumSize;
        OnCloseRequested = () => QueueFree();

        var vbox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        vbox.AddThemeConstantOverride("separation", 8);
        ContentContainer.AddChild(vbox);

        _infoLabel = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _infoLabel.AddThemeFontSizeOverride("font_size", 12);
        vbox.AddChild(_infoLabel);

        var treeHeader = new HBoxContainer();
        vbox.AddChild(treeHeader);

        var targetPrompt = new Label
        {
            Text = L10n.Tr("ui.landmarks.select_target_folder"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center
        };
        targetPrompt.AddThemeFontSizeOverride("font_size", 11);
        targetPrompt.AddThemeColorOverride("font_color", new Color(0.7f, 0.8f, 0.9f));
        treeHeader.AddChild(targetPrompt);

        _newFolderBtn = new Button
        {
            Text = L10n.Tr("ui.landmarks.new_folder_btn"),
            TooltipText = L10n.Tr("ui.landmarks.new_folder_tooltip"),
            FocusMode = FocusModeEnum.None
        };
        _newFolderBtn.Pressed += OnNewFolderPressed;
        treeHeader.AddChild(_newFolderBtn);

        _folderTree = new Tree
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row
        };
        _folderTree.ItemSelected += OnFolderSelected;
        _folderTree.ItemActivated += OnFolderActivated;
        vbox.AddChild(_folderTree);

        var bottomRow = new HBoxContainer();
        bottomRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(bottomRow);

        _cancelBtn = new Button
        {
            Text = L10n.Tr("ui.common.cancel"),
            FocusMode = FocusModeEnum.None
        };
        _cancelBtn.Pressed += () => QueueFree();
        bottomRow.AddChild(_cancelBtn);

        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bottomRow.AddChild(spacer);

        _moveBtn = new Button
        {
            Text = L10n.Tr("ui.landmarks.move_here_btn"),
            FocusMode = FocusModeEnum.None
        };
        _moveBtn.Pressed += OnConfirmMove;
        bottomRow.AddChild(_moveBtn);
    }

    private void FitAndCenter()
    {
        if (!IsInstanceValid(this)) return;

        var vp = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        float width = Mathf.Min(350f, Mathf.Max(260f, vp.X - 40f));
        float height = Mathf.Min(GetCombinedMinimumSize().Y, vp.Y - 60f);

        Size = new Vector2(width, height);
        Position = new Vector2(
            Mathf.Max(20f, (vp.X - Size.X) * 0.5f),
            Mathf.Max(TopInset + 20f, (vp.Y - Size.Y) * 0.35f));
    }

    public void Initialize(
        GridSession? session,
        Guid itemId,
        string itemName,
        string currentFolderPath,
        Guid? suggestedTargetFolderId = null,
        Action<Guid, string>? onMoved = null)
    {
        _session = session;
        _itemId = itemId;
        _itemName = itemName;
        _currentPath = currentFolderPath;
        _onMoved = onMoved;

        _infoLabel.Text = $"📍 {itemName}\n📂 {L10n.Tr("ui.landmarks.current_folder")}: {currentFolderPath}";

        RebuildFolderTree(suggestedTargetFolderId);
        CallDeferred(nameof(FitAndCenter));
    }

    private void RebuildFolderTree(Guid? selectFolderId = null)
    {
        _folderTree.Clear();
        var root = _folderTree.CreateItem();

        if (_session == null || _session.LandmarksFolderId is not { } rootLmId || rootLmId == Guid.Empty)
            return;

        var folders = _session.GetLandmarkFolders();
        if (folders.Count == 0) return;

        var rootNode = new FolderItemNode(rootLmId, "Landmarks", "Landmarks");
        foreach (var f in folders)
        {
            if (f.Id == rootLmId) continue;
            string rel = f.Path.StartsWith("Landmarks/", StringComparison.OrdinalIgnoreCase)
                ? f.Path.Substring("Landmarks/".Length)
                : f.Path;

            var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var curr = rootNode;
            string curPath = "Landmarks";
            for (int i = 0; i < segments.Length; i++)
            {
                curPath += "/" + segments[i];
                if (!curr.Children.TryGetValue(segments[i], out var child))
                {
                    var id = (i == segments.Length - 1) ? f.Id : Guid.Empty;
                    child = new FolderItemNode(id, segments[i], curPath);
                    curr.Children[segments[i]] = child;
                }
                else if (i == segments.Length - 1 && child.Id == Guid.Empty)
                {
                    child.Id = f.Id;
                }
                curr = child;
            }
        }

        TreeItem? itemToSelect = null;
        Guid targetToSelect = selectFolderId ?? rootLmId;
        int renderedCount = 0;

        void RenderNode(TreeItem parentTreeItem, FolderItemNode node)
        {
            renderedCount++;
            var treeItem = _folderTree.CreateItem(parentTreeItem);
            treeItem.SetText(0, $"📁 {node.Name}");
            treeItem.SetMetadata(0, $"{node.Id}|{node.Path}|{node.Name}");

            if (node.Id == targetToSelect)
            {
                itemToSelect = treeItem;
            }

            foreach (var child in node.Children.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                RenderNode(treeItem, child);
            }
        }

        RenderNode(root, rootNode);

        // Adjust tree height to number of items, bounded within [90, 210] px
        float desiredTreeHeight = Mathf.Clamp(renderedCount * 24f + 8f, 90f, 210f);
        _folderTree.CustomMinimumSize = new Vector2(0, desiredTreeHeight);

        if (itemToSelect != null)
        {
            _folderTree.SetSelected(itemToSelect, 0);
            UpdateSelectionFromItem(itemToSelect);
        }
        else
        {
            var first = root.GetFirstChild();
            if (first != null)
            {
                _folderTree.SetSelected(first, 0);
                UpdateSelectionFromItem(first);
            }
        }
    }

    private void OnFolderSelected()
    {
        var item = _folderTree.GetSelected();
        if (item != null) UpdateSelectionFromItem(item);
    }

    private void UpdateSelectionFromItem(TreeItem item)
    {
        string meta = item.GetMetadata(0).AsString();
        var parts = meta.Split('|');
        if (parts.Length >= 3 && Guid.TryParse(parts[0], out var id))
        {
            _selectedFolderId = id;
            _selectedFolderPath = parts[1];
            _selectedFolderName = parts[2];

            _moveBtn.Disabled = _selectedFolderId == Guid.Empty;
            _moveBtn.Text = L10n.TrFormat("ui.landmarks.move_to_chosen_folder", _selectedFolderName);
        }
    }

    private void OnFolderActivated()
    {
        OnConfirmMove();
    }

    private void OnConfirmMove()
    {
        if (_session == null || _itemId == Guid.Empty || _selectedFolderId == Guid.Empty) return;

        _moveBtn.Disabled = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await _session.MoveInventoryAsync(_itemId, _selectedFolderId, isFolder: false, _selectedFolderPath).ConfigureAwait(false);
                Callable.From(() =>
                {
                    _onMoved?.Invoke(_selectedFolderId, _selectedFolderPath);
                    QueueFree();
                }).CallDeferred();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MoveLandmark] Move failed: {ex.Message}");
                Callable.From(() =>
                {
                    if (IsInstanceValid(this)) _moveBtn.Disabled = false;
                }).CallDeferred();
            }
        });
    }

    private void OnNewFolderPressed()
    {
        if (_session == null) return;
        var prompt = new TextPromptWindow();
        var host = GetTree()?.Root?.GetNodeOrNull<CanvasLayer>("Boot/HudLayer")
                   ?? (Node?)GetParent() ?? this;
        host.AddChild(prompt);
        prompt.Initialize(
            title: L10n.Tr("ui.landmarks.new_folder_title"),
            prompt: L10n.TrFormat("ui.landmarks.new_folder_prompt", _selectedFolderName),
            initialText: "",
            confirmLabel: L10n.Tr("ui.landmarks.create_folder_btn"));

        prompt.Confirmed += newFolderName =>
        {
            if (string.IsNullOrWhiteSpace(newFolderName)) return;
            var parentId = _selectedFolderId != Guid.Empty ? _selectedFolderId : _session.LandmarksFolderId ?? Guid.Empty;
            if (parentId == Guid.Empty) return;
            var newFolderId = _session.CreateInventoryFolder(parentId, newFolderName.Trim());
            RebuildFolderTree(newFolderId);
            CallDeferred(nameof(FitAndCenter));
        };
    }
}
