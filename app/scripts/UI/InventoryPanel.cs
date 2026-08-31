using Godot;
using SLNG.Net;
using System;
using System.Collections.Generic;
using System.Linq;

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

    // FEAT-UI-16 / FEAT-INV-04: "Inventar" / "Angezogen" / "Outfits" tabs.
    private TabBar _tabs = null!;
    private VBoxContainer _inventoryView = null!;
    private VBoxContainer _wornView = null!;
    private Tree _wornTree = null!;
    private Label _wornStatus = null!;
    private PopupMenu _wornMenu = null!;
    private Timer _wornTimer = null!;
    private Button _wornCleanupBtn = null!;

    // FEAT-INV-04: Outfits tab.
    private VBoxContainer _outfitsView = null!;
    private Tree _outfitsTree = null!;
    private Label _outfitsStatus = null!;
    private LineEdit _outfitNameEdit = null!;
    private Button _outfitSaveBtn = null!;
    private PopupMenu _outfitsMenu = null!;
    private readonly HashSet<Guid> _loadedOutfitFolders = new();
    private Guid? _renamingOutfitId;

    public override void _Ready()
    {
        base._Ready(); // Setup SLNGWindow styling

        PersistId = "inventory"; // FEAT-UI-11: remember position/size across sessions

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

        // FEAT-UI-16: "Inventar" / "Angezogen" tabs. The worn list is its own flat view rather
        // than a highlighted folder buried in the tree.
        _tabs = new TabBar();
        _tabs.AddTab("Inventar");
        _tabs.AddTab("Angezogen");
        _tabs.AddTab("Outfits");
        _tabs.TabChanged += OnTabChanged;
        var tabsMargin = new MarginContainer();
        tabsMargin.AddThemeConstantOverride("margin_left", 8);
        tabsMargin.AddThemeConstantOverride("margin_right", 8);
        tabsMargin.AddThemeConstantOverride("margin_top", 4);
        tabsMargin.AddChild(_tabs);
        vbox.AddChild(tabsMargin);

        _inventoryView = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _inventoryView.AddThemeConstantOverride("separation", 0);
        vbox.AddChild(_inventoryView);

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
        _inventoryView.AddChild(searchContainer);

        _status = new Label();
        var statusMargin = new MarginContainer();
        statusMargin.AddThemeConstantOverride("margin_left", 12);
        statusMargin.AddChild(_status);
        _inventoryView.AddChild(statusMargin);

        _contextMenu = new PopupMenu();
        _contextMenu.AddItem("Wear / Attach", 0);
        _contextMenu.AddItem("Copy", 1);
        _contextMenu.AddItem("Edit", 2);
        _contextMenu.AddItem("Export (Full Perm)", 3);
        _contextMenu.AddItem("Delete", 4);
        _contextMenu.AddItem("Teleport", 5);
        _contextMenu.AddItem("Detach", 6);
        _contextMenu.IdPressed += OnContextMenuIdPressed;
        
        _tree = new InventoryTree 
        { 
            SizeFlagsVertical = SizeFlags.ExpandFill, 
            HideRoot = true, 
            FocusMode = FocusModeEnum.None,
            AllowRmbSelect = true 
        };
        _tree.AddChild(_contextMenu);
        
        _tree.ItemCollapsed += OnItemCollapsed;
        _tree.ItemActivated += OnItemActivated;
        _tree.GuiInput += OnTreeGuiInput;
        _inventoryView.AddChild(_tree);

        // ---- "Angezogen" (Worn) view -------------------------------------------------------
        _wornView = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, Visible = false };
        _wornView.AddThemeConstantOverride("separation", 0);
        vbox.AddChild(_wornView);

        _wornStatus = new Label();
        var wornStatusMargin = new MarginContainer();
        wornStatusMargin.AddThemeConstantOverride("margin_left", 12);
        wornStatusMargin.AddThemeConstantOverride("margin_top", 8);
        wornStatusMargin.AddThemeConstantOverride("margin_bottom", 4);
        wornStatusMargin.AddChild(_wornStatus);
        _wornView.AddChild(wornStatusMargin);

        // FEAT-INV-03: prune dead / unworn-attachment links from the Current Outfit.
        _wornCleanupBtn = new Button { Text = "🧹 Outfit aufräumen", Flat = true };
        _wornCleanupBtn.TooltipText = "Tote Outfit-Links und nicht getragene Anhänge in den " +
            "Papierkorb verschieben (Kleidung & Körperteile bleiben; alles wiederherstellbar).";
        _wornCleanupBtn.Pressed += OnWornCleanupPressed;
        var cleanupMargin = new MarginContainer();
        cleanupMargin.AddThemeConstantOverride("margin_left", 8);
        cleanupMargin.AddThemeConstantOverride("margin_right", 8);
        cleanupMargin.AddThemeConstantOverride("margin_bottom", 4);
        cleanupMargin.AddChild(_wornCleanupBtn);
        _wornView.AddChild(cleanupMargin);

        _wornMenu = new PopupMenu();
        _wornMenu.AddItem("Ablegen", 0);
        _wornMenu.IdPressed += OnWornMenuPressed;

        _wornTree = new Tree
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HideRoot = true,
            FocusMode = FocusModeEnum.None,
            AllowRmbSelect = true
        };
        _wornTree.AddChild(_wornMenu);
        _wornTree.ItemActivated += OnWornItemActivated;
        _wornTree.GuiInput += OnWornGuiInput;
        _wornView.AddChild(_wornTree);

        // Safety-net poll: attachment attach/detach has no clean LibreMetaverse event, so while
        // the Worn tab is open, re-read every few seconds. WornItemsChanged covers wearables/bakes
        // instantly; this catches the rest.
        _wornTimer = new Timer { WaitTime = 2.5, Autostart = false, OneShot = false };
        _wornTimer.Timeout += () => { if (IsInstanceValid(this) && _wornView.Visible) RefreshWorn(); };
        AddChild(_wornTimer);

        // ---- "Outfits" view (FEAT-INV-04) --------------------------------------------------
        _outfitsView = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, Visible = false };
        _outfitsView.AddThemeConstantOverride("separation", 0);
        vbox.AddChild(_outfitsView);

        var saveRow = new HBoxContainer();
        _outfitNameEdit = new LineEdit
        {
            PlaceholderText = "Name für aktuelles Outfit…",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _outfitNameEdit.TextSubmitted += _ => OnSaveOutfitPressed();
        _outfitSaveBtn = new Button { Text = "💾 Speichern" };
        _outfitSaveBtn.Pressed += OnSaveOutfitPressed;
        saveRow.AddChild(_outfitNameEdit);
        saveRow.AddChild(_outfitSaveBtn);
        var saveMargin = new MarginContainer();
        saveMargin.AddThemeConstantOverride("margin_left", 8);
        saveMargin.AddThemeConstantOverride("margin_right", 8);
        saveMargin.AddThemeConstantOverride("margin_top", 8);
        saveMargin.AddThemeConstantOverride("margin_bottom", 4);
        saveMargin.AddChild(saveRow);
        _outfitsView.AddChild(saveMargin);

        _outfitsStatus = new Label();
        var outfitsStatusMargin = new MarginContainer();
        outfitsStatusMargin.AddThemeConstantOverride("margin_left", 12);
        outfitsStatusMargin.AddThemeConstantOverride("margin_bottom", 4);
        outfitsStatusMargin.AddChild(_outfitsStatus);
        _outfitsView.AddChild(outfitsStatusMargin);

        _outfitsMenu = new PopupMenu();
        _outfitsMenu.AddItem("Aktuelles Outfit ersetzen", 0);
        _outfitsMenu.AddItem("Zu aktuellem Outfit hinzufügen", 1);
        _outfitsMenu.AddItem("Von aktuellem Outfit entfernen", 2);
        _outfitsMenu.AddSeparator();
        _outfitsMenu.AddItem("Outfit neu benennen", 3);
        _outfitsMenu.AddItem("Outfit speichern (= akt. Getrage)", 4);
        _outfitsMenu.AddSeparator();
        _outfitsMenu.AddItem("Outfit löschen", 5);
        _outfitsMenu.IdPressed += OnOutfitsMenuPressed;

        _outfitsTree = new Tree
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HideRoot = true,
            FocusMode = FocusModeEnum.None,
            AllowRmbSelect = true
        };
        _outfitsTree.AddChild(_outfitsMenu);
        _outfitsTree.ItemActivated += OnOutfitActivated;
        _outfitsTree.ItemCollapsed += OnOutfitItemCollapsed;
        _outfitsTree.GuiInput += OnOutfitsGuiInput;
        _outfitsView.AddChild(_outfitsTree);
    }

    private partial class InventoryTree : Tree
    {
        public override Variant _GetDragData(Vector2 atPosition)
        {
            var item = GetItemAtPosition(atPosition) ?? GetSelected();
            if (item == null) return default;

            var metaStr = item.GetMetadata(0).AsString();
            if (string.IsNullOrEmpty(metaStr)) return default;

            bool isFolder = !metaStr.Contains(',');
            var parts = metaStr.Split(',');
            if (!isFolder && parts.Length < 7) return default;

            string itemIdStr = parts[0];
            bool canTransfer = isFolder ? true : bool.Parse(parts[3]);
            bool isLink = isFolder ? false : bool.Parse(parts[6]);
            int assetType = isFolder ? -1 : int.Parse(parts[4]);
            string name = item.GetText(0).Replace("  ⇢", "");

            string payload = $"slng_item|{itemIdStr}|{name}|{canTransfer}|{isFolder}|{assetType}";

            var preview = new Label { Text = name };
            SetDragPreview(preview);

            return payload;
        }
    }

    public void Initialize(GridSession session)
    {
        if (_session != null) _session.WornItemsChanged -= OnWornItemsChanged;
        _session = session;
        _session.WornItemsChanged += OnWornItemsChanged;
    }

    public override void _ExitTree()
    {
        if (_session != null) _session.WornItemsChanged -= OnWornItemsChanged;
        base._ExitTree();
    }

    public void Toggle()
    {
        Visible = !Visible;
        if (Visible)
        {
            PopulateRoots();
            // FEAT-INV-03: re-fetch the Current Outfit folder so an outfit change made in another
            // viewer shows without a relog. No-op unless the user has expanded the COF folder in
            // the tree; the "Angezogen" tab is the primary worn view and refreshes on its own.
            if (_session?.CurrentOutfitFolderId is { } cofId) RefreshFolder(cofId);
            if (_wornView.Visible) RefreshWorn();
        }
    }

    /// <summary>Ctrl+O: show the inventory window on the Outfits tab (FEAT-INV-04).</summary>
    public void OpenOnOutfits()
    {
        Visible = true;
        PopulateRoots();
        if (_tabs.CurrentTab == 2) OnTabChanged(2); // already on the tab — just refresh
        else _tabs.CurrentTab = 2;                  // fires TabChanged -> shows + refreshes
    }

    // ---- FEAT-UI-16: Worn tab -------------------------------------------------------------

    private void OnTabChanged(long tab)
    {
        _inventoryView.Visible = tab == 0;
        _wornView.Visible = tab == 1;
        _outfitsView.Visible = tab == 2;

        if (tab == 1) { RefreshWorn(); _wornTimer.Start(); }
        else _wornTimer.Stop();

        if (tab == 2) RefreshOutfits();
    }

    // WornItemsChanged fires on a LibreMetaverse network thread — hop to the main thread the
    // thread-safe way (Node.CallDeferred by method name, never Callable.From(lambda) from a bg
    // thread — that crashes; see the project memory).
    private void OnWornItemsChanged(object? sender, EventArgs e)
    {
        if (IsInstanceValid(this)) CallDeferred(nameof(RefreshWornIfVisible));
    }

    private void RefreshWornIfVisible()
    {
        if (IsInstanceValid(this) && _wornView is { Visible: true }) RefreshWorn();
    }

    private static readonly (SLNG.Core.WornCategory Cat, string Label)[] WornGroups =
    {
        (SLNG.Core.WornCategory.BodyPart, "Körper"),
        (SLNG.Core.WornCategory.Clothing, "Kleidung"),
        (SLNG.Core.WornCategory.Attachment, "Anhänge"),
        (SLNG.Core.WornCategory.Hud, "HUDs"),
    };

    /// <summary>Rebuilds the flat worn list from <see cref="GridSession.GetWornItems"/>. Main
    /// thread only (mutates the Tree); the call is cheap (reads LibreMetaverse caches, no I/O).</summary>
    private void RefreshWorn()
    {
        if (_session == null || !IsInstanceValid(_wornTree)) return;

        var items = _session.GetWornItems();
        _wornTree.Clear();
        var root = _wornTree.CreateItem();

        if (items.Count == 0)
        {
            _wornStatus.Text = "Nichts getragen.";
            return;
        }
        _wornStatus.Text = $"{items.Count} getragen";

        foreach (var (cat, label) in WornGroups)
        {
            var group = items.Where(i => i.Category == cat)
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (group.Count == 0) continue;

            var header = _wornTree.CreateItem(root);
            header.SetText(0, $"{label}  ({group.Count})");
            header.SetSelectable(0, false);
            header.SetCustomColor(0, new Color(0.62f, 0.76f, 0.92f));

            foreach (var it in group)
            {
                var row = _wornTree.CreateItem(header);
                string text = string.IsNullOrEmpty(it.Name) ? "(unbenannt)" : it.Name;
                if (!string.IsNullOrEmpty(it.AttachPoint)) text += $"  ·  {it.AttachPoint}";
                if (!it.Live) text += "  (nicht aktiv)";
                row.SetText(0, text);
                row.SetMetadata(0, it.ItemId.ToString());
                row.SetCustomColor(0, it.Live
                    ? new Color(1.0f, 0.88f, 0.4f)
                    : new Color(1f, 1f, 1f, 0.5f));
            }
            header.Collapsed = false;
        }
    }

    private void OnWornGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb || !mb.Pressed || mb.ButtonIndex != MouseButton.Right) return;
        var row = _wornTree.GetItemAtPosition(mb.Position);
        if (row == null || !Guid.TryParse(row.GetMetadata(0).AsString(), out _)) return; // header / empty
        row.Select(0);
        _wornMenu.Position = (Vector2I)GetGlobalMousePosition();
        _wornMenu.Popup();
    }

    private void OnWornItemActivated()
    {
        var row = _wornTree.GetSelected();
        if (row != null && Guid.TryParse(row.GetMetadata(0).AsString(), out var itemId))
            _ = DetachWornAsync(itemId);
    }

    private void OnWornMenuPressed(long id)
    {
        var row = _wornTree.GetSelected();
        if (row == null || !Guid.TryParse(row.GetMetadata(0).AsString(), out var itemId)) return;
        if (id == 0) _ = DetachWornAsync(itemId);
    }

    private async System.Threading.Tasks.Task DetachWornAsync(Guid itemId)
    {
        if (_session == null) return;
        _wornStatus.Text = "Wird abgelegt…";

        var result = await _session.DetachItemAsync(itemId).ConfigureAwait(false);

        Callable.From(() =>
        {
            if (!IsInstanceValid(this)) return;
            _wornStatus.Text = result.WearableRemoved
                ? "Wearable entfernt — Server bäckt neu…"
                : result.WasAttached
                    ? "Abgelegt."
                    : result.StaleLinksRemoved > 0
                        ? $"War nicht getragen — {result.StaleLinksRemoved} veraltete(n) Outfit-Link entfernt."
                        : "War nicht getragen.";
            RefreshWorn();
            if (_session.CurrentOutfitFolderId is { } cofId) RefreshFolder(cofId);
        }).CallDeferred();
    }

    // FEAT-INV-03: prune the Current Outfit. Synchronous — reads the LibreMetaverse inventory
    // store and sends MoveItem packets, no blocking I/O. Everything moves links to Trash, never
    // real items, and never a Clothing/Bodypart link or a worn item.
    private void OnWornCleanupPressed()
    {
        if (_session == null) return;

        var r = _session.CleanUpCurrentOutfit();
        if (r.Total == 0)
        {
            _wornStatus.Text = "Outfit ist sauber — nichts zu entfernen.";
            return;
        }

        var parts = new System.Collections.Generic.List<string>();
        int dead = r.DeadLinks + r.TrashedTargetLinks;
        if (dead > 0) parts.Add($"{dead} tote(r) Link(s)");
        if (r.UnwornAttachmentLinks > 0) parts.Add($"{r.UnwornAttachmentLinks} nicht getragene(r) Anhang/Anhänge");
        _wornStatus.Text = $"{string.Join(" + ", parts)} → Papierkorb.";

        RefreshWorn();
        if (_session.CurrentOutfitFolderId is { } cofId) RefreshFolder(cofId);
    }

    // ---- FEAT-INV-04: Outfits tab -------------------------------------------------------

    private void RefreshOutfits()
    {
        if (_session == null) return;
        if (_renamingOutfitId is null) _outfitsStatus.Text = "Lädt…";
        _ = RefreshOutfitsAsync();
    }

    private async System.Threading.Tasks.Task RefreshOutfitsAsync()
    {
        System.Collections.Generic.IReadOnlyList<SLNG.Core.OutfitEntry> outfits =
            System.Array.Empty<SLNG.Core.OutfitEntry>();
        string? err = null;
        try { outfits = await _session!.GetSavedOutfitsAsync().ConfigureAwait(false); }
        catch (Exception ex) { err = ex.Message; }

        Callable.From(() =>
        {
            if (!IsInstanceValid(_outfitsTree)) return;
            _outfitsTree.Clear();
            var root = _outfitsTree.CreateItem();

            if (err != null) { _outfitsStatus.Text = $"Fehler: {err}"; return; }
            if (_session?.MyOutfitsFolderId is null)
            {
                _outfitsStatus.Text = "Dieses Grid hat keinen #Outfits-Ordner.";
                return;
            }
            if (outfits.Count == 0) { _outfitsStatus.Text = "Noch keine gespeicherten Outfits."; return; }

            _outfitsStatus.Text = $"{outfits.Count} Outfit(s) · aufklappen zum Anschauen · Rechtsklick = Anhänge anziehen";
            _loadedOutfitFolders.Clear();
            foreach (var o in outfits)
            {
                var row = _outfitsTree.CreateItem(root);
                row.SetText(0, (o.IsCurrent ? "✅ " : "👗 ") + o.Name);
                row.SetMetadata(0, o.FolderId.ToString());
                if (o.IsCurrent) row.SetCustomColor(0, new Color(1.0f, 0.88f, 0.4f));
                row.Collapsed = true;
                var placeholder = _outfitsTree.CreateItem(row);
                placeholder.SetText(0, "…");
                placeholder.SetSelectable(0, false);
            }
        }).CallDeferred();
    }

    private static string StripOutfitPrefix(string s)
        => s.StartsWith("✅ ") ? s["✅ ".Length..]
         : s.StartsWith("👗 ") ? s["👗 ".Length..]
         : s;

    // Lazy-load an outfit folder's contents on first expand, like the main inventory tree.
    private void OnOutfitItemCollapsed(TreeItem item)
    {
        if (item.Collapsed) return; // fires both directions; only expansion loads
        if (!Guid.TryParse(item.GetMetadata(0).AsString(), out var folderId)) return; // not an outfit row
        if (!_loadedOutfitFolders.Add(folderId)) return; // already loaded
        _ = LoadOutfitContentsAsync(item, folderId);
    }

    private async System.Threading.Tasks.Task LoadOutfitContentsAsync(TreeItem row, Guid folderId, bool isRetry = false)
    {
        System.Collections.Generic.IReadOnlyList<SLNG.Core.WornItem> items =
            System.Array.Empty<SLNG.Core.WornItem>();
        try { items = await _session!.GetOutfitContentsAsync(folderId).ConfigureAwait(false); }
        catch (Exception ex) { GD.PrintErr($"[Outfits] contents fetch failed: {ex.Message}"); }

        Callable.From(() =>
        {
            if (!IsInstanceValid(_outfitsTree) || !IsInstanceValid(row)) return;

            var child = row.GetFirstChild();
            while (child != null) { var next = child.GetNext(); child.Free(); child = next; }

            if (items.Count == 0)
            {
                var empty = _outfitsTree.CreateItem(row);
                empty.SetText(0, "(leer)");
                empty.SetSelectable(0, false);
                empty.SetCustomColor(0, new Color(1, 1, 1, 0.4f));
                return;
            }

            bool anyPending = false;
            foreach (var it in items.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                var c = _outfitsTree.CreateItem(row);
                string icon = it.Category switch
                {
                    SLNG.Core.WornCategory.BodyPart => "🧍",
                    SLNG.Core.WornCategory.Clothing => "👕",
                    SLNG.Core.WornCategory.Hud => "🖥",
                    _ => "📦",
                };
                string name = string.IsNullOrEmpty(it.Name) ? "(lädt…)" : it.Name;
                if (string.IsNullOrEmpty(it.Name)) anyPending = true;
                // A saved outfit item that you're also wearing right now is gold, like the Angezogen tab.
                c.SetText(0, $"{icon} {name}");
                c.SetMetadata(0, ""); // child rows are display-only
                c.SetSelectable(0, false);
                if (it.Live) c.SetCustomColor(0, new Color(1.0f, 0.88f, 0.4f));
            }

            // Names arrive from RequestFetchInventory a moment later — reload once.
            if (anyPending && !isRetry)
            {
                var t = GetTree().CreateTimer(1.3);
                t.Timeout += () =>
                {
                    if (IsInstanceValid(this) && IsInstanceValid(row) && !row.Collapsed)
                        _ = LoadOutfitContentsAsync(row, folderId, isRetry: true);
                };
            }
        }).CallDeferred();
    }

    private void OnSaveOutfitPressed()
    {
        if (_session == null) return;
        var name = _outfitNameEdit.Text.Trim();

        if (_renamingOutfitId is { } renameId)
        {
            if (name.Length == 0) { _outfitsStatus.Text = "Erst einen Namen eingeben."; return; }
            bool ok = _session.RenameOutfitAsync(renameId, name);
            if (ok)
            {
                _outfitsStatus.Text = $"Umbenannt in „{name}“.";
                // Update the row in place — a server re-fetch can still race and return the old name.
                for (var r = _outfitsTree.GetRoot()?.GetFirstChild(); r != null; r = r.GetNext())
                    if (Guid.TryParse(r.GetMetadata(0).AsString(), out var fid) && fid == renameId)
                    {
                        r.SetText(0, (r.GetText(0).StartsWith("✅ ") ? "✅ " : "👗 ") + name);
                        break;
                    }
            }
            else _outfitsStatus.Text = "Umbenennen fehlgeschlagen.";
            CancelRenameOutfit();
            return;
        }

        if (name.Length == 0) { _outfitsStatus.Text = "Erst einen Namen eingeben."; return; }
        _outfitsStatus.Text = $"Speichere „{name}“…";
        _ = SaveOutfitAsync(name);
    }

    private async System.Threading.Tasks.Task SaveOutfitAsync(string name)
    {
        Guid? id = null;
        string? err = null;
        try { id = await _session!.SaveCurrentOutfitAsync(name).ConfigureAwait(false); }
        catch (Exception ex) { err = ex.Message; }

        Callable.From(() =>
        {
            if (!IsInstanceValid(this)) return;
            if (err != null) _outfitsStatus.Text = $"Fehler: {err}";
            else if (id is null) _outfitsStatus.Text = "Kein #Outfits-Ordner auf diesem Grid.";
            else { _outfitsStatus.Text = $"„{name}“ gespeichert."; _outfitNameEdit.Text = ""; }
            RefreshOutfits();
        }).CallDeferred();
    }

    // Double-click just folds/unfolds the outfit to look inside. Wearing is right-click only, so
    // exploring a list of outfits can't accidentally attach a pile of objects.
    private void OnOutfitActivated()
    {
        var row = _outfitsTree.GetSelected();
        if (row != null && Guid.TryParse(row.GetMetadata(0).AsString(), out _))
            row.Collapsed = !row.Collapsed;
    }

    private void OnOutfitsGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb || !mb.Pressed || mb.ButtonIndex != MouseButton.Right) return;
        var row = _outfitsTree.GetItemAtPosition(mb.Position);
        if (row == null || !Guid.TryParse(row.GetMetadata(0).AsString(), out _)) return;
        if (_renamingOutfitId is not null) CancelRenameOutfit();
        row.Select(0);
        _outfitsMenu.Position = (Vector2I)GetGlobalMousePosition();
        _outfitsMenu.Popup();
    }

    private void OnOutfitsMenuPressed(long id)
    {
        var row = _outfitsTree.GetSelected();
        if (row == null || !Guid.TryParse(row.GetMetadata(0).AsString(), out var folderId)) return;
        switch (id)
        {
            case 0: _ = ReplaceWornWithOutfitAsync(folderId); break;          // make my attachments match the outfit
            case 1: _ = WearOutfitAsync(folderId); break;                     // attach the outfit's objects, keep current
            case 2: _ = RemoveOutfitFromWornAsync(folderId); break;           // detach the outfit's attachments
            case 3: BeginRenameOutfit(row); break;
            case 4: _ = ModifyOutfitAsync(folderId, replace: true); break;    // saved outfit contents := current worn
            case 5: DeleteOutfitAsync(folderId); break;
        }
    }

    // Rename via the name field at the top (Tree cell-editing needs keyboard focus, which this
    // tree deliberately doesn't take). The "💾 Speichern" button doubles as "✏️ Umbenennen"
    // while a rename is armed.
    private void BeginRenameOutfit(TreeItem row)
    {
        if (!Guid.TryParse(row.GetMetadata(0).AsString(), out var folderId)) return;
        _renamingOutfitId = folderId;

        _outfitNameEdit.Text = StripOutfitPrefix(row.GetText(0)).Trim();
        _outfitSaveBtn.Text = "✏️ Umbenennen";
        _outfitsStatus.Text = "Neuen Namen eingeben, dann Enter / „Umbenennen“.";
        _outfitNameEdit.GrabFocus();
        _outfitNameEdit.SelectAll();
    }

    private void CancelRenameOutfit()
    {
        _renamingOutfitId = null;
        _outfitSaveBtn.Text = "💾 Speichern";
        _outfitNameEdit.Text = "";
    }

    private async System.Threading.Tasks.Task RemoveOutfitFromWornAsync(Guid folderId)
    {
        Callable.From(() => { if (IsInstanceValid(this)) _outfitsStatus.Text = "Entferne…"; }).CallDeferred();
        int n = 0;
        string? err = null;
        try { n = await _session!.RemoveOutfitFromWornAsync(folderId).ConfigureAwait(false); }
        catch (Exception ex) { err = ex.Message; }
        Callable.From(() =>
        {
            if (!IsInstanceValid(this)) return;
            _outfitsStatus.Text = err != null ? $"Fehler: {err}"
                : n == 0 ? "Nichts davon getragen."
                         : $"{n} Anhang/Anhänge abgelegt. Kleidung & Körper unverändert (Phase 2).";
        }).CallDeferred();
    }

    private void DeleteOutfitAsync(Guid folderId)
    {
        if (_session?.DeleteOutfitAsync(folderId) == true)
        {
            _outfitsStatus.Text = "Outfit in den Papierkorb verschoben.";
            RefreshOutfits();
        }
    }

    private async System.Threading.Tasks.Task ReplaceWornWithOutfitAsync(Guid folderId)
    {
        Callable.From(() => { if (IsInstanceValid(this)) _outfitsStatus.Text = "Tausche Anhänge…"; }).CallDeferred();

        (int Detached, int Attached) r = (0, 0);
        string? err = null;
        try { r = await _session!.ReplaceWornWithOutfitAttachmentsAsync(folderId).ConfigureAwait(false); }
        catch (Exception ex) { err = ex.Message; }

        Callable.From(() =>
        {
            if (!IsInstanceValid(this)) return;
            _outfitsStatus.Text = err != null
                ? $"Fehler: {err}"
                : $"{r.Detached} abgelegt, {r.Attached} angezogen. Kleidung & Körper unverändert (Phase 2).";
            if (err == null) RefreshOutfits(); // the ✅ "getragen" marker moved
        }).CallDeferred();
    }

    private async System.Threading.Tasks.Task WearOutfitAsync(Guid folderId)
    {
        int n = 0;
        string? err = null;
        try { n = await _session!.WearOutfitAttachmentsAsync(folderId).ConfigureAwait(false); }
        catch (Exception ex) { err = ex.Message; }

        Callable.From(() =>
        {
            if (!IsInstanceValid(this)) return;
            _outfitsStatus.Text = err != null
                ? $"Fehler: {err}"
                : n == 0
                    ? "Keine Anhänge in diesem Outfit."
                    : $"{n} Anhang/Anhänge angezogen. Kleidung & Körper folgen mit FEAT-AVATAR-01 Phase 2.";
        }).CallDeferred();
    }

    // "hinzufügen" (replace=false) or "ersetzen" (replace=true) on an existing outfit folder.
    private async System.Threading.Tasks.Task ModifyOutfitAsync(Guid folderId, bool replace)
    {
        Callable.From(() => { if (IsInstanceValid(this)) _outfitsStatus.Text = replace ? "Ersetze…" : "Füge hinzu…"; }).CallDeferred();

        int n = 0;
        string? err = null;
        try
        {
            n = replace
                ? await _session!.ReplaceOutfitWithCurrentAsync(folderId).ConfigureAwait(false)
                : await _session!.AddCurrentToOutfitAsync(folderId).ConfigureAwait(false);
        }
        catch (Exception ex) { err = ex.Message; }

        Callable.From(() =>
        {
            if (!IsInstanceValid(this)) return;
            _outfitsStatus.Text = err != null
                ? $"Fehler: {err}"
                : replace
                    ? $"Outfit ersetzt — {n} Teil(e) verlinkt."
                    : n == 0 ? "Nichts hinzuzufügen — alles schon im Outfit."
                             : $"{n} Teil(e) zum Outfit hinzugefügt.";

            // Reload that outfit's contents if it's expanded.
            var row = _outfitsTree.GetSelected();
            if (row != null && Guid.TryParse(row.GetMetadata(0).AsString(), out var fid) && fid == folderId)
            {
                _loadedOutfitFolders.Remove(folderId);
                if (!row.Collapsed) _ = LoadOutfitContentsAsync(row, folderId);
            }
        }).CallDeferred();
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

        // The Current Outfit folder used to be pinned open here as a poor-man's worn-items view.
        // The "Angezogen" tab (FEAT-UI-16) replaced that — it's live, grouped, and marks stale
        // links — so the redundant pinned copy is gone. The real COF folder is still reachable in
        // its normal place under "My Inventory" for anyone who wants the raw link list.
    }

    private void OnSearchTextChanged(string newText)
    {
        var root = _tree.GetRoot();
        if (root == null) return;
        string query = newText.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(query))
        {
            EnsureFoldersLoadedForSearch(root);
        }
        FilterTree(root, query);
    }

    private void EnsureFoldersLoadedForSearch(TreeItem item)
    {
        var metaStr = item.GetMetadata(0).AsString();
        var idStr = metaStr.Contains(',') ? metaStr.Split(',')[0] : metaStr;

        if (!metaStr.Contains(',') && Guid.TryParse(idStr, out var folderId))
        {
            if (!_loadedFolders.Contains(folderId))
            {
                LoadFolder(item, folderId);
            }
        }

        foreach (var child in item.GetChildren())
        {
            EnsureFoldersLoadedForSearch(child);
        }
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
                if (parts.Length >= 7)
                {
                    bool canCopy = bool.Parse(parts[1]);
                    bool canModify = bool.Parse(parts[2]);
                    bool canTransfer = bool.Parse(parts[3]);
                    int assetType = int.Parse(parts[4]);
                    bool isLink = bool.Parse(parts[6]);
                    // Links (e.g. Current Outfit entries) always carry AssetId == Guid.Empty --
                    // their target item, not this row, holds the real landmark asset -- so
                    // exclude them by IsLink, not by asset-id emptiness: a just-created (non-link)
                    // landmark can ALSO have a momentarily-empty asset id (server-side indexing
                    // lag right after creation, see TeleportAsync below), and that case must still
                    // enable Teleport rather than being mistaken for a link.
                    bool isLandmark = assetType == SLNG.Core.AssetTypeIds.Landmark && !isLink;

                    bool isWorn = false;
                    var wornMap = _session?.GetWornItemsMap() ?? new System.Collections.Generic.Dictionary<Guid, string>();
                    if (Guid.TryParse(parts[0], out var checkId))
                    {
                        if (wornMap.ContainsKey(checkId)) isWorn = true;
                        else if (parts.Length >= 8 && Guid.TryParse(parts[7], out var linkTarget) && wornMap.ContainsKey(linkTarget)) isWorn = true;
                    }
                    if (!isWorn)
                    {
                        var parent = item.GetParent();
                        while (parent != null)
                        {
                            var pMeta = parent.GetMetadata(0).AsString();
                            var pIdStr = pMeta.Contains(',') ? pMeta.Split(',')[0] : pMeta;
                            if (Guid.TryParse(pIdStr, out var pId) && _session?.CurrentOutfitFolderId == pId)
                            {
                                isWorn = true;
                                break;
                            }
                            parent = parent.GetParent();
                        }
                    }

                    _contextMenu.SetItemDisabled(0, isWorn); // Wear / Attach
                    _contextMenu.SetItemDisabled(1, !canCopy); // Copy
                    _contextMenu.SetItemDisabled(2, !canModify); // Edit
                    _contextMenu.SetItemDisabled(3, !(canCopy && canModify && canTransfer)); // Export
                    _contextMenu.SetItemDisabled(4, false); // Delete
                    _contextMenu.SetItemDisabled(5, !isLandmark); // Teleport
                    _contextMenu.SetItemDisabled(6, !isWorn); // Detach

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
            TryTeleportFromItem(item);
        }
        else if (id == 0) // Wear / Attach
        {
            if (isFolder) return;
            var parts = metaStr.Split(',');
            _ = AttachAndRefreshAsync(itemId, parts);
        }
        else if (id == 6) // Detach
        {
            if (isFolder) return;
            var parts = metaStr.Split(',');
            _ = DetachAndRefreshAsync(itemId, parts);
        }
    }

    /// <summary>Double-click activation on a Tree row -- teleports immediately if it's a landmark,
    /// otherwise no-ops (folders/other item types have no default double-click action yet).</summary>
    private void OnItemActivated()
    {
        var item = _tree.GetSelected();
        if (item == null || _session == null) return;
        TryTeleportFromItem(item);
    }

    /// <summary>Parses a row's metadata and starts teleporting if it's a (non-link) landmark --
    /// shared by the context menu's Teleport action and double-click activation. Metadata layout:
    /// Id,CanCopy,CanModify,CanTransfer,AssetType,AssetId,IsLink,LinkTargetId (see Populate).
    /// Uses "at least 7" rather than an exact count so adding further trailing fields later
    /// doesn't silently re-break this the way LinkTargetId's addition did (parts.Length != 7).</summary>
    private void TryTeleportFromItem(TreeItem item)
    {
        var metaStr = item.GetMetadata(0).AsString();
        if (!metaStr.Contains(',')) return; // folder row -- metadata is just the folder id

        var parts = metaStr.Split(',');
        if (parts.Length < 7) return;
        if (!Guid.TryParse(parts[0], out var itemId)) return;
        if (!int.TryParse(parts[4], out var assetType) || assetType != SLNG.Core.AssetTypeIds.Landmark) return;
        if (!bool.TryParse(parts[6], out var isLink) || isLink) return;
        if (!Guid.TryParse(parts[5], out var assetId)) return;

        var parentItem = item.GetParent();
        Guid? parentFolderId = null;
        var parentMetaStr = parentItem?.GetMetadata(0).AsString() ?? "";
        var parentIdStr = parentMetaStr.Contains(',') ? parentMetaStr.Split(',')[0] : parentMetaStr;
        if (Guid.TryParse(parentIdStr, out var pid)) parentFolderId = pid;

        _status.Text = "Teleporting…";
        _ = TeleportAsync(itemId, assetId, parentFolderId);
    }

    private async System.Threading.Tasks.Task AttachAndRefreshAsync(Guid itemId, string[] parts)
    {
        if (_session == null) return;
        _status.Text = "Attaching…";

        await _session.AttachItemAsync(itemId).ConfigureAwait(false);

        Callable.From(() =>
        {
            if (!IsInstanceValid(this)) return;
            _status.Text = "Attached.";
            if (_session.CurrentOutfitFolderId is { } cofId)
            {
                RefreshFolder(cofId);
            }
            var selectedItem = _tree.GetSelected();
            var parentItem = selectedItem?.GetParent();
            if (parentItem != null)
            {
                var parentMetaStr = parentItem.GetMetadata(0).AsString();
                var parentIdStr = parentMetaStr.Contains(',') ? parentMetaStr.Split(',')[0] : parentMetaStr;
                if (Guid.TryParse(parentIdStr, out var parentId))
                {
                    RefreshFolder(parentId);
                }
            }
        }).CallDeferred();
    }

    private async System.Threading.Tasks.Task DetachAndRefreshAsync(Guid itemId, string[] parts)
    {
        if (_session == null) return;
        _status.Text = "Detaching…";

        var result = await _session.DetachItemAsync(itemId).ConfigureAwait(false);
        if (parts.Length >= 8 && Guid.TryParse(parts[7], out var linkTargetId) && linkTargetId != Guid.Empty && linkTargetId != itemId)
        {
            var linkResult = await _session.DetachItemAsync(linkTargetId).ConfigureAwait(false);
            result = new SLNG.Core.DetachResult(
                result.WasAttached || linkResult.WasAttached,
                result.StaleLinksRemoved + linkResult.StaleLinksRemoved,
                result.WearableRemoved || linkResult.WearableRemoved);
        }

        // "Detached." unconditionally was the misleading part of the original report: for an item
        // that only had a stale Current-Outfit link the detach packet is a server-side no-op, so
        // the row stayed exactly as it was under a success message. Say which of the two happened.
        string status = result.WearableRemoved
            ? "Wearable removed — server re-baking…"
            : result.WasAttached
                ? "Detached."
                : result.StaleLinksRemoved > 0
                    ? $"Was not attached — removed {result.StaleLinksRemoved} stale outfit link(s)."
                    : "Was not attached, and no outfit link found.";

        Callable.From(() =>
        {
            if (!IsInstanceValid(this)) return;
            _status.Text = status;
            if (_session.CurrentOutfitFolderId is { } cofId)
            {
                RefreshFolder(cofId);
            }
            var selectedItem = _tree.GetSelected();
            var parentItem = selectedItem?.GetParent();
            if (parentItem != null)
            {
                var parentMetaStr = parentItem.GetMetadata(0).AsString();
                var parentIdStr = parentMetaStr.Contains(',') ? parentMetaStr.Split(',')[0] : parentMetaStr;
                if (Guid.TryParse(parentIdStr, out var parentId))
                {
                    RefreshFolder(parentId);
                }
            }
        }).CallDeferred();
    }

    /// <summary>Teleports to a landmark's asset id. If that id is still <see cref="Guid.Empty"/>
    /// (the freshly-created-landmark race -- see RefreshFolder/Populate), re-resolves it from a
    /// fresh fetch of the parent folder before giving up: a server-side indexing lag can leave a
    /// just-created item's asset id briefly unresolved even right after RefreshFolder's own patch,
    /// e.g. if the folder wasn't expanded at creation time so that patch never ran. Never passes
    /// Guid.Empty to TeleportToLandmarkAsync -- the SL wire protocol treats a null landmark id as
    /// "teleport home", so an unresolved id must fail loudly, not silently send the agent home.</summary>
    private async System.Threading.Tasks.Task TeleportAsync(Guid itemId, Guid assetId, Guid? parentFolderId)
    {
        GD.Print($"[Teleport] item={itemId} assetId={assetId} parentFolder={parentFolderId}");

        if (assetId == Guid.Empty && parentFolderId is { } folderId && _session != null)
        {
            var children = await _session.FetchInventoryChildrenAsync(folderId).ConfigureAwait(false);
            var fresh = children.FirstOrDefault(c => c.Id == itemId);
            assetId = fresh?.AssetId ?? Guid.Empty;
            GD.Print($"[Teleport] re-resolved assetId={assetId} (found={fresh != null})");
        }

        if (assetId == Guid.Empty)
        {
            GD.PrintErr("[Teleport] asset id still empty after re-resolve -- refusing to teleport");
            Callable.From(() =>
            {
                if (IsInstanceValid(this))
                    _status.Text = "Landmark not ready yet — try again in a moment.";
            }).CallDeferred();
            return;
        }

        var result = await _session!.TeleportToLandmarkAsync(assetId).ConfigureAwait(false);
        GD.Print($"[Teleport] result success={result.Success} message='{result.Message}'");
        Callable.From(() =>
        {
            if (!IsInstanceValid(this)) return;
            _status.Text = result.Success
                ? string.Empty
                : $"Teleport failed{(string.IsNullOrEmpty(result.Message) ? "." : $": {result.Message}")}";
                
            // If teleport was successful, release focus from the inventory panel so the user can immediately move with WASD
            if (result.Success)
            {
                GetViewport().GuiReleaseFocus();
            }
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

        var wornMap = _session?.GetWornItemsMap() ?? new System.Collections.Generic.Dictionary<Guid, string>();

        // Folders first, then items — each group keeps the server's by-name order.
        foreach (var entry in children)
            if (entry.IsFolder)
                AddFolderItem(item, entry.Id, entry.Name);
        foreach (var entry in children)
        {
            if (entry.IsFolder) continue;
            var row = _tree.CreateItem(item);

            // See RefreshFolder's doc comment: a just-created item's own known-good asset id
            // (from the create response, not this fetch) wins over whatever this listing reports.
            // Live-tested proof the lag isn't limited to asset_id: a landmark refreshed right
            // after creation came back with AssetType == 0 (not 3/Landmark) too, from the exact
            // same indexing-lag fetch -- so AssetType gets the same known-good override here,
            // hardcoded to Landmark since that's the only case this override path is used for.
            bool isKnownItem = entry.Id == knownItemId && knownAssetId.HasValue;
            bool isLandmarkSubtree = _session != null && _session.IsInLandmarksSubtree(entry.ParentId);
            var assetId = isKnownItem ? knownAssetId!.Value : entry.AssetId;
            int assetType = (isKnownItem || isLandmarkSubtree) ? SLNG.Core.AssetTypeIds.Landmark : entry.AssetType;

            // Links (Current Outfit etc.) point at another inventory item — mark them so an
            // apparently duplicated item is readable as the link it is. Landmarks get a globe
            // prefix so they're recognizable in the tree without opening the context menu.
            string text = entry.IsLink ? entry.Name + "  ⇢" : entry.Name;
            if (assetType == SLNG.Core.AssetTypeIds.Landmark) text = "🌐 " + text;
            text += entry.GetPermissionSuffix();

            bool isWorn = wornMap.TryGetValue(entry.Id, out var loc) || (entry.IsLink && wornMap.TryGetValue(entry.LinkTargetId, out loc));

            if (isWorn)
            {
                if (!string.IsNullOrEmpty(loc) && loc != "getragen")
                {
                    text += $" (getragen an {loc})";
                }
                else if (_session?.CurrentOutfitFolderId is { } cofId && entry.ParentId != cofId)
                {
                    text += " (getragen)";
                }

                // Highlight worn items with a warm gold color so they stand out like Firestorm
                row.SetCustomColor(0, new Color(1.0f, 0.88f, 0.4f));
            }

            row.SetText(0, text);
            row.SetMetadata(0, $"{entry.Id},{entry.CanCopy},{entry.CanModify},{entry.CanTransfer},{assetType},{assetId},{entry.IsLink},{entry.LinkTargetId}");
        }

        if (children.Count == 0)
        {
            var empty = _tree.CreateItem(item);
            empty.SetText(0, "(empty)");
            empty.SetCustomColor(0, new Color(1, 1, 1, 0.4f));
        }

        if (_searchBox != null && !string.IsNullOrEmpty(_searchBox.Text))
        {
            var root = _tree.GetRoot();
            if (root != null)
            {
                FilterTree(root, _searchBox.Text.Trim().ToLowerInvariant());
            }
        }

        _status.Text = "";
    }
}
