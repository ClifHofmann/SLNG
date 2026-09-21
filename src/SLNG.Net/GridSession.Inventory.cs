using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using LibreMetaverse;
using LibreMetaverse.Imaging;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Microsoft.Extensions.Logging;
using SLNG.Core;

namespace SLNG.Net;

// GridSession, Inventory part.
//
// The inventory tree and its cache, attachments, the Current Outfit Folder and saved
// outfits, landmarks and item properties.
//
// Split out of the single 10,042-line GridSession.cs -- a pure move, no member
// changed. The class is still one type; `partial` only spreads it over files that
// can be read, reviewed and merged independently.
public sealed partial class GridSession
{
    /// <summary>Root folder id of the agent's own inventory, or null until login has completed
    /// (LibreMetaverse builds the store — folders only, no items — from the login response's
    /// inventory skeleton; there is no way to opt out and nothing extra to request).</summary>
    public Guid? InventoryRootId => _client.Inventory.Store?.RootFolder?.UUID.Guid;

    /// <summary>Root folder id of the grid-provided Library tree, or null until login (or if the
    /// grid has no library).</summary>
    public Guid? LibraryRootId => _client.Inventory.Store?.LibraryFolder?.UUID.Guid;

    // FEAT-INV-07: path of this account's inventory cache file, null until OpenInventoryCache.
    // While it is null every fetch behaves exactly as before -- the cache is an optimisation,
    // never a dependency.
    private string? _inventoryCachePath;

    /// <summary>
    /// FEAT-INV-07: loads this account's on-disk inventory cache, so folders that have not changed
    /// since the last session are drawn without a CAPS round trip. Call once per login, after the
    /// inventory skeleton has arrived, with a directory the client owns (the app resolves
    /// <c>user://</c> — <c>src/</c> must not know about Godot's virtual filesystem).
    ///
    /// <para><b>The version comparison is LibreMetaverse's, not ours.</b> <c>RestoreFromDisk</c>
    /// implements exactly the algorithm the reference viewer uses in
    /// <c>LLInventoryModel::loadSkeleton</c> (llinventorymodel.cpp:2929-2946): for every cached
    /// folder it compares the cached version against the version the login skeleton just reported
    /// and sets <c>InventoryNode.NeedsUpdate</c> accordingly, restoring contents only for folders
    /// that match and dropping items whose parent is dirty (InventoryCache.cs:195-250). Verified
    /// against the <i>pinned</i> 3.1.3 package by round-tripping a store, not merely read in the
    /// newer vendored checkout — see <c>InventoryCacheTests</c>, which pins that behaviour so a
    /// package bump that breaks it fails a test instead of serving a stale inventory.</para>
    ///
    /// <para>Ordering matters: the skeleton must already be in the store, or there are no server
    /// versions to compare against and every cached folder is discarded as orphaned.</para>
    /// </summary>
    public void OpenInventoryCache(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;

        var agent = _client.Self.AgentID;
        if (agent == UUID.Zero) return; // not logged in yet -- nothing to key the file by

        var store = _client.Inventory.Store;
        if (store?.RootFolder == null)
        {
            Console.Error.WriteLine("[InvCache] skeleton not loaded yet -- not restoring the cache");
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            // Keyed by agent id: two accounts on one machine, or the same name on two grids,
            // must never read each other's inventory.
            _inventoryCachePath = System.IO.Path.Combine(directory, $"{agent.Guid:N}.inv.cache");

            if (!File.Exists(_inventoryCachePath))
            {
                Console.Error.WriteLine("[InvCache] no cache yet -- this session builds one");
                return;
            }

            // -1 on any problem (missing magic, wrong format version, corrupt payload); the store
            // is simply left as the skeleton built it and everything is fetched as before.
            int restored = store.RestoreFromDisk(_inventoryCachePath);
            Console.Error.WriteLine(restored < 0
                ? $"[InvCache] unreadable cache {_inventoryCachePath} -- fetching everything"
                : $"[InvCache] restored {restored} item(s) from {_inventoryCachePath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[InvCache] could not open cache in {directory}: {ex.Message}");
            _inventoryCachePath = null;
        }
    }

    /// <summary>
    /// FEAT-INV-07: writes the inventory cache to disk. Call on quit <b>and</b> on explicit logout
    /// — a clean quit is not the only way a session ends.
    ///
    /// <para>Only worth doing once something has actually been fetched; saving an empty store
    /// would replace a good cache with nothing, so a store still holding nothing but the skeleton
    /// is left alone.</para>
    /// </summary>
    /// <summary>FEAT-INV-07: whether a folder's contents are already local, so reading them costs
    /// nothing. True once the folder has been fetched this session or restored from the cache at a
    /// matching version — the same <c>NeedsUpdate</c> flag
    /// <see cref="FetchInventoryChildrenAsync"/> acts on. Lets a caller tell a free read from one
    /// that will hit the network.</summary>
    public bool IsFolderLocal(Guid folderId)
        => _client.Inventory.Store?.GetNodeOrDefault(new UUID(folderId)) is { NeedsUpdate: false };

    /// <summary>How many folders go into one <c>FetchInventoryDescendents2</c> POST. The CAPS
    /// request takes a list, so this is one round trip for ten folders rather than ten. Matches the
    /// reference viewer's own batch size (llinventorymodelbackgroundfetch.cpp:1092).</summary>
    private const int PrefetchBatchSize = 10;

    /// <summary>Pause between batches. The viewer keeps 12 requests in flight; SLNG deliberately
    /// runs ONE at a time with a gap, because this shares a caps budget with texture and material
    /// fetching and the log already shows the sim's rate limiter filling up under normal load. A
    /// background fill that makes the world load slower has missed the point.</summary>
    private static readonly TimeSpan PrefetchBatchPause = TimeSpan.FromMilliseconds(250);

    /// <summary>Stop after this many batches. A guard against a pathological inventory or a grid
    /// that never clears a folder's flag — at ten folders a batch this still covers 50 000 folders,
    /// far more than any real account has.</summary>
    private const int PrefetchMaxBatches = 5000;

    /// <summary>
    /// FEAT-INV-07 Phase 2: fills the inventory in the background, so searching and browsing find
    /// folders the user never opened by hand.
    ///
    /// <para>Phase 1 made a <i>revisited</i> folder free, but only folders that had been opened at
    /// least once were ever in the cache — and on a first login it is empty. This walks everything
    /// still flagged <c>NeedsUpdate</c> and fetches it, ten folders per request, so by the time the
    /// user opens the inventory the answer is already local. Newly fetched folders reveal their own
    /// subfolders, so the walk repeats until nothing is left.</para>
    ///
    /// <para>Deliberately <b>not</b> LibreMetaverse's <c>RequestFolderContentsAsync(folder, …)</c>
    /// per folder: the CAPS payload takes a list of folders, and one POST for ten is the difference
    /// between a background fill and a flood.</para>
    ///
    /// <para>The agent's own inventory only — the Library is shared, immutable and large, and
    /// nobody searches it for their own things. Cancel via <paramref name="ct"/>; a cancelled or
    /// failed run costs nothing, since anything not fetched simply stays flagged and is fetched on
    /// demand as before.</para>
    /// </summary>
    /// <param name="progress">Called after each batch with (folders fetched so far, folders still
    /// known to be pending). Both numbers move as the walk discovers new subfolders.</param>
    /// <returns>How many folders were fetched.</returns>
    public async Task<int> PrefetchInventoryAsync(
        Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var store = _client.Inventory.Store;
        if (store?.RootNode == null || !_client.Network.Connected) return 0;

        var cap = _client.Network.CurrentSim?.Caps?.CapabilityURI("FetchInventoryDescendents2");
        if (cap == null)
        {
            Console.Error.WriteLine("[InvPrefetch] no FetchInventoryDescendents2 capability -- skipping");
            return 0;
        }

        var agent = _client.Self.AgentID;
        // Folders the grid would not fill in. Without this a folder whose flag never clears would
        // be picked up by every single pass and the walk would never end.
        var refused = new HashSet<UUID>();
        int fetched = 0;

        for (int batchNo = 0; batchNo < PrefetchMaxBatches; batchNo++)
        {
            ct.ThrowIfCancellationRequested();

            var pending = PendingFolders(store, refused);
            if (pending.Count == 0) break;

            var batch = pending.Take(PrefetchBatchSize).ToList();
            try
            {
                await _client.Inventory.RequestFolderContentsAsync(
                    batch, cap, fetchFolders: true, fetchItems: true,
                    LibreMetaverse.InventorySortOrder.ByName, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[InvPrefetch] batch failed, stopping: {ex.Message}");
                break;
            }

            // Whatever the grid did not clear, it is not going to clear on a retry either.
            foreach (var folder in batch)
            {
                if (store.GetNodeOrDefault(folder.UUID) is { NeedsUpdate: false }) fetched++;
                else refused.Add(folder.UUID);
            }

            progress?.Invoke(fetched, PendingFolders(store, refused).Count);
            await Task.Delay(PrefetchBatchPause, ct).ConfigureAwait(false);
        }

        if (refused.Count > 0)
            Console.Error.WriteLine($"[InvPrefetch] {refused.Count} folder(s) the grid would not fill");
        Console.Error.WriteLine($"[InvPrefetch] done, {fetched} folder(s) fetched");
        return fetched;

        // Every folder under the agent's root still flagged as not-yet-fetched. Re-walked each
        // batch on purpose: fetching a folder is how its subfolders become visible in the first
        // place, so the set grows as the walk descends.
        List<LibreMetaverse.InventoryFolder> PendingFolders(
            LibreMetaverse.Inventory inv, HashSet<UUID> skip)
        {
            var result = new List<LibreMetaverse.InventoryFolder>();
            var stack = new Stack<LibreMetaverse.InventoryNode>();
            stack.Push(inv.RootNode);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.Data is LibreMetaverse.InventoryFolder folder &&
                    node.NeedsUpdate && !skip.Contains(folder.UUID))
                {
                    // OwnerID is unset on folders the descendents parser created (only skeleton
                    // folders carry one), and a zero owner makes the grid return nothing — the
                    // same trap FetchInventoryChildrenAsync documents.
                    result.Add(new LibreMetaverse.InventoryFolder(folder.UUID)
                    {
                        OwnerID = folder.OwnerID == UUID.Zero ? agent : folder.OwnerID,
                    });
                }

                // The store is mutated by network threads while we walk it; a snapshot that throws
                // mid-enumeration just means this pass sees less, and the next one picks it up.
                try
                {
                    foreach (var child in node.Nodes.Values.ToList()) stack.Push(child);
                }
                catch (InvalidOperationException) { /* concurrently modified -- try again next pass */ }
            }

            return result;
        }
    }

    public void SaveInventoryCache()
    {
        if (_inventoryCachePath == null) return;

        var store = _client.Inventory.Store;
        if (store?.RootFolder == null) return;

        try
        {
            store.SaveToDisk(_inventoryCachePath);
            var bytes = new FileInfo(_inventoryCachePath).Length;
            Console.Error.WriteLine($"[InvCache] saved {store.Count} node(s), {bytes / 1024} KB");
        }
        catch (Exception ex)
        {
            // Losing a cache costs speed, never correctness.
            Console.Error.WriteLine($"[InvCache] could not save {_inventoryCachePath}: {ex.Message}");
        }
    }

    /// <summary>LibreMetaverse's <c>FindFolderForType</c> logs at ERROR level when the inventory
    /// store does not exist yet ("Inventory is null, FindFolderForType() lookup cannot continue")
    /// and returns <c>UUID.Zero</c>. Before login, and in any unit test that constructs a
    /// GridSession without one, that is the ordinary state rather than a fault -- but it printed
    /// three <c>fail:</c> lines into every CI test run, which is noise that makes a real failure
    /// harder to find. Asking "what is my Trash folder" with no inventory should answer
    /// "unknown", not raise an error.
    ///
    /// <para>Also normalises <c>UUID.Zero</c> to null. Several of these properties documented
    /// themselves as "null until login" while in fact returning <c>Guid.Empty</c>, which is not
    /// the same thing to a caller written against the doc comment.</para></summary>
    private Guid? SystemFolderId(FolderType type)
    {
        if (_client.Inventory?.Store == null) return null;
        var id = _client.Inventory.FindFolderForType(type);
        return id == LibreMetaverse.UUID.Zero ? null : id.Guid;
    }

    /// <summary>Folder id of the Trash folder, or null until login.</summary>
    public Guid? TrashFolderId => SystemFolderId(FolderType.Trash);

    /// <summary>Folder id of the Landmarks system folder, or null until login. Falls back to
    /// the inventory root (LibreMetaverse's own FindFolderForType behavior) if the grid never
    /// sent one — same fallback shape as <see cref="TrashFolderId"/>.</summary>
    public Guid? LandmarksFolderId => SystemFolderId(FolderType.Landmark);

    /// <summary>Folder id of the Current Outfit system folder (COF), or null until login. Its
    /// children are LINK items pointing at whatever's actually worn/attached right now — the
    /// same folder Firestorm's "Worn Items" tab reads, and the fastest way to identify a worn
    /// attachment by name without a dedicated UI (browse to it in the existing inventory tree).</summary>
    public Guid? CurrentOutfitFolderId => SystemFolderId(FolderType.CurrentOutfit);

    /// <summary>The Body Parts folder — where <see cref="CreateTestSkinAsync"/> puts the skin, so the
    /// UI can refresh exactly that folder rather than making the user close and reopen the window to
    /// see an item it was just told about.</summary>
    public Guid? BodyPartsFolderId => SystemFolderId(FolderType.BodyPart);

    /// <summary>The Textures folder — the other destination <see cref="CreateTestSkinAsync"/> writes
    /// to.</summary>
    public Guid? TexturesFolderId => SystemFolderId(FolderType.Texture);

    /// <summary>Folder id of the <c>#Outfits</c> system folder (each direct subfolder is one saved
    /// outfit), or null if the grid doesn't have one / before login. FEAT-INV-04.</summary>
    public Guid? MyOutfitsFolderId => SystemFolderId(FolderType.MyOutfits);

    /// <summary>Checks if a folder is the Landmarks system folder or any descendant subfolder of it.</summary>
    public bool IsInLandmarksSubtree(Guid folderId)
    {
        var store = _client.Inventory.Store;
        if (store == null) return false;
        var landmarkFolderUuid = _client.Inventory.FindFolderForType(FolderType.Landmark);
        if (landmarkFolderUuid == UUID.Zero) return false;

        var folderUuid = new LibreMetaverse.UUID(folderId);
        if (folderUuid == landmarkFolderUuid) return true;

        for (var n = store.GetNodeOrDefault(folderUuid); n != null; n = n.Parent)
        {
            if (n.Data?.UUID == landmarkFolderUuid) return true;
        }
        return false;
    }

    /// <summary>
    /// Fetches one folder's direct children (subfolders + items) — the lazy per-folder expansion
    /// unit for an inventory UI. One CAPS request (FetchInventoryDescendents2 — supported by
    /// modern OpenSim; AIS3 is SL-only and mutation-only in LibreMetaverse anyway) per call, no
    /// recursion: recursing the whole tree hammers the grid and is never needed for a browser.
    /// Returns an empty list before login or when the fetch fails (LibreMetaverse's
    /// FolderContentsAsync falls back to its own cache on failure rather than surfacing an
    /// error — acceptable for a UI tree, where the user just re-expands).
    /// </summary>
    public async Task<IReadOnlyList<InventoryEntry>> FetchInventoryChildrenAsync(
        Guid folderId, CancellationToken ct = default)
    {
        var store = _client.Inventory.Store;
        if (store == null) return Array.Empty<InventoryEntry>();

        var folderUuid = new LibreMetaverse.UUID(folderId);

        // FEAT-INV-07. NeedsUpdate is LibreMetaverse's "these contents are trustworthy" flag: it
        // starts true for every skeleton folder, and only two things clear it — a successful
        // contents fetch this session, or RestoreFromDisk finding the cached folder at the same
        // version the login skeleton just reported. Either way the store already holds the truth,
        // so there is nothing to ask the grid for.
        //
        // This is also why re-expanding a folder is now free: today's code refetched on every
        // expand even though the answer was already in the store.
        var cachedNode = store.GetNodeOrDefault(folderUuid);
        bool servedFromCache = cachedNode is { NeedsUpdate: false };
        // Library folders are owned by the library owner, not the agent — but do NOT trust the
        // stored node's own OwnerID for this: LibreMetaverse's descendents-reply parser creates
        // folders with OwnerID unset (UUID.Zero) — only login-SKELETON folders carry an owner.
        // Passing Zero as the owner makes OpenSim return nothing, which rendered every folder
        // below the root as "(empty)" on the first live test. Membership in the Library subtree
        // (walk the store's parent chain) is the reliable signal.
        var owner = _client.Self.AgentID;
        var libraryRoot = store.LibraryFolder;
        if (libraryRoot != null)
        {
            for (var n = store.GetNodeOrDefault(folderUuid); n != null; n = n.Parent)
            {
                if (n.Data?.UUID != libraryRoot.UUID) continue;
                owner = libraryRoot.OwnerID;
                break;
            }
        }

        var contents = servedFromCache
            ? store.GetContents(folderUuid)
            : await _client.Inventory.FolderContentsAsync(
                folderUuid, owner, fetchFolders: true, fetchItems: true,
                LibreMetaverse.InventorySortOrder.ByName, ct).ConfigureAwait(false);
        if (contents == null) return Array.Empty<InventoryEntry>();

        bool isLandmarksFolder = IsInLandmarksSubtree(folderId);

        var result = new List<InventoryEntry>(contents.Count);
        foreach (var entry in contents)
        {
            switch (entry)
            {
                case LibreMetaverse.InventoryFolder f:
                    result.Add(new InventoryEntry(
                        f.UUID.Guid, f.ParentUUID.Guid, f.OwnerID.Guid, f.Name,
                        IsFolder: true, (int)f.PreferredType,
                        AssetId: Guid.Empty, AssetType: -1, InventoryType: -1,
                        IsLink: false, LinkTargetId: Guid.Empty,
                        CanCopy: true, CanModify: true, CanTransfer: true));
                    break;
                case LibreMetaverse.InventoryItem i:
                    var owned = i.Permissions.OwnerMask;
                    int assetType = (int)i.AssetType;
                    if (assetType <= 0 && (i.InventoryType == LibreMetaverse.InventoryType.Landmark || i is LibreMetaverse.InventoryLandmark || isLandmarksFolder))
                    {
                        assetType = (int)LibreMetaverse.AssetType.Landmark;
                    }
                    result.Add(new InventoryEntry(
                        i.UUID.Guid, i.ParentUUID.Guid, i.OwnerID.Guid, i.Name,
                        IsFolder: false, PreferredFolderType: -1,
                        // For links the "asset" id actually points at the linked inventory item —
                        // report it as the link target and leave AssetId empty (resolving the
                        // target's real asset takes a second fetch the UI doesn't need yet).
                        AssetId: i.ResolvedAssetID.Guid,
                        assetType, (int)i.InventoryType,
                        i.IsLink(), i.IsLink() ? i.ResolvedItemID.Guid : Guid.Empty,
                        owned.HasFlag(LibreMetaverse.PermissionMask.Copy),
                        owned.HasFlag(LibreMetaverse.PermissionMask.Modify),
                        owned.HasFlag(LibreMetaverse.PermissionMask.Transfer)));
                    break;
            }
        }

        // Deduplicate entries under Current Outfit (COF) if multiple links/items point to the same target
        if (folderUuid == _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit))
        {
            var seenTargets = new HashSet<Guid>();
            var deduped = new List<InventoryEntry>(result.Count);
            foreach (var item in result)
            {
                var target = item.IsLink ? item.LinkTargetId : item.Id;
                if (target != Guid.Empty && !seenTargets.Add(target))
                {
                    continue; // Skip duplicate link/item pointing to same target
                }
                deduped.Add(item);
            }
            result = deduped;
        }

        return result;
    }

    /// <summary>
    /// Moves an inventory item (or folder) to the Trash folder — the viewer's Delete.
    ///
    /// <para><b>Sends the legacy UDP message on purpose, and does not call
    /// <c>InventoryManager.MoveItem</c>/<c>MoveFolder</c>.</b> Those prefer AIS whenever it is
    /// available and PATCH <c>{cap}/item/{id}</c> with a bare <c>parent_id</c>, which Second Life
    /// answers with <b>HTTP 400</b> — so on SL every Delete silently failed and the item stayed put
    /// (reported live 2026-09-14: „Item löschen geht nicht", and a declined gift that stayed in
    /// Objects). The real viewer never moves through AIS either: a reparent is
    /// <c>MoveInventoryItem</c> (llviewerinventory.cpp:566-579) or <c>MoveInventoryFolder</c>
    /// (:647-660) over UDP, on every grid, to this day — AIS is used for item *content* updates
    /// (<c>AISAPI::UpdateItem</c>) and for real deletion, not for reparenting. The UDP message is
    /// equally correct on OpenSim, so there is nothing to branch on.</para>
    ///
    /// <para>LibreMetaverse's store is updated here too, exactly as its own <c>MoveItem</c> would,
    /// so a UI reading the store does not keep drawing the item under its old parent.</para>
    /// </summary>
    public Task MoveToTrashAsync(Guid itemId, bool isFolder)
    {
        if (TrashFolderId is not { } trashId) return Task.CompletedTask;
        return MoveInventoryAsync(itemId, trashId, isFolder, "Trash");
    }

    /// <summary>
    /// Moves an item or folder into another folder. FEAT-INV-08.
    /// </summary>
    /// <param name="destinationLabel">What to call the destination in the log line; the folder's
    /// own name where the caller knows it.</param>
    /// <remarks>
    /// The same legacy UDP reparent <see cref="MoveToTrashAsync"/> sends, which is that method's
    /// whole point and is documented there: <c>InventoryManager.MoveItem</c>/<c>MoveFolder</c>
    /// prefer AIS whenever it is available and PATCH <c>{cap}/item/{id}</c> with a bare
    /// <c>parent_id</c>, which Second Life answers with <b>HTTP 400</b>. Every move made that way
    /// failed silently and the thing stayed where it was. Trash was simply the first destination
    /// this project needed; nothing about the mechanism is specific to it.
    /// </remarks>
    public Task MoveInventoryAsync(Guid itemId, Guid newParentId, bool isFolder, string destinationLabel = "")
    {
        if (itemId == Guid.Empty || newParentId == Guid.Empty) return Task.CompletedTask;
        if (itemId == newParentId) return Task.CompletedTask; // a folder cannot contain itself
        if (!_client.Network.Connected) return Task.CompletedTask;

        var id = new UUID(itemId);
        var trash = new UUID(newParentId);

        // Keep the local store in step with what we are about to tell the grid.
        if (_client.Inventory.Store?.GetNodeOrDefault(id)?.Data is { } node)
        {
            node.ParentUUID = trash;
            _client.Inventory.Store.UpdateNodeFor(node);
        }

        if (isFolder)
        {
            var move = new MoveInventoryFolderPacket
            {
                AgentData = { AgentID = _client.Self.AgentID, SessionID = _client.Self.SessionID, Stamp = false },
                InventoryData = new[]
                {
                    new MoveInventoryFolderPacket.InventoryDataBlock { FolderID = id, ParentID = trash },
                },
            };
            _client.Network.SendPacket(move);
        }
        else
        {
            var move = new MoveInventoryItemPacket
            {
                AgentData = { AgentID = _client.Self.AgentID, SessionID = _client.Self.SessionID, Stamp = false },
                InventoryData = new[]
                {
                    // Empty NewName = "keep the name", the same as the viewer's addString("NewName", NULL).
                    new MoveInventoryItemPacket.InventoryDataBlock
                    {
                        ItemID = id, FolderID = trash, NewName = Utils.EmptyBytes,
                    },
                },
            };
            _client.Network.SendPacket(move);
        }

        // UDP is fire-and-forget: unlike the AIS call this replaces, a failure here is silent, so
        // say what was sent. This line is how the 400 that made Delete a no-op was found.
        Console.Error.WriteLine(
            $"[Inventory] MoveInventory{(isFolder ? "Folder" : "Item")} {id} -> " +
            $"{(string.IsNullOrEmpty(destinationLabel) ? "folder" : destinationLabel)} {trash}");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gives an inventory item to another agent (IM inventory offer).
    /// </summary>
    public Task GiveItemAsync(Guid itemId, string itemName, int assetType, Guid recipientAgentId)
    {
        _client.Inventory.GiveItem(
            new LibreMetaverse.UUID(itemId),
            itemName,
            (LibreMetaverse.AssetType)assetType,
            new LibreMetaverse.UUID(recipientAgentId),
            doEffect: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gives an inventory folder to another agent (IM inventory offer).
    /// </summary>
    public Task GiveFolderAsync(Guid folderId, string folderName, Guid recipientAgentId)
    {
        _client.Inventory.GiveItem(
            new LibreMetaverse.UUID(folderId),
            folderName,
            LibreMetaverse.AssetType.Folder,
            new LibreMetaverse.UUID(recipientAgentId),
            doEffect: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Copies an inventory item to a new parent folder.
    /// </summary>
    public async Task CopyItemAsync(Guid itemId, Guid newParentId, string newName)
    {
        var itemUuid = new LibreMetaverse.UUID(itemId);
        var parentUuid = new LibreMetaverse.UUID(newParentId);

        await _client.Inventory.RequestCopyItemAsync(itemUuid, parentUuid, newName, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Wears an inventory item. An attachment/object is attached via <c>Attach</c>. A system
    /// wearable (Clothing/Bodypart layer) is added via <c>AppearanceManager.AddToOutfit</c> — on an
    /// SL server-side-baking region straight through, otherwise only after every currently-worn
    /// wearable is decoded so the rebake can't persist a default shape (FEAT-AVATAR-01); if that
    /// preparation fails the wear is refused with <see cref="WearableEditUnavailable"/>. Handles
    /// item IDs and links.
    /// </summary>
    public async Task AttachItemAsync(Guid itemId, byte attachPoint = 0, bool replace = false)
    {
        var itemUuid = new LibreMetaverse.UUID(itemId);
        var store = _client.Inventory.Store;
        var itemNode = store?.GetNodeOrDefault(itemUuid);

        if (itemNode?.Data is LibreMetaverse.InventoryItem item && item.IsLink())
        {
            itemUuid = item.ResolvedItemID;
            itemNode = store?.GetNodeOrDefault(itemUuid);
        }

        if (itemNode?.Data is LibreMetaverse.InventoryItem realItem)
        {
            // FEAT-AVATAR-01: a system wearable is not an attachment. AddToOutfit → RequestSetAppearance
            // triggers a rebake; MakeAppearancePacket rebuilds the 218 visual params from the DECODED
            // worn wearables, falling back to vp.DefaultValue for any it can't decode — which is how
            // the 2026-08-02 / 2026-08-29 flatten happened. PrepareWearableEditAsync makes that safe
            // (SL SSB: server composes, nothing sent; else: decode every worn wearable first).
            if (ClassifyItem(realItem is LibreMetaverse.InventoryWearable, (int)realItem.AssetType)
                == WearableKind.Wearable)
            {
                await WearWearableAsync(realItem, replace).ConfigureAwait(false);
                return;
            }

            // A #Library item can't be linked into an outfit (you don't own it). Attach the OWNED
            // COPY instead of the Library original, so the scene attachment id, the COF link and
            // any outfit link all point at the same item — otherwise the worn marker never matches
            // ("Schuhe angezogen, im Outfit stehen sie als nicht getragen"). The copy is content-
            // identical and reused across wears (CopyLibraryItemForOutfitAsync dedups by AssetUUID).
            if (IsUnderLibrary(realItem.UUID))
            {
                var owned = await CopyLibraryItemForOutfitAsync(realItem).ConfigureAwait(false);
                if (owned != null) realItem = owned;
            }

            _client.Appearance.Attach(realItem, (LibreMetaverse.AttachmentPoint)attachPoint, replace);
            // LibreMetaverse's Attach only sends RezSingleAttachmentFromInv — it never records the
            // item in the Current Outfit folder. Without a COF link the attachment is on the avatar
            // this session only: SL's server-side bake recomposites from the COF on the next relog,
            // and every outfit-save slams COF links, so a worn-but-unlinked attachment silently
            // vanishes on relog and is missing from any outfit saved while it was on.
            // WearWearableAsync already writes this link for system layers.
            await EnsureCofLinkForItemAsync(realItem, LibreMetaverse.InventoryType.Object).ConfigureAwait(false);
            WornItemsChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _client.Appearance.Attach(
                itemUuid,
                _client.Self.AgentID,
                "Attachment",
                "",
                new LibreMetaverse.Permissions { OwnerMask = LibreMetaverse.PermissionMask.All },
                0,
                (LibreMetaverse.AttachmentPoint)attachPoint,
                replace);
        }
    }

    /// <summary>Creates a Current-Outfit link for <paramref name="item"/> unless one already
    /// exists. See the call in <see cref="AttachItemAsync"/> for why an attachment needs this
    /// explicitly — no bake or appearance send is triggered, this is an inventory link only.</summary>
    private async Task EnsureCofLinkForItemAsync(LibreMetaverse.InventoryItem item, LibreMetaverse.InventoryType invType)
    {
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        if (cofUuid == LibreMetaverse.UUID.Zero) return;

        var target = await ResolveCofLinkTargetAsync(item).ConfigureAwait(false);
        if (target == null) return;
        if (FindCofLinkTo(target.UUID) != null) return; // already recorded

        try
        {
            await CreateCofLinkAsync(cofUuid, target, string.Empty, invType).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Appearance] could not COF-link '{target.Name}': {ex.Message}");
        }
    }

    /// <summary>Resolves an inventory item to the one a Current-Outfit link may legally point at,
    /// or <c>null</c> — with the reason logged — when there is none.
    ///
    /// <para>Two different things make AIS refuse a link, and it answers both with the same bare
    /// <c>Create inventory in &lt;folder&gt;: Bad Request</c>: a <c>linked_id</c> that is itself a
    /// link (link-to-link is illegal, and one hop is not always enough — the pairs BUG-INV-01 kept
    /// hitting), and a target that is not in this agent's inventory. The second is the
    /// <c>#Library</c> case: a Linden-owned starter item wears fine but cannot be linked, so the
    /// reference viewer copies it into your inventory first and links the copy
    /// (<c>LLAppearanceMgr::wearItemsOnAvatar</c>).</para>
    ///
    /// <para>Shared by the attachment path and the wearable path. It was written for the first and
    /// for a year applied only there, which is one half of why wearing a skin from the inventory
    /// could 400 while the identical item worn from an outfit did not.</para></summary>
    private async Task<LibreMetaverse.InventoryItem?> ResolveCofLinkTargetAsync(LibreMetaverse.InventoryItem item)
    {
        var store = _client.Inventory.Store;

        // Walk to the real item: a link's linked_id must be a base item, never another link.
        var target = item;
        for (int hop = 0; hop < 4 && target.IsLink(); hop++)
        {
            var next = target.ResolvedItemID != LibreMetaverse.UUID.Zero ? target.ResolvedItemID : target.AssetUUID;
            if (next == LibreMetaverse.UUID.Zero) break;
            if (store?.GetNodeOrDefault(next)?.Data is not LibreMetaverse.InventoryItem resolved) { target = null!; break; }
            target = resolved;
        }
        if (target is null || target.UUID == LibreMetaverse.UUID.Zero || target.IsLink())
        {
            Console.Error.WriteLine(
                $"[Appearance] not COF-linking '{item.Name}' ({item.UUID}) — does not resolve to a real inventory item " +
                $"(isLink={item.IsLink()} resolvedItemId={item.ResolvedItemID} assetUuid={item.AssetUUID})");
            return null;
        }

        if (IsUnderLibrary(target.UUID))
        {
            var owned = await CopyLibraryItemForOutfitAsync(target).ConfigureAwait(false);
            if (owned is null)
            {
                Console.Error.WriteLine(
                    $"[Appearance] '{target.Name}' is a Library item and could not be copied into your inventory — " +
                    "it cannot be recorded in your outfit");
                return null;
            }
            target = owned;
        }

        return target;
    }

    /// <summary>Writes one Current-Outfit link and reports the truth about it.
    ///
    /// <para><c>CreateLinkAsync</c> signals an AIS refusal by returning <c>null</c> — it does not
    /// throw, and the only other trace is LibreMetaverse's own
    /// <c>warn: Create inventory in &lt;folder&gt;: Bad Request</c>. A caller that ignores the
    /// return therefore reports a wear as successful while the folder the server bakes from never
    /// received the item. Returns the created link, or null after logging everything known about
    /// why it was refused.</para></summary>
    private async Task<LibreMetaverse.InventoryItem?> CreateCofLinkAsync(
        LibreMetaverse.UUID cofUuid, LibreMetaverse.InventoryItem target, string description,
        LibreMetaverse.InventoryType invType)
    {
        var created = await _client.Inventory.CreateLinkAsync(
            cofUuid, target.UUID, target.Name, description, invType, LibreMetaverse.UUID.Zero).ConfigureAwait(false);
        if (created != null) return created;

        if (target.OwnerID != LibreMetaverse.UUID.Zero && target.OwnerID != _client.Self.AgentID)
        {
            Console.Error.WriteLine(
                $"[Appearance] '{target.Name}' ({target.UUID}) was not added to your outfit — the grid says it is " +
                $"owned by {target.OwnerID}, not you, so it is not in your inventory (worn from a shared/demo source?)");
            return null;
        }

        var store = _client.Inventory.Store;
        string where = "?";
        for (var n = store?.GetNodeOrDefault(target.UUID); n != null; n = n.Parent)
            if (n.Data is LibreMetaverse.InventoryFolder pf)
            { where = pf.PreferredType != LibreMetaverse.FolderType.None ? pf.PreferredType.ToString() : pf.Name; break; }

        Console.Error.WriteLine(
            $"[Appearance] AIS refused COF link for '{target.Name}' ({target.UUID}): " +
            $"assetType={target.AssetType} invType={target.InventoryType} isLink={target.IsLink()} " +
            $"owner={target.OwnerID} mine={target.OwnerID == _client.Self.AgentID} " +
            $"perms={target.Permissions.OwnerMask} parentFolder={where}");
        return null;
    }

    /// <summary>The Current-Outfit link pointing at <paramref name="targetId"/>, or null. Used to
    /// keep a wear idempotent: re-wearing something already on must not pile up duplicate links,
    /// which <see cref="CollectWornWearablesFromCof"/> would then report to the simulator twice.</summary>
    private LibreMetaverse.InventoryItem? FindCofLinkTo(LibreMetaverse.UUID targetId)
    {
        if (targetId == LibreMetaverse.UUID.Zero) return null;

        var store = _client.Inventory.Store;
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        var cofNode = cofUuid != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cofUuid) : null;
        if (cofNode == null) return null;

        foreach (var child in cofNode.Nodes.Values.ToList())
        {
            if (child.Data is not LibreMetaverse.InventoryItem link || !link.IsLink()) continue;
            var t = link.ResolvedItemID != LibreMetaverse.UUID.Zero ? link.ResolvedItemID : link.AssetUUID;
            if (t == targetId) return link;
        }
        return null;
    }

    /// <summary>True when <paramref name="itemId"/>'s node sits anywhere under the store's
    /// <c>#Library</c> root — a Linden-owned item that this agent can wear but not link or
    /// modify.</summary>
    private bool IsUnderLibrary(LibreMetaverse.UUID itemId)
    {
        var store = _client.Inventory.Store;
        var libRoot = store?.LibraryFolder;
        if (libRoot == null) return false;
        for (var n = store!.GetNodeOrDefault(itemId); n != null; n = n.Parent)
            if (n.Data?.UUID == libRoot.UUID) return true;
        return false;
    }

    // Library-item-id -> the owned copy we made this session, so re-wearing the same starter
    // item never copies twice. Cross-session dedup is the AssetUUID scan in the method below.
    private readonly Dictionary<LibreMetaverse.UUID, LibreMetaverse.UUID> _libraryCopyCache = new();

    /// <summary>Copies a <c>#Library</c> item into the agent's own inventory so it can be linked
    /// into an outfit. Never makes a second copy of the same starter item: a session cache
    /// short-circuits a re-wear, and otherwise the whole owned inventory is scanned for an
    /// existing copy of the <b>same asset</b> (<c>AssetUUID</c>, which a copy shares with its
    /// original). Returns the owned copy, or null if the copy failed.</summary>
    private async Task<LibreMetaverse.InventoryItem?> CopyLibraryItemForOutfitAsync(LibreMetaverse.InventoryItem libItem)
    {
        var store = _client.Inventory.Store;
        if (store == null) return null;

        // 1. Already copied this session?
        if (_libraryCopyCache.TryGetValue(libItem.UUID, out var cachedId)
            && store.GetNodeOrDefault(cachedId)?.Data is LibreMetaverse.InventoryItem cached && !cached.IsLink())
            return cached;

        // 2. A copy from an earlier session? A copy shares the original's AssetUUID.
        if (libItem.AssetUUID != LibreMetaverse.UUID.Zero && store.RootFolder != null)
        {
            var stack = new Stack<LibreMetaverse.UUID>();
            stack.Push(store.RootFolder.UUID);
            while (stack.Count > 0)
            {
                var node = store.GetNodeOrDefault(stack.Pop());
                if (node == null) continue;
                foreach (var child in node.Nodes.Values)
                {
                    switch (child.Data)
                    {
                        case LibreMetaverse.InventoryFolder:
                            stack.Push(child.Data.UUID);
                            break;
                        case LibreMetaverse.InventoryItem c when !c.IsLink()
                            && c.OwnerID == _client.Self.AgentID
                            && c.AssetUUID == libItem.AssetUUID
                            && c.AssetType == libItem.AssetType:
                            _libraryCopyCache[libItem.UUID] = c.UUID;
                            return c;
                    }
                }
            }
        }

        // 3. Make the copy — into the system folder for the item's asset type.
        var destType = libItem.AssetType switch
        {
            LibreMetaverse.AssetType.Bodypart => LibreMetaverse.FolderType.BodyPart,
            LibreMetaverse.AssetType.Clothing => LibreMetaverse.FolderType.Clothing,
            _ => LibreMetaverse.FolderType.Object,
        };
        var dest = _client.Inventory.FindFolderForType(destType);
        if (dest == LibreMetaverse.UUID.Zero) dest = store.RootFolder?.UUID ?? LibreMetaverse.UUID.Zero;
        if (dest == LibreMetaverse.UUID.Zero) return null;

        try
        {
            var copied = await _client.Inventory.RequestCopyItemAsync(
                libItem.UUID, dest, libItem.Name, libItem.OwnerID, CancellationToken.None).ConfigureAwait(false);
            if (copied is LibreMetaverse.InventoryItem ci)
            {
                Console.Error.WriteLine($"[Appearance] copied Library item '{libItem.Name}' into your inventory ({ci.UUID})");
                _libraryCopyCache[libItem.UUID] = ci.UUID;
                return ci;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Appearance] copy of Library item '{libItem.Name}' failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Takes an inventory item off the agent. An attachment is removed via
    /// <c>DetachAttachmentIntoInv</c> (plus stale-COF-link cleanup). A system wearable
    /// (Clothing/Bodypart layer) is removed via <c>AppearanceManager.RemoveFromOutfit</c> — on an
    /// SL server-side-baking region straight through, otherwise only after every currently-worn
    /// wearable is decoded so the rebake can't persist a default shape (FEAT-AVATAR-01); if that
    /// preparation fails the detach is refused with <see cref="WearableEditUnavailable"/>. Handles
    /// item IDs and links inside Current Outfit.
    /// </summary>
    public Task<DetachResult> DetachItemAsync(Guid itemId)
    {
        var itemUuid = new LibreMetaverse.UUID(itemId);
        var store = _client.Inventory.Store;
        var node = store?.GetNodeOrDefault(itemUuid);

        // FEAT-AVATAR-01: a system wearable is not an attachment (the sim ignores
        // DetachAttachmentIntoInv for a Clothing/Bodypart layer). RemoveFromOutfit → RequestSetAppearance
        // triggers a rebake; see WearWearableAsync's comment for why it needs preparation.
        {
            var real = node?.Data as LibreMetaverse.InventoryItem;
            if (real is { } && real.IsLink())
                real = store?.GetNodeOrDefault(real.ResolvedItemID)?.Data as LibreMetaverse.InventoryItem ?? real;
            if (real is { }
                && ClassifyItem(real is LibreMetaverse.InventoryWearable, (int)real.AssetType) == WearableKind.Wearable)
            {
                return RemoveWearableAsync(real);
            }
        }

        var uuidsToDetach = new HashSet<LibreMetaverse.UUID>();
        if (itemUuid != LibreMetaverse.UUID.Zero) uuidsToDetach.Add(itemUuid);

        if (node?.Data is LibreMetaverse.InventoryItem item && item.IsLink())
        {
            if (item.ResolvedItemID != LibreMetaverse.UUID.Zero) uuidsToDetach.Add(item.ResolvedItemID);
            if (item.AssetUUID != LibreMetaverse.UUID.Zero) uuidsToDetach.Add(item.AssetUUID);
        }

        // Search Current Outfit folder to find matching links or items
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        if (cofUuid != LibreMetaverse.UUID.Zero)
        {
            var cofNode = store?.GetNodeOrDefault(cofUuid);
            if (cofNode != null)
            {
                foreach (var childNode in cofNode.Nodes.Values)
                {
                    if (childNode.Data is LibreMetaverse.InventoryItem cofItem)
                    {
                        var linkId = cofItem.UUID;
                        var targetId = cofItem.IsLink() ? (cofItem.ResolvedItemID != LibreMetaverse.UUID.Zero ? cofItem.ResolvedItemID : cofItem.AssetUUID) : cofItem.UUID;

                        if (uuidsToDetach.Contains(linkId) || uuidsToDetach.Contains(targetId) || linkId == itemUuid || targetId == itemUuid)
                        {
                            if (linkId != LibreMetaverse.UUID.Zero) uuidsToDetach.Add(linkId);
                            if (targetId != LibreMetaverse.UUID.Zero) uuidsToDetach.Add(targetId);
                        }
                    }
                }
            }
        }

        // Query active attachments from AppearanceManager ONLY for matching candidate UUIDs.
        // wasAttached is the load-bearing bit: DetachAttachmentIntoInv is matched server-side
        // against LIVE attachments, so when nothing here is actually attached every packet below
        // is a silent no-op -- see the stale-link cleanup at the end of this method.
        bool wasAttached = false;
        try
        {
            var activeAtts = _client.Appearance.GetAttachmentsByItemId();
            foreach (var kvp in activeAtts)
            {
                if (uuidsToDetach.Contains(kvp.Key))
                {
                    uuidsToDetach.Add(kvp.Key);
                    wasAttached = true;
                }
            }
        }
        catch { }

        // LLVOAvatarSelf::detachObject: stop motions from candidate source IDs
        var candidateSourceIds = new HashSet<Guid>();
        if (itemId != Guid.Empty) candidateSourceIds.Add(itemId);
        foreach (var u in uuidsToDetach)
        {
            if (u != LibreMetaverse.UUID.Zero) candidateSourceIds.Add(u.Guid);
        }

        var sim = _client.Network.CurrentSim;
        if (sim != null)
        {
            foreach (var p in sim.ObjectsPrimitives.Values)
            {
                if (p == null || p.ParentID != _client.Self.LocalID) continue;
                var attId = ExtractAttachItemId(p);
                if (candidateSourceIds.Contains(p.ID.Guid) || (attId != Guid.Empty && candidateSourceIds.Contains(attId)))
                {
                    candidateSourceIds.Add(p.ID.Guid);
                    foreach (var child in sim.ObjectsPrimitives.Values)
                    {
                        if (child != null && child.ParentID == p.LocalID)
                        {
                            candidateSourceIds.Add(child.ID.Guid);
                        }
                    }
                }
            }
        }

        StopMotionsFromSources(candidateSourceIds);

        // Send DetachAttachmentIntoInv packet for every candidate UUID
        foreach (var u in uuidsToDetach)
        {
            if (u != LibreMetaverse.UUID.Zero)
            {
                _client.Appearance.Detach(u);
            }
        }

        // Clean up stale link nodes from local Store under COF
        int staleLinksRemoved = 0;
        try
        {
            if (cofUuid != LibreMetaverse.UUID.Zero)
            {
                var cofNode = store?.GetNodeOrDefault(cofUuid);
                if (cofNode != null)
                {
                    var staleKeys = new List<LibreMetaverse.UUID>();
                    foreach (var childNode in cofNode.Nodes.Values)
                    {
                        if (childNode.Data is LibreMetaverse.InventoryItem cofItem)
                        {
                            var target = cofItem.IsLink() ? (cofItem.ResolvedItemID != LibreMetaverse.UUID.Zero ? cofItem.ResolvedItemID : cofItem.AssetUUID) : cofItem.UUID;
                            if (uuidsToDetach.Contains(cofItem.UUID) || uuidsToDetach.Contains(target))
                            {
                                staleKeys.Add(cofItem.UUID);
                            }
                        }
                    }

                    // Server-side half. Dropping the node from the local Store alone (below) only
                    // hides the row until the next fetch re-reads the folder from the sim, which
                    // is exactly the "Detach does nothing" report: the item was never attached, so
                    // the DetachAttachmentIntoInv packets above matched nothing, and the COF link
                    // that made it LOOK worn survived every refresh.
                    //
                    // FEAT-INV-03: this now runs for a real attachment too. It used to be gated on
                    // !wasAttached, trusting the sim to drop the link as part of the detach -- but
                    // OpenSim does not do that reliably, so the link survived and the item was
                    // re-worn on the next login.
                    //
                    // DELETE, not move-to-Trash. MoveInventoryItem on a Current-Outfit link gets
                    // HTTP 400 from AIS on SL ("Move item … to <Trash>: Bad Request") -- the COF
                    // handler rejects the move -- so the link never left and the item stayed worn
                    // across logins ("Ablegen geht nicht persistent", live 2026-09-03). staleKeys
                    // are the links for the one item the user explicitly chose to take off, so a
                    // durable delete is well-targeted; the linked inventory item is untouched.
                    var toDelete = staleKeys.Where(k => k != LibreMetaverse.UUID.Zero).ToList();
                    if (toDelete.Count > 0)
                    {
                        try
                        {
                            _ = _client.Inventory.RemoveItemsAsync(toDelete, System.Threading.CancellationToken.None);
                            staleLinksRemoved = toDelete.Count;
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[Detach] RemoveItemsAsync threw: {ex.Message}");
                        }
                    }

                    foreach (var k in staleKeys)
                    {
                        cofNode.Nodes.Remove(k);
                    }
                }
            }
        }
        catch { }

        return Task.FromResult(new DetachResult(wasAttached, staleLinksRemoved));
    }

    /// <summary>Detaches whatever attachment is the given scene-local object id, via ObjectDetach
    /// (by localId). Unlike <see cref="DetachItemAsync"/> (DetachAttachmentIntoInv, which the sim
    /// matches on the attachment's AttachItemID name-value) this works even when that name-value
    /// is missing or stale — the case where an inventory "Detach" silently does nothing.
    ///
    /// <para>Taking the object off the avatar is only half of it. What makes an item WORN is its
    /// link in the Current Outfit folder, and ObjectDetach does not touch that: the object left
    /// the avatar, the link stayed, and the next login put the item back on — reported in-world
    /// as "detach works until I log in again". The viewer's own Detach is
    /// <c>LLAppearanceMgr::removeItemFromAvatar</c>, which does both. So does this now, by way of
    /// <see cref="DetachItemAsync"/>, which already owns the link cleanup (including the reason it
    /// must be a DELETE rather than a move to Trash).</para>
    ///
    /// <para>Only when the object names an inventory item. Without one there is no link to find,
    /// and the object is off the avatar either way.</para>
    /// </summary>
    public void DetachByLocalId(uint localId)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null || localId == 0) return;

        var candidateSourceIds = new HashSet<Guid>();
        if (sim.ObjectsPrimitives.TryGetValue(localId, out var rootPrim) && rootPrim != null)
        {
            candidateSourceIds.Add(rootPrim.ID.Guid);
            var attId = ExtractAttachItemId(rootPrim);
            if (attId != Guid.Empty) candidateSourceIds.Add(attId);

            foreach (var child in sim.ObjectsPrimitives.Values)
            {
                if (child != null && child.ParentID == rootPrim.LocalID)
                {
                    candidateSourceIds.Add(child.ID.Guid);
                }
            }
        }
        StopMotionsFromSources(candidateSourceIds);

        _client.Objects.DetachObjects(sim, new List<uint> { localId });

        var attachItemId = rootPrim != null ? ExtractAttachItemId(rootPrim) : Guid.Empty;
        if (attachItemId != Guid.Empty) _ = DetachItemAsync(attachItemId);
    }

    /// <summary>Escape hatch for a stuck attachment that can't be pinned down in inventory:
    /// ObjectDetach every worn attachment (optionally only the HUD-point ones) by localId.
    /// Returns how many were sent.</summary>
    public int DetachAllAttachments(bool hudOnly = false)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null) return 0;

        var ids = new List<uint>();
        var itemIds = new List<Guid>();
        var candidateSourceIds = new HashSet<Guid>();
        var report = new List<(uint LocalId, LibreMetaverse.AttachmentPoint Point, string Name)>();
        foreach (var p in sim.ObjectsPrimitives.Values)
        {
            // Only root prims of an attachment carry ParentID == our avatar; child prims hang off
            // the attachment root and ObjectDetach on the root takes the whole linkset.
            if (p == null || p.ParentID != _client.Self.LocalID) continue;
            var ap = p.PrimData.AttachmentPoint;
            if (ap == LibreMetaverse.AttachmentPoint.Default) continue;
            bool isHud = (int)ap >= 31 && (int)ap <= 38; // HUDCenter2 .. HUDBottomRight
            if (hudOnly && !isHud) continue;
            ids.Add(p.LocalID);
            report.Add((p.LocalID, ap, p.Properties?.Name ?? ""));

            // FEAT-INV-03: the inventory item id travels in the AttachItemID name-value -- the
            // same field the sim matches DetachAttachmentIntoInv on. Needed to trash the COF link
            // so "Detach All" survives a relog.
            var attId = ExtractAttachItemId(p);
            if (attId != Guid.Empty)
            {
                itemIds.Add(attId);
                candidateSourceIds.Add(attId);
            }
            candidateSourceIds.Add(p.ID.Guid);
            foreach (var child in sim.ObjectsPrimitives.Values)
            {
                if (child != null && child.ParentID == p.LocalID)
                {
                    candidateSourceIds.Add(child.ID.Guid);
                }
            }
        }

        // Named, not just counted: "0 detached" and "3 detached but one is still on screen" are
        // the two outcomes this escape hatch has to be able to tell apart afterwards.
        foreach (var (localId, point, name) in report)
            Console.Error.WriteLine($"[Detach] localId={localId} point={point} \"{name}\"");

        StopMotionsFromSources(candidateSourceIds);

        if (ids.Count > 0) _client.Objects.DetachObjects(sim, ids);

        int linksRemoved = RemoveOutfitLinksForItems(itemIds);
        if (linksRemoved > 0)
            Console.Error.WriteLine($"[Detach] moved {linksRemoved} Current-Outfit link(s) to Trash");

        return ids.Count;
    }

    /// <summary>Pulls the <c>AttachItemID</c> name-value (the inventory item id) off an attachment
    /// prim, or <see cref="Guid.Empty"/> if it isn't present / parseable. FEAT-INV-03.</summary>
    private static Guid ExtractAttachItemId(Primitive p)
    {
        try
        {
            if (p.NameValues == null) return Guid.Empty;
            foreach (var nv in p.NameValues)
            {
                if (nv.Name != "AttachItemID") continue;
                if (LibreMetaverse.UUID.TryParse(nv.Value?.ToString() ?? string.Empty, out var u))
                    return u.Guid;
            }
        }
        catch { }
        return Guid.Empty;
    }

    /// <summary>The avatar's attachments as they exist in the SCENE right now — prims parented to
    /// the local agent, keyed by inventory item id (<c>AttachItemID</c>), valued by attachment
    /// point. This is the authoritative "worn right now" for attachments;
    /// <c>AppearanceManager.GetAttachmentsByItemId()</c> is a cache that keeps listing an item for
    /// a while after it's detached (see the project memory). FEAT-INV-03 / FEAT-INV-04.</summary>
    private Dictionary<Guid, LibreMetaverse.AttachmentPoint> GetSceneWornAttachments()
    {
        var map = new Dictionary<Guid, LibreMetaverse.AttachmentPoint>();
        try
        {
            var sim = _client.Network.CurrentSim;
            if (sim == null) return map;
            foreach (var p in sim.ObjectsPrimitives.Values)
            {
                if (p == null || p.ParentID != _client.Self.LocalID) continue;
                var pt = p.PrimData.AttachmentPoint;
                if (pt == LibreMetaverse.AttachmentPoint.Default) continue;
                var aid = ExtractAttachItemId(p);
                if (aid != Guid.Empty) { map[aid] = pt; _attachmentsSeenWornThisSession.Add(aid); }
            }
        }
        catch { }
        return map;
    }

    // Every attachment id we have seen parented to our avatar this session. Lets the login
    // re-attach pass (ReattachMissingCofAttachments) leave alone anything the user took off --
    // that was seen worn first -- and only re-request items that never rezzed at all.
    private readonly HashSet<Guid> _attachmentsSeenWornThisSession = new();

    private int _attachmentReconcileArmed;

    /// <summary>When false, the login Current-Outfit attachment reconcile never runs — nothing is
    /// attached on this client's own initiative. Set from the <c>--no-reattach</c> command-line
    /// flag; defaults to the normal behaviour. See <see cref="ArmAttachmentReconcile"/>.</summary>
    public bool ReattachMissingAttachments { get; set; } = true;

    /// <summary>The simulator sometimes fails to rez one or two Current-Outfit attachments on
    /// login (a COF/asset race, worse for freshly-made <c>#Library</c> copies): the item is in the
    /// COF but never appears in-world, so the Angezogen tab shows it "(nicht aktiv)". The
    /// reference viewer's <c>LLAttachmentsMgr</c> re-requests missing attachments; this does the
    /// same a few times after login. Bounded, and it skips anything ever seen worn this session so
    /// it never re-adds something the user deliberately took off.</summary>
    private void ArmAttachmentReconcile()
    {
        if (System.Threading.Interlocked.Exchange(ref _attachmentReconcileArmed, 1) != 0) return;

        // BUG-AVATAR-07: this pass is the only thing the client does to a live avatar's outfit on
        // its own initiative, and it is therefore the only thing that can make the avatar look
        // different in OTHER people's viewers — re-attaching restarts the object's scripts (an AO,
        // an ankle lock, a mesh body's own scripts), which everyone sees, not just us. The switch
        // exists so that can be A/B tested against a second viewer instead of argued about.
        if (!ReattachMissingAttachments)
        {
            Console.Error.WriteLine(
                "[Appearance] login attachment reconcile DISABLED (--no-reattach) — nothing will be " +
                "re-attached; a Current-Outfit item the sim failed to rez stays missing this session");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // First check soon so a missing attachment pops in fast, not 20 s later; the
                // later passes cover a slow COF load or a sim that is still settling.
                int[] schedule = { 6, 12, 22, 45, 80 };
                int totalReattached = 0;
                for (int i = 0; i < schedule.Length; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(i == 0 ? schedule[0] : schedule[i] - schedule[i - 1]))
                        .ConfigureAwait(false);
                    if (!_client.Network.Connected) return;
                    int sent = await ReattachMissingCofAttachmentsAsync().ConfigureAwait(false);
                    totalReattached += sent;
                    if (sent == 0 && i > 0) break;
                }

                // BUG-AVATAR-04: the second half of the login summary. Silent before, so a login
                // where every attachment rezzed and a login where this never ran looked the same.
                // Only speaks up when it actually did something -- a clean login stays quiet.
                if (totalReattached > 0)
                    Console.Error.WriteLine(
                        $"[Appearance] login summary: RECONCILED {totalReattached} missing Current-Outfit attachment(s) " +
                        "— the sim did not rez them on login");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Appearance] attachment reconcile failed: {ex.Message}");
            }
        });
    }

    /// <summary>Re-sends an attach for every Current-Outfit attachment link whose target is not in
    /// the scene and was never seen worn this session. Returns how many re-attach requests were
    /// sent.</summary>
    private async Task<int> ReattachMissingCofAttachmentsAsync()
    {
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        if (cofUuid == LibreMetaverse.UUID.Zero) return 0;

        // The inventory is fetched lazily per folder — nothing pulls the COF on login, so it is
        // usually not in the store when this runs. Fetch it here or there is nothing to inspect.
        try { await FetchInventoryChildrenAsync(cofUuid.Guid).ConfigureAwait(false); }
        catch { }

        var store = _client.Inventory.Store;
        var cofNode = store?.GetNodeOrDefault(cofUuid);
        if (cofNode == null || cofNode.Nodes.Count == 0) return 0;

        // The SCENE is the only reliable "is it actually on the avatar" signal. LibreMetaverse's
        // GetAttachmentsByItemId() cache is NOT — it lags a detach and can list a COF attachment
        // as worn before it has rezzed, which is exactly how the boots kept getting skipped.
        var worn = new HashSet<Guid>(GetSceneWornAttachments().Keys);

        var byItem = new List<LibreMetaverse.InventoryItem>();
        var byRawUuid = new List<LibreMetaverse.UUID>();
        foreach (var childNode in cofNode.Nodes.Values)
        {
            if (childNode.Data is not LibreMetaverse.InventoryItem link) continue;

            var targetId = link.IsLink()
                ? (link.ResolvedItemID != LibreMetaverse.UUID.Zero ? link.ResolvedItemID : link.AssetUUID)
                : link.UUID;
            var target = targetId != LibreMetaverse.UUID.Zero
                ? store?.GetNodeOrDefault(targetId)?.Data as LibreMetaverse.InventoryItem
                : null;

            if (link.AssetType == LibreMetaverse.AssetType.LinkFolder) continue;
            if (targetId == LibreMetaverse.UUID.Zero
                || worn.Contains(targetId.Guid)
                || _attachmentsSeenWornThisSession.Contains(targetId.Guid)) continue;

            if (target != null)
            {
                if (target.AssetType == LibreMetaverse.AssetType.Object) byItem.Add(target);
                // else: wearable / gesture — no scene object to reconcile
            }
            else if (link.InventoryType == LibreMetaverse.InventoryType.Object
                     || link.AssetType == LibreMetaverse.AssetType.Object)
            {
                byRawUuid.Add(targetId);
            }
        }

        int total = byItem.Count + byRawUuid.Count;
        if (total == 0) return 0;

        Console.Error.WriteLine(
            $"[Appearance] {total} Current-Outfit attachment(s) the sim did not rez on login — re-attaching: " +
            string.Join(", ", byItem.Select(m => $"'{m.Name}'").Concat(byRawUuid.Select(u => "?" + u.ToString()[..8]))));

        foreach (var m in byItem)
        {
            try { _client.Appearance.Attach(m, LibreMetaverse.AttachmentPoint.Default, replace: false); }
            catch (Exception ex) { Console.Error.WriteLine($"[Appearance] re-attach of '{m.Name}' failed: {ex.Message}"); }
        }
        foreach (var u in byRawUuid)
        {
            try
            {
                _client.Appearance.Attach(u, _client.Self.AgentID, "Attachment", string.Empty,
                    new LibreMetaverse.Permissions { OwnerMask = LibreMetaverse.PermissionMask.All }, 0,
                    LibreMetaverse.AttachmentPoint.Default, replace: false);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[Appearance] re-attach of {u} failed: {ex.Message}"); }
        }
        return total;
    }

    /// <summary>Deletes every Current-Outfit link that points at one of <paramref name="itemIds"/>
    /// (or whose own id is in the set) and drops it from the local store. Returns how many were
    /// removed. Edits the outfit only -- the linked items stay in inventory. FEAT-INV-03.
    ///
    /// DELETE, not move-to-Trash: <c>MoveInventoryItem</c> on a Current-Outfit link gets HTTP 400
    /// from AIS on SL (the COF handler rejects the move), so the link never actually left and the
    /// item came back worn on the next login. The caller passes explicit ids of attachments it
    /// just detached, so a durable delete is well-targeted here (unlike the heuristic scan in
    /// <see cref="CleanUpCurrentOutfit"/>, which is load-gated for that reason).</summary>
    private int RemoveOutfitLinksForItems(ICollection<Guid> itemIds)
    {
        if (itemIds.Count == 0 || TrashFolderId is null) return 0;

        var store = _client.Inventory.Store;
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        var cofNode = cofUuid != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cofUuid) : null;
        if (cofNode == null) return 0;

        var want = new HashSet<Guid>(itemIds);
        var linkKeys = new List<LibreMetaverse.UUID>();
        foreach (var childNode in cofNode.Nodes.Values)
        {
            if (childNode.Data is not LibreMetaverse.InventoryItem link) continue;
            var target = link.IsLink()
                ? (link.ResolvedItemID != LibreMetaverse.UUID.Zero ? link.ResolvedItemID : link.AssetUUID)
                : link.UUID;
            if (link.UUID != LibreMetaverse.UUID.Zero
                && (want.Contains(link.UUID.Guid) || want.Contains(target.Guid)))
                linkKeys.Add(link.UUID);
        }

        foreach (var k in linkKeys) cofNode.Nodes.Remove(k);
        if (linkKeys.Count > 0)
        {
            try { _ = _client.Inventory.RemoveItemsAsync(linkKeys, System.Threading.CancellationToken.None); }
            catch (Exception ex) { Console.Error.WriteLine($"[Detach] RemoveItemsAsync threw: {ex.Message}"); }
        }
        return linkKeys.Count;
    }

    /// <summary>Walks up from <paramref name="node"/> to see if any ancestor folder is the Trash
    /// folder. Bounded so a cyclic store can't hang it. FEAT-INV-03.</summary>
    private bool IsUnderTrash(LibreMetaverse.InventoryNode node)
    {
        if (TrashFolderId is not { } trashId) return false;
        var trashUuid = new LibreMetaverse.UUID(trashId);
        var cur = node;
        for (int guard = 0; guard < 32 && cur != null; guard++)
        {
            if (cur.Data != null && cur.Data.UUID == trashUuid) return true;
            cur = cur.Parent;
        }
        return false;
    }

    /// <summary>Tidies the Current Outfit Folder: deletes (a) links that resolve to nothing,
    /// (b) links whose target item is already in Trash, (c) duplicate links to the same target,
    /// and (d) links to <c>AssetType.Object</c> items that are not currently attached. Never
    /// touches a Clothing/Bodypart link (removing one needs a rebake -- FEAT-AVATAR-01) or a
    /// currently-worn item. A COF link has no asset, so a delete only drops the outfit entry.
    ///
    /// Refuses to do anything while the inventory store or the scene is still loading (see the
    /// safety gate): a COF-link delete is a durable AIS delete on SL and any COF change forces a
    /// server re-composite, so acting on a half-loaded folder -- where an unresolved target reads
    /// as "dead" and an attachment not yet in the scene reads as "unworn" -- once deleted two real
    /// links and left the avatar with no bake (live regression 2026-09-03). FEAT-INV-03.</summary>
    /// <param name="targetsResolved">Set by <see cref="CleanUpCurrentOutfitAsync"/> after it has
    /// explicitly asked the server for every COF link target missing from the store. Without it the
    /// store-ready half of the gate below is <b>unsatisfiable by waiting</b>: LibreMetaverse's store
    /// only ever holds folders somebody fetched, so a link pointing into a folder the user never
    /// opened never resolves, no matter how long you wait. Live, Agni 2026-09-03:
    /// <c>[OutfitCleanup] deferred — still loading (links=28 unresolved=10 …)</c> on a fully-loaded
    /// session — "Outfit aufräumen" had become a permanent no-op ("bereinigen hilft auch nicht").
    /// Any link still unresolved after that fetch is skipped individually further down
    /// (<c>uncachedSkipped</c>), never deleted, so relaxing the gate cannot delete a link we failed
    /// to understand. The half that actually caused the v0.20.36 regression -- <c>sceneReady</c>,
    /// which stops an attachment that has not rezzed yet from reading as "not worn" -- is
    /// untouched, and that is the path the incident's own log line
    /// (<c>unworn-attachment=2</c>) came from.</param>
    /// <summary>Resolves every Current-Outfit link target that is missing from LibreMetaverse's
    /// inventory store, then runs <see cref="CleanUpCurrentOutfit"/>.
    ///
    /// <para>The fetch is the point. A COF link carries the target item's NAME, so the Worn tab can
    /// list an item perfectly well without its target ever being in the store -- which is why two
    /// attachments could show as worn-but-inactive while the cleanup that should have removed them
    /// skipped them as "uncached" and, worse, refused to run at all because its store-ready gate
    /// counted them as "still loading". Asking the server for them turns both into a decision that
    /// can actually be made.</para>
    ///
    /// <para>Anything the server does not return stays unresolved and is skipped by the cleanup
    /// loop, exactly as before -- this only removes the case where SLNG had simply never asked.</para>
    /// </summary>
    public async Task<OutfitCleanupResult> CleanUpCurrentOutfitAsync(CancellationToken ct = default)
    {
        HashSet<Guid>? confirmedMissing = null;
        try { confirmedMissing = await ResolveCofLinkTargetsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine($"[OutfitCleanup] link-target fetch failed: {ex.Message}"); }
        return CleanUpCurrentOutfit(targetsResolved: true, confirmedMissing);
    }

    /// <returns>Targets we explicitly asked the server for that did NOT come back, i.e. items the
    /// server does not have. <c>null</c> when the fetch itself failed or was cut short -- then we
    /// know nothing, and the caller must not treat any link as dead.</returns>
    private async Task<HashSet<Guid>?> ResolveCofLinkTargetsAsync(CancellationToken ct)
    {
        var store = _client.Inventory.Store;
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        var cofNode = cofUuid != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cofUuid) : null;
        if (cofNode == null || store == null) return null;

        int linkCount = 0;
        var missing = new Dictionary<LibreMetaverse.UUID, LibreMetaverse.UUID>();
        foreach (var n in cofNode.Nodes.Values)
        {
            if (n.Data is not LibreMetaverse.InventoryItem li || !li.IsLink()) continue;
            linkCount++;
            var t = li.ResolvedItemID != LibreMetaverse.UUID.Zero ? li.ResolvedItemID : li.AssetUUID;
            if (t == LibreMetaverse.UUID.Zero) continue;
            if (store.GetNodeOrDefault(t)?.Data is LibreMetaverse.InventoryItem) continue;
            missing[t] = _client.Self.AgentID;
        }
        if (missing.Count == 0) return new HashSet<Guid>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        bool fetchCompleted = true;
        try
        {
            await _client.Inventory.RequestFetchInventoryAsync(missing, timeout.Token, items =>
            {
                if (items == null) return;
                foreach (var item in items)
                {
                    if (item == null) continue;
                    // UpdateNodeFor is LibreMetaverse's own way of putting a fetched item into the
                    // store -- its fetch reply handler uses it too.
                    try { store.UpdateNodeFor(item); } catch { }
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { fetchCompleted = false; }
        catch (Exception ex)
        {
            fetchCompleted = false;
            Console.Error.WriteLine($"[OutfitCleanup] COF link-target fetch threw: {ex.Message}");
        }

        // Then WAIT FOR THE STORE, rather than trusting the call above to have finished the job.
        // Whether that method returns once the reply is in, or merely once the request is sent, is
        // an implementation detail of the pinned LibreMetaverse build -- and the callback is not
        // the only writer either, since LMV's own reply handler also fills the store. Polling what
        // the cleanup actually reads makes the outcome independent of both. Short poll, hard cap:
        // a target the server will not return must not hold the button hostage, and one that stays
        // missing is skipped by the cleanup loop anyway.
        int resolved = 0;
        for (int i = 0; i < 25; i++)
        {
            resolved = missing.Keys.Count(t => store.GetNodeOrDefault(t)?.Data is LibreMetaverse.InventoryItem);
            if (resolved == missing.Count) break;
            try { await Task.Delay(200, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { fetchCompleted = false; break; }
        }

        var stillMissing = missing.Keys
            .Where(t => store.GetNodeOrDefault(t)?.Data is not LibreMetaverse.InventoryItem)
            .Select(t => t.Guid)
            .ToHashSet();

        // Link count AND distinct-target count, because they diverge in a way that matters: a live
        // session showed uncached=10 links against exactly ONE unresolved target -- ten Current
        // Outfit links all pointing at the same vanished item. The per-link number on its own reads
        // like ten separate problems.
        Console.Error.WriteLine(
            $"[OutfitCleanup] link targets: {linkCount} link(s), {missing.Count} distinct uncached target(s), " +
            $"resolved {resolved}, still missing {stillMissing.Count}, fetchCompleted={fetchCompleted}");

        // Only a CLEAN fetch licenses "the server does not have this". A cancelled or throwing one
        // tells us nothing, and the caller must not delete anything on the strength of it.
        return fetchCompleted ? stillMissing : null;
    }

    /// <param name="confirmedMissingTargets">Link targets the server was explicitly asked for and
    /// did not return -- so the item is gone and its Current-Outfit links are dead weight that the
    /// <c>uncached</c> skip below would otherwise preserve forever. <c>null</c> means "we did not
    /// ask, or the asking failed", and then nothing here is treated as missing.</param>
    public OutfitCleanupResult CleanUpCurrentOutfit(bool targetsResolved = false,
        HashSet<Guid>? confirmedMissingTargets = null)
    {
        // Proxy for "inventory skeleton is loaded" -- if the Trash folder isn't known yet, the
        // Current Outfit folder almost certainly isn't either.
        if (TrashFolderId is null) return new OutfitCleanupResult(0, 0, 0, Deferred: true);

        var store = _client.Inventory.Store;
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        var cofNode = cofUuid != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cofUuid) : null;
        if (cofNode == null)
        {
            Console.Error.WriteLine("[OutfitCleanup] no Current Outfit folder in the store — nothing to do");
            return new OutfitCleanupResult(0, 0, 0, Deferred: true);
        }

        // "Worn right now" from the SCENE, not LibreMetaverse's GetAttachmentsByItemId() cache --
        // that cache lags a detach, which is exactly the "I took it all off but the outfit still
        // lists it" case.
        var wornAttachItemIds = new HashSet<Guid>(GetSceneWornAttachments().Keys);

        HashSet<Guid> cacheAttachIds;
        try { cacheAttachIds = _client.Appearance.GetAttachmentsByItemId().Keys.Select(k => k.Guid).ToHashSet(); }
        catch { cacheAttachIds = new HashSet<Guid>(); }

        // SAFETY GATE — live regression 2026-09-03 (v0.20.34: "Outfit aufräumen" deleted two links
        // and the avatar came back grey on the next login). Since v0.20.33 a removal here is a
        // durable AIS delete on SL, and *any* COF change makes the server re-composite the avatar.
        // While the store is still streaming, a link whose target node hasn't arrived is
        // indistinguishable from a genuinely dead one; while the region prims are still arriving,
        // an attachment reads as "unworn". Only touch the COF once BOTH are demonstrably in.
        int linkTotal = 0, linkUnresolved = 0;
        foreach (var n in cofNode.Nodes.Values)
        {
            if (n.Data is not LibreMetaverse.InventoryItem li || !li.IsLink()) continue;
            linkTotal++;
            var t = li.ResolvedItemID != LibreMetaverse.UUID.Zero ? li.ResolvedItemID : li.AssetUUID;
            // t == Zero is a genuinely targetless ("dead") link regardless of load state; only a
            // link WITH a target whose node is missing from the store means "still loading".
            if (t != LibreMetaverse.UUID.Zero &&
                store?.GetNodeOrDefault(t)?.Data is not LibreMetaverse.InventoryItem)
                linkUnresolved++;
        }

        // You are always wearing at least a body — zero scene attachments means the region prims
        // haven't arrived, so "not in the scene" can't yet be read as "not worn".
        bool sceneReady = wornAttachItemIds.Count > 0;
        bool storeReady = linkTotal > 0 && (linkUnresolved == 0 || targetsResolved);
        if (!storeReady || !sceneReady)
        {
            Console.Error.WriteLine(
                $"[OutfitCleanup] deferred — still loading (links={linkTotal} unresolved={linkUnresolved} " +
                $"scene-worn-attachments={wornAttachItemIds.Count} targetsResolved={targetsResolved}); " +
                "nothing removed, retry in a moment");
            return new OutfitCleanupResult(0, 0, 0, Deferred: true);
        }

        int dead = 0, trashedTarget = 0, unworn = 0, duplicate = 0, missingTarget = 0;

        // A Current Outfit folder holds ONE link per worn item. More than one is corruption, and it
        // is not cosmetic: a wearable linked twice is worn twice, drawn twice, and the copy without
        // an ordering token sorts below everything that has one -- so a second copy of an opaque
        // skin quietly reappears underneath the whole stack. Measured live 2026-09-01: 20 links for
        // 10 wearables. The link carrying a valid ordering token is the one to keep, since that is
        // what the layer order is built from.
        var seenTargets = new Dictionary<LibreMetaverse.UUID, LibreMetaverse.UUID>();
        int links = 0, wearableSkipped = 0, wornSkipped = 0, uncachedSkipped = 0;
        var toRemove = new List<LibreMetaverse.UUID>();

        // DELETE the COF link, don't move it to Trash. A COF link has no asset -- deleting one
        // only drops the outfit entry, the linked item is untouched -- and MoveInventoryItem on a
        // Current-Outfit link does NOT stick on SL/OpenSim: the link reappears on the next COF
        // refetch (user-reported: "beim aufräumen verschwinden die kurz, tauchen aber wieder auf").
        // RemoveItemsAsync is what the reference viewer uses for COF link removal
        // (llappearancemgr.cpp removeCOFItemLinks -> remove_inventory_item); LibreMetaverse routes
        // it through the AIS capability on SL (durable) and a RemoveInventoryObjects packet on
        // OpenSim, either a real delete rather than a move the COF handler reverts.
        bool Trash(LibreMetaverse.UUID linkKey)
        {
            if (linkKey == LibreMetaverse.UUID.Zero) return false;
            toRemove.Add(linkKey);
            cofNode!.Nodes.Remove(linkKey);
            return true;
        }

        foreach (var childNode in cofNode.Nodes.Values.ToList())
        {
            if (childNode.Data is not LibreMetaverse.InventoryItem link || !link.IsLink()) continue;
            links++;

            var targetUuid = link.ResolvedItemID != LibreMetaverse.UUID.Zero ? link.ResolvedItemID : link.AssetUUID;

            if (targetUuid == LibreMetaverse.UUID.Zero)
            {
                if (Trash(link.UUID)) dead++;
                continue;
            }

            var targetNode = store?.GetNodeOrDefault(targetUuid);
            if (targetNode != null && IsUnderTrash(targetNode))
            {
                if (Trash(link.UUID)) trashedTarget++;
                continue;
            }

            var target = targetNode?.Data as LibreMetaverse.InventoryItem;
            if (target == null)
            {
                // "Not in the store" on its own is evidence of nothing -- LibreMetaverse's store
                // only holds folders somebody fetched, so this is the normal state for an item in a
                // folder the user never opened, and skipping is right. Once the server has been
                // asked for this exact id and did not return it, though, the item is GONE and the
                // link is dead weight the skip would preserve forever. Measured live: 10 such links
                // in one COF, all pointing at a single vanished item, every one of them skipped --
                // which is a large part of why "Outfit aufraeumen" looked like it did nothing.
                if (confirmedMissingTargets != null && confirmedMissingTargets.Contains(targetUuid.Guid))
                {
                    if (Trash(link.UUID)) missingTarget++;
                    continue;
                }
                uncachedSkipped++;
                continue;
            }

            if (seenTargets.TryGetValue(targetUuid, out var keptLink))
            {
                // Keep whichever of the two carries a usable ordering token.
                var wearType = target is LibreMetaverse.InventoryWearable dupWearable
                    ? (int)dupWearable.WearableType : -1;
                bool thisTokened = wearType >= 0 && WearableLayerOrder.IsValidOrderString(link.Description, wearType);
                bool keptTokened = wearType >= 0
                    && store?.GetNodeOrDefault(keptLink)?.Data is LibreMetaverse.InventoryItem k
                    && WearableLayerOrder.IsValidOrderString(k.Description, wearType);

                var drop = thisTokened && !keptTokened ? keptLink : link.UUID;
                if (drop == keptLink) seenTargets[targetUuid] = link.UUID;
                if (Trash(drop)) duplicate++;
                continue;
            }
            seenTargets[targetUuid] = link.UUID;
            if (target.AssetType != LibreMetaverse.AssetType.Object) { wearableSkipped++; continue; }
            if (wornAttachItemIds.Contains(targetUuid.Guid)) { wornSkipped++; continue; }

            if (Trash(link.UUID)) unworn++;
        }

        if (toRemove.Count > 0)
        {
            try { _ = _client.Inventory.RemoveItemsAsync(toRemove, System.Threading.CancellationToken.None); }
            catch (Exception ex) { Console.Error.WriteLine($"[OutfitCleanup] RemoveItemsAsync threw: {ex.Message}"); }
        }

        Console.Error.WriteLine(
            $"[OutfitCleanup] links={links} scene-worn={wornAttachItemIds.Count} cache-worn={cacheAttachIds.Count} " +
            $"| deleted dead={dead} target-in-trash={trashedTarget} unworn-attachment={unworn} duplicate={duplicate} " +
            $"missing-target={missingTarget} " +
            $"(via RemoveItems, AIS={_client.AisClient?.IsAvailable}) " +
            $"| kept worn={wornSkipped} clothing/bodypart={wearableSkipped} uncached={uncachedSkipped}");

        return new OutfitCleanupResult(dead + missingTarget, trashedTarget, unworn);
    }

    /// <summary>
    /// Formats an SL AttachmentPoint enum value to a human-readable display string.
    /// </summary>
    public static string FormatAttachmentPoint(LibreMetaverse.AttachmentPoint point)
    {
        return point switch
        {
            LibreMetaverse.AttachmentPoint.Chest => "Brust",
            LibreMetaverse.AttachmentPoint.Skull => "Kopf",
            LibreMetaverse.AttachmentPoint.LeftShoulder => "Linke Schulter",
            LibreMetaverse.AttachmentPoint.RightShoulder => "Rechte Schulter",
            LibreMetaverse.AttachmentPoint.LeftHand => "Linke Hand",
            LibreMetaverse.AttachmentPoint.RightHand => "Rechte Hand",
            LibreMetaverse.AttachmentPoint.LeftFoot => "Linker Fuß",
            LibreMetaverse.AttachmentPoint.RightFoot => "Rechter Fuß",
            LibreMetaverse.AttachmentPoint.Spine => "Rücken",
            LibreMetaverse.AttachmentPoint.Pelvis => "Becken",
            LibreMetaverse.AttachmentPoint.Mouth => "Mund",
            LibreMetaverse.AttachmentPoint.Chin => "Kinn",
            LibreMetaverse.AttachmentPoint.LeftEar => "Linkes Ohr",
            LibreMetaverse.AttachmentPoint.RightEar => "Rechtes Ohr",
            LibreMetaverse.AttachmentPoint.LeftEyeball => "Linkes Auge",
            LibreMetaverse.AttachmentPoint.RightEyeball => "Rechtes Auge",
            LibreMetaverse.AttachmentPoint.Nose => "Nase",
            LibreMetaverse.AttachmentPoint.RightUpperArm => "Rechter Oberarm",
            LibreMetaverse.AttachmentPoint.RightForearm => "Rechter Unterarm",
            LibreMetaverse.AttachmentPoint.LeftUpperArm => "Linker Oberarm",
            LibreMetaverse.AttachmentPoint.LeftForearm => "Linker Unterarm",
            LibreMetaverse.AttachmentPoint.RightHip => "Rechte Hüfte",
            LibreMetaverse.AttachmentPoint.RightUpperLeg => "Rechtes Oberschenkel",
            LibreMetaverse.AttachmentPoint.RightLowerLeg => "Rechtes Unterschenkel",
            LibreMetaverse.AttachmentPoint.LeftHip => "Linke Hüfte",
            LibreMetaverse.AttachmentPoint.LeftUpperLeg => "Linkes Oberschenkel",
            LibreMetaverse.AttachmentPoint.LeftLowerLeg => "Linkes Unterschenkel",
            LibreMetaverse.AttachmentPoint.Stomach => "Bauch",
            LibreMetaverse.AttachmentPoint.LeftPec => "Linke Brust",
            LibreMetaverse.AttachmentPoint.RightPec => "Rechte Brust",
            LibreMetaverse.AttachmentPoint.HUDCenter2 => "Mitte 2",
            LibreMetaverse.AttachmentPoint.HUDTopRight => "Oben rechts",
            LibreMetaverse.AttachmentPoint.HUDTop => "Oben",
            LibreMetaverse.AttachmentPoint.HUDTopLeft => "Oben links",
            LibreMetaverse.AttachmentPoint.HUDCenter => "Mitte",
            LibreMetaverse.AttachmentPoint.HUDBottomLeft => "Unten links",
            LibreMetaverse.AttachmentPoint.HUDBottom => "Unten",
            LibreMetaverse.AttachmentPoint.HUDBottomRight => "Unten rechts",
            LibreMetaverse.AttachmentPoint.Neck => "Hals",
            LibreMetaverse.AttachmentPoint.Root => "Stamm",
            LibreMetaverse.AttachmentPoint.LeftWing => "Linker Flügel",
            LibreMetaverse.AttachmentPoint.RightWing => "Rechter Flügel",
            _ => point.ToString()
        };
    }

    /// <summary>
    /// Returns a dictionary mapping currently worn inventory item IDs (or link target IDs)
    /// to their attachment point display string (e.g. "Linker Flügel", "Oben links") or "getragen".
    /// </summary>
    public Dictionary<Guid, string> GetWornItemsMap()
    {
        var result = new Dictionary<Guid, string>();

        // Scene-derived, not GetAttachmentsByItemId() — that cache lags a detach (project memory).
        foreach (var kvp in GetSceneWornAttachments())
            result[kvp.Key] = FormatAttachmentPoint(kvp.Value);

        try
        {
            var store = _client.Inventory.Store;
            var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
            if (cofUuid != LibreMetaverse.UUID.Zero)
            {
                var cofNode = store?.GetNodeOrDefault(cofUuid);
                if (cofNode != null)
                {
                    foreach (var childNode in cofNode.Nodes.Values)
                    {
                        if (childNode.Data is LibreMetaverse.InventoryItem item)
                        {
                            var targetId = item.IsLink() ? item.ResolvedItemID.Guid : item.UUID.Guid;
                            if (targetId != Guid.Empty && !result.ContainsKey(targetId))
                            {
                                result[targetId] = "getragen";
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return result;
    }

    /// <summary>Which <see cref="WornCategory"/> a system wearable of the given SL <c>AssetType</c>
    /// wire value falls under. FEAT-UI-16.</summary>
    internal static WornCategory CategorizeWearable(int assetType)
        => assetType == (int)LibreMetaverse.AssetType.Bodypart
            ? WornCategory.BodyPart
            : WornCategory.Clothing;

    /// <summary>Whether a raw SL attachment-point value is a HUD point (31..38 =
    /// <c>HUDCenter2</c>..<c>HUDBottomRight</c>) or a body point. Mirrors the HUD test in
    /// <see cref="DetachAllAttachments"/>. FEAT-UI-16.</summary>
    internal static WornCategory CategorizeAttachment(int attachPointRaw)
        => attachPointRaw >= 31 && attachPointRaw <= 38
            ? WornCategory.Hud
            : WornCategory.Attachment;

    /// <summary>Item ids we've already asked the grid to fetch details for (FEAT-UI-16 name
    /// resolution). Prevents <see cref="GetWornItems"/> re-requesting the same ids on every poll.</summary>
    private readonly HashSet<Guid> _wornDetailFetchRequested = new();

    /// <summary>Every item the local avatar is wearing right now — live attachments and the live
    /// wearables set, plus anything referenced only by a Current-Outfit link (reported with
    /// <c>Live == false</c>: a stale link, or a wear that has not taken effect). Backs the
    /// inventory "Worn" tab (FEAT-UI-16); <see cref="GetWornItemsMap"/> still backs the
    /// "(getragen)" labels in the folder tree.</summary>
    public IReadOnlyList<WornItem> GetWornItems()
    {
        var byId = new Dictionary<Guid, WornItem>();
        var store = _client.Inventory.Store;

        string StoreName(LibreMetaverse.UUID id) =>
            (store?.GetNodeOrDefault(id)?.Data as LibreMetaverse.InventoryItem)?.Name ?? string.Empty;

        // Worn attachments come from the SCENE, not AppearanceManager.GetAttachmentsByItemId() --
        // that cache keeps listing an item after it's detached, which showed detached attachments
        // as still-worn in the tab (project memory). A prim parented to us also carries a usable
        // name in Properties.Name when the inventory item isn't in the lazily-loaded store yet.
        var scenePrimNames = new Dictionary<Guid, string>();
        try
        {
            var sim = _client.Network.CurrentSim;
            if (sim != null)
                foreach (var p in sim.ObjectsPrimitives.Values)
                {
                    if (p == null || p.ParentID != _client.Self.LocalID) continue;
                    var aid = ExtractAttachItemId(p);
                    var nm = p.Properties?.Name;
                    if (aid != Guid.Empty && !string.IsNullOrEmpty(nm)) scenePrimNames[aid] = nm!;
                }
        }
        catch { }

        var unresolved = new List<LibreMetaverse.UUID>();

        foreach (var kvp in GetSceneWornAttachments())
        {
            var id = kvp.Key;
            if (id == Guid.Empty) continue;
            var uuid = new LibreMetaverse.UUID(id);
            var name = StoreName(uuid);
            if (name.Length == 0 && scenePrimNames.TryGetValue(id, out var sn)) name = sn;
            if (name.Length == 0) unresolved.Add(uuid);
            byId[id] = new WornItem(id, name,
                CategorizeAttachment((int)kvp.Value), FormatAttachmentPoint(kvp.Value),
                (int)LibreMetaverse.AssetType.Object, Live: true);
        }

        try
        {
            foreach (var w in _client.Appearance.GetWearables())
            {
                var id = w.ItemID.Guid;
                if (id == Guid.Empty || byId.ContainsKey(id)) continue;
                var name = StoreName(w.ItemID);
                if (name.Length == 0) unresolved.Add(w.ItemID);
                byId[id] = new WornItem(id, name,
                    CategorizeWearable((int)w.AssetType), null, (int)w.AssetType, Live: true);
            }
        }
        catch { }

        try
        {
            var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
            var cofNode = cofUuid != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cofUuid) : null;
            if (cofNode != null)
            {
                foreach (var childNode in cofNode.Nodes.Values)
                {
                    if (childNode.Data is not LibreMetaverse.InventoryItem link) continue;

                    // Skip the outfit FOLDER link. The Current Outfit Folder carries one link to
                    // the outfit folder itself so a viewer can name the worn outfit -- Firestorm
                    // shows it as "Aktuelles Outfit: <name>". It is not a worn item, and listing it
                    // put the outfit's own name in the attachments group as a permanently
                    // "(nicht aktiv)" row (reported live: "Standard Enzo").
                    if (link.AssetType == LibreMetaverse.AssetType.LinkFolder) continue;

                    var targetUuid = link.IsLink() ? link.ResolvedItemID : link.UUID;
                    var id = targetUuid.Guid;
                    if (id == Guid.Empty || byId.ContainsKey(id)) continue;

                    var target = store?.GetNodeOrDefault(targetUuid)?.Data as LibreMetaverse.InventoryItem;
                    int assetType = (int)(target?.AssetType ?? link.AssetType);
                    var cat = assetType == (int)LibreMetaverse.AssetType.Bodypart ? WornCategory.BodyPart
                        : assetType == (int)LibreMetaverse.AssetType.Clothing ? WornCategory.Clothing
                        : WornCategory.Attachment;
                    var name = target?.Name ?? link.Name ?? string.Empty;
                    if (name.Length == 0 && scenePrimNames.TryGetValue(id, out var sn)) name = sn;
                    if (name.Length == 0 && targetUuid != LibreMetaverse.UUID.Zero) unresolved.Add(targetUuid);

                    // A Current-Outfit link IS the worn state for a WEARABLE. There is nothing else
                    // to check it against: unlike an attachment, a Clothing/Bodypart layer has no
                    // in-scene object, so BUG-NET-02's "stale link with no live attachment" test
                    // simply does not apply to it. Marking these not-live was wrong and showed most
                    // of the outfit as "(nicht aktiv)" while Firestorm -- which reads the COF --
                    // listed the same items as worn.
                    //
                    // The legacy AgentWearablesUpdate cannot stand in for this: it carries ONE
                    // wearable per type slot, so a modern multi-layer outfit (several skin/tattoo
                    // layers) is unrepresentable in it and the extra layers never appear in
                    // Appearance.GetWearables() at all. The COF is the only complete source.
                    bool live = cat is WornCategory.BodyPart or WornCategory.Clothing;
                    byId[id] = new WornItem(id, name, cat, null, assetType, Live: live);
                }
            }
        }
        catch { }

        // Pull missing item details into the store so the next poll has real names. Requested
        // once per id per session -- the tab's 2.5 s refresh (FEAT-UI-16) surfaces the result.
        var toFetch = new Dictionary<LibreMetaverse.UUID, LibreMetaverse.UUID>();
        foreach (var u in unresolved)
            if (_wornDetailFetchRequested.Add(u.Guid)) toFetch[u] = _client.Self.AgentID;
        if (toFetch.Count > 0)
        {
            try { _client.Inventory.RequestFetchInventory(toFetch); } catch { }
        }

        return byId.Values.ToList();
    }

    /// <summary>The saved outfits — every direct subfolder of the <c>#Outfits</c> system folder,
    /// with the one the avatar is currently wearing flagged. Empty if the grid has no
    /// <c>#Outfits</c> folder or we're not connected. FEAT-INV-04.</summary>
    public async Task<IReadOnlyList<OutfitEntry>> GetSavedOutfitsAsync(CancellationToken ct = default)
    {
        if (MyOutfitsFolderId is not { } outfitsId) return Array.Empty<OutfitEntry>();

        var children = await FetchInventoryChildrenAsync(outfitsId, ct).ConfigureAwait(false);
        var folders = children.Where(e => e.IsFolder).ToList();

        // (1) The Current Outfit Folder carries a folder-link to the outfit it's "based on"
        // (SL / Firestorm mechanism). Cheap — one fetch.
        var activeOutfit = Guid.Empty;
        try
        {
            var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
            if (cofUuid != LibreMetaverse.UUID.Zero)
            {
                await FetchInventoryChildrenAsync(cofUuid.Guid, ct).ConfigureAwait(false);
                var cofNode = _client.Inventory.Store?.GetNodeOrDefault(cofUuid);
                if (cofNode != null)
                    foreach (var n in cofNode.Nodes.Values)
                        if (n.Data is LibreMetaverse.InventoryItem it
                            && it.AssetType == LibreMetaverse.AssetType.LinkFolder
                            && it.AssetUUID != LibreMetaverse.UUID.Zero)
                        { activeOutfit = it.AssetUUID.Guid; break; }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // (2) Fallback (common on OpenSim, which doesn't write that folder-link): the current
        // outfit is the fully-worn one with the most items — i.e. every item it links is worn now.
        if (activeOutfit == Guid.Empty && folders.Count is > 0 and <= 60)
        {
            var wornIds = new HashSet<Guid>(GetWornItems().Where(w => w.Live).Select(w => w.ItemId));
            if (wornIds.Count > 0)
            {
                int bestCount = 0;
                var contents = await Task.WhenAll(folders.Select(f =>
                    FetchInventoryChildrenAsync(f.Id, ct))).ConfigureAwait(false);
                for (int i = 0; i < folders.Count; i++)
                {
                    var targets = contents[i]
                        .Where(e => !e.IsFolder)
                        .Select(e => e.IsLink && e.LinkTargetId != Guid.Empty ? e.LinkTargetId : e.Id)
                        .Where(g => g != Guid.Empty)
                        .ToHashSet();
                    if (targets.Count > bestCount && targets.IsSubsetOf(wornIds))
                    {
                        bestCount = targets.Count;
                        activeOutfit = folders[i].Id;
                    }
                }
            }
        }

        Console.Error.WriteLine($"[SavedOutfits] {folders.Count} outfits, active={activeOutfit}");

        var result = folders
            .Select(e => new OutfitEntry(e.Id, e.Name, e.Id == activeOutfit && activeOutfit != Guid.Empty))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return result;
    }

    /// <summary>Current worn set, re-read once after a short wait if any name is still unresolved
    /// (wearables have no in-scene prim to fall back on), so links get real names not "Link".</summary>
    private async Task<IReadOnlyList<WornItem>> GetWornItemsWithNamesAsync(CancellationToken ct)
    {
        var worn = GetWornItems();
        if (worn.Any(w => w.Live && string.IsNullOrEmpty(w.Name)))
        {
            try { await Task.Delay(700, ct).ConfigureAwait(false); } catch (OperationCanceledException) { throw; }
            worn = GetWornItems();
        }
        return worn;
    }

    /// <summary>The set of inventory-item ids an outfit folder already links to.</summary>
    private async Task<HashSet<Guid>> GetOutfitTargetIdsAsync(Guid outfitFolderId, CancellationToken ct)
    {
        var set = new HashSet<Guid>();
        try
        {
            foreach (var e in await FetchInventoryChildrenAsync(outfitFolderId, ct).ConfigureAwait(false))
            {
                if (e.IsFolder) continue;
                var t = e.IsLink && e.LinkTargetId != Guid.Empty ? e.LinkTargetId : e.Id;
                if (t != Guid.Empty) set.Add(t);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return set;
    }

    /// <summary>Creates a link in <paramref name="folder"/> for each live worn item not in
    /// <paramref name="skip"/>. Returns how many links were created.</summary>
    private async Task<int> LinkWornIntoAsync(
        LibreMetaverse.UUID folder, IReadOnlyList<WornItem> worn, HashSet<Guid> skip, CancellationToken ct)
    {
        int added = 0;
        foreach (var w in worn)
        {
            if (!w.Live || w.ItemId == Guid.Empty || skip.Contains(w.ItemId)) continue;
            ct.ThrowIfCancellationRequested();

            var linkName = w.Name;
            if (string.IsNullOrEmpty(linkName))
                linkName = (_client.Inventory.Store?.GetNodeOrDefault(new LibreMetaverse.UUID(w.ItemId))?.Data
                    as LibreMetaverse.InventoryItem)?.Name ?? string.Empty;

            var invType = w.Category is WornCategory.BodyPart or WornCategory.Clothing
                ? LibreMetaverse.InventoryType.Wearable
                : LibreMetaverse.InventoryType.Object;
            try
            {
                // CreateLinkAsync does NOT throw on an AIS rejection (e.g. "Create inventory in
                // <folder>: Bad Request") -- InventoryAISClient swallows it and resolves to a
                // null InventoryItem. Counting every call as `added` regardless of this return
                // value reported a link as saved when AIS had silently refused it, so the outfit
                // came back short after a relog with no error anywhere in the UI (v0.20.96).
                var created = await _client.Inventory.CreateLinkAsync(
                    folder, new LibreMetaverse.UUID(w.ItemId), linkName, string.Empty,
                    invType, LibreMetaverse.UUID.Zero, ct).ConfigureAwait(false);
                if (created != null)
                {
                    added++;
                }
                else
                {
                    Console.Error.WriteLine(
                        $"[Outfits] link create for {w.ItemId} ('{linkName}') into {folder} came back empty -- " +
                        "see the preceding 'Create inventory' warning for the AIS reason");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Outfits] CreateLinkAsync threw for {w.ItemId} ('{linkName}'): {ex.Message}");
            }
        }
        return added;
    }

    /// <summary>Saves what is worn right now into a <c>#Outfits</c> subfolder as inventory links.
    /// A same-named subfolder is reused rather than spawning a duplicate. Returns the folder id,
    /// or null if there's no <c>#Outfits</c> folder / the folder create failed. FEAT-INV-04.
    ///
    /// <para>On an AISv3 grid this is <c>LLAppearanceMgr::makeNewOutfitLinks</c>: create the
    /// folder, then one atomic <c>slamCategoryLinks(getCOF(), folder)</c> — the same COF-sourced
    /// slam as <see cref="ReplaceOutfitWithCurrentAsync"/>, so it never feeds AIS a scene
    /// <c>AttachItemID</c> that resolves to a link or a since-gone item (the
    /// <c>Create inventory in … Bad Request</c> pairs). OpenSim keeps the per-item link
    /// pass.</para></summary>
    public async Task<Guid?> SaveCurrentOutfitAsync(string name, CancellationToken ct = default)
    {
        if (MyOutfitsFolderId is not { } outfitsId) return null;
        name = string.IsNullOrWhiteSpace(name) ? "Outfit" : name.Trim();

        // Reuse an existing same-name outfit folder rather than creating a duplicate.
        var folder = LibreMetaverse.UUID.Zero;
        var outfitsNode = _client.Inventory.Store?.GetNodeOrDefault(new LibreMetaverse.UUID(outfitsId));
        if (outfitsNode != null)
            foreach (var n in outfitsNode.Nodes.Values)
                if (n.Data is LibreMetaverse.InventoryFolder f
                    && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                { folder = f.UUID; break; }

        bool freshlyCreated = folder == LibreMetaverse.UUID.Zero;
        if (freshlyCreated)
            folder = _client.Inventory.CreateFolder(new LibreMetaverse.UUID(outfitsId), name);
        if (folder == LibreMetaverse.UUID.Zero) return null;

        if (_client.AisClient?.IsAvailable == true)
        {
            // CreateFolder is a fire-and-forget UDP packet with a client-side UUID; give the
            // server a moment to register it before the slam PUT lands, and retry once.
            if (freshlyCreated) await SafeDelayAsync(600, ct).ConfigureAwait(false);
            try
            {
                if (await SlamOutfitLinksFromCofAsync(folder, ct).ConfigureAwait(false) is null && freshlyCreated)
                {
                    await SafeDelayAsync(1200, ct).ConfigureAwait(false);
                    await SlamOutfitLinksFromCofAsync(folder, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Outfits] new-outfit slam into {folder} failed: {ex.Message}");
            }
        }
        else
        {
            var worn = await GetWornItemsWithNamesAsync(ct).ConfigureAwait(false);
            var already = await GetOutfitTargetIdsAsync(folder.Guid, ct).ConfigureAwait(false);
            await LinkWornIntoAsync(folder, worn, already, ct).ConfigureAwait(false);
        }

        // Saving the look you are wearing makes that outfit the active one — the COF folder-link
        // marker the Outfits list reads (LLAppearanceMgr::makeNewOutfitLinks → createBaseOutfitLink).
        try { await SetCurrentOutfitLinkAsync(folder.Guid, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Console.Error.WriteLine($"[Outfits] set-active-outfit link failed: {ex.Message}"); }

        return folder.Guid;
    }

    private static async Task SafeDelayAsync(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
    }

    /// <summary>Adds the current worn set to an existing outfit folder — links only the items that
    /// aren't already in it. Returns how many links were added. FEAT-INV-04.</summary>
    public async Task<int> AddCurrentToOutfitAsync(Guid outfitFolderId, CancellationToken ct = default)
    {
        if (outfitFolderId == Guid.Empty) return 0;
        var worn = await GetWornItemsWithNamesAsync(ct).ConfigureAwait(false);
        var already = await GetOutfitTargetIdsAsync(outfitFolderId, ct).ConfigureAwait(false);
        return await LinkWornIntoAsync(new LibreMetaverse.UUID(outfitFolderId), worn, already, ct).ConfigureAwait(false);
    }

    /// <summary>The decision half of <see cref="ReplaceOutfitWithCurrentAsync"/>: every entry in an
    /// outfit folder that is a <b>link</b> (those are deleted so the folder can be re-linked from
    /// scratch), plus the ids of entries that are real items rather than links — those are left
    /// alone, because deleting one would destroy inventory over an "edit this outfit" action (same
    /// rule as <see cref="SelectOutfitLinksToRemove"/>). Folders are ignored. Pure so it can be
    /// tested without a grid.</summary>
    internal static (List<Guid> LinkIds, List<Guid> NonLinkItemIds) SelectOutfitLinksToClear(
        IEnumerable<InventoryEntry> children)
    {
        var linkIds = new List<Guid>();
        var nonLinkItemIds = new List<Guid>();
        foreach (var e in children)
        {
            if (e.IsFolder) continue;
            if (e.IsLink) linkIds.Add(e.Id);
            else nonLinkItemIds.Add(e.Id);
        }
        return (linkIds, nonLinkItemIds);
    }

    /// <summary>One row of the Current Outfit folder, reduced to just what
    /// <see cref="SelectCofLinkTargetsToSlam"/> needs — engine-neutral so the selection is
    /// unit-testable without a grid.</summary>
    internal readonly record struct CofLinkRow(bool IsLink, bool IsFolderLink, Guid Target);

    /// <summary>Pure: the ordered, de-duplicated list of <b>link targets</b> to write when
    /// slamming a saved outfit from the Current Outfit folder. Item-links only — real items,
    /// subfolders and the COF folder-link are excluded, and a broken (targetless) link is
    /// dropped. Mirrors <c>LLAppearanceMgr::slamCategoryLinks</c> with
    /// <c>include_folder_links = false</c>.</summary>
    internal static List<Guid> SelectCofLinkTargetsToSlam(IEnumerable<CofLinkRow> rows)
    {
        var outp = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var r in rows)
        {
            if (!r.IsLink || r.IsFolderLink || r.Target == Guid.Empty) continue;
            if (!seen.Add(r.Target)) continue;
            outp.Add(r.Target);
        }
        return outp;
    }

    /// <summary>Replaces an existing outfit folder's contents with what is worn right now.
    /// FEAT-INV-04.
    ///
    /// <para>On any grid with AISv3 (real Second Life) this is <b>one atomic "slam"</b> that
    /// rewrites the outfit folder's entire link set from the resolved Current-Outfit-Folder
    /// links — exactly <c>LLAppearanceMgr::updateBaseOutfit → slamCategoryLinks →
    /// AISAPI::SlamFolder</c> in the reference viewer. No delete pass and no per-item
    /// <c>CreateInventory</c> POST (each of which AIS can reject on its own — link-to-link, an
    /// unresolved target, an item already sitting in the folder — which is what produced the
    /// <c>warn: Create inventory in … Bad Request</c> pairs that silently dropped clothing from a
    /// saved outfit). Real (non-link) items already in the folder are left untouched because a
    /// slam only rewrites links. Returns the number of links written, or <c>-1</c> if the COF is
    /// not fully loaded yet (the caller shows "try again in a moment" rather than slam a
    /// truncated outfit — the failure mode <c>v0.20.36</c> was created to prevent).</para>
    ///
    /// <para>OpenSim and other AIS-less grids fall through to the legacy path: delete the
    /// folder's links (<c>RemoveItemsAsync</c>, not <c>MoveItem → Trash</c> which 400s on SL),
    /// then re-link the worn set, skipping any worn item already present as a real item in the
    /// folder.</para></summary>
    public async Task<int> ReplaceOutfitWithCurrentAsync(Guid outfitFolderId, CancellationToken ct = default)
    {
        if (outfitFolderId == Guid.Empty) return 0;
        var folderUuid = new LibreMetaverse.UUID(outfitFolderId);

        if (_client.AisClient?.IsAvailable == true)
        {
            var slammed = await SlamOutfitLinksFromCofAsync(folderUuid, ct).ConfigureAwait(false);
            return slammed ?? -1; // null == COF still loading
        }

        return await ReplaceOutfitLegacyAsync(folderUuid, outfitFolderId, ct).ConfigureAwait(false);
    }

    /// <summary>The Firestorm "Save Outfit" mechanism: take the resolved Current-Outfit-Folder
    /// links and PUT them as <paramref name="outfitFolder"/>'s entire link set in one AIS
    /// request (<c>AISAPI::SlamFolder</c> → <c>PUT {cap}/category/{id}/links</c>, body a bare
    /// LLSD array of <c>{name, desc, linked_id, type}</c> maps — matched to
    /// <c>LLAppearanceMgr::slamCategoryLinks</c>). Returns the link count, or <c>null</c> when
    /// the COF is not fully resolved in the store yet.</summary>
    private async Task<int?> SlamOutfitLinksFromCofAsync(LibreMetaverse.UUID outfitFolder, CancellationToken ct)
    {
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        if (cofUuid == LibreMetaverse.UUID.Zero) return null;

        // Authoritative refetch so the slam list is not a stale local snapshot.
        try { await FetchInventoryChildrenAsync(cofUuid.Guid, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { /* fall back to whatever the store already holds */ }

        var store = _client.Inventory.Store;
        var cofNode = cofUuid != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cofUuid) : null;
        if (cofNode == null) return null;

        var rows = cofNode.Nodes.Values.Select(n =>
        {
            if (n.Data is not LibreMetaverse.InventoryItem li || !li.IsLink())
                return new CofLinkRow(false, false, Guid.Empty);
            var t = li.ResolvedItemID != LibreMetaverse.UUID.Zero ? li.ResolvedItemID : li.AssetUUID;
            return new CofLinkRow(true, li.AssetType == LibreMetaverse.AssetType.LinkFolder, t.Guid);
        });
        var targets = SelectCofLinkTargetsToSlam(rows);
        if (targets.Count == 0)
        {
            Console.Error.WriteLine("[Outfits] slam aborted — no resolvable item-links in the Current Outfit folder");
            return null;
        }

        // Readiness gate (mirrors CleanUpCurrentOutfit's storeReady): every target node must be
        // in the store, or the list we just built could be missing links that haven't streamed in.
        int unresolved = targets.Count(g =>
            store?.GetNodeOrDefault(new LibreMetaverse.UUID(g))?.Data is not LibreMetaverse.InventoryItem);
        if (unresolved > 0)
        {
            Console.Error.WriteLine(
                $"[Outfits] slam deferred — COF still loading ({unresolved}/{targets.Count} link targets not in the store)");
            return null;
        }

        var contents = new OSDArray();
        foreach (var g in targets)
        {
            var target = new LibreMetaverse.UUID(g);
            var name = (store?.GetNodeOrDefault(target)?.Data as LibreMetaverse.InventoryItem)?.Name ?? string.Empty;
            contents.Add(new OSDMap
            {
                ["name"] = OSD.FromString(name),
                ["desc"] = OSD.FromString(string.Empty),
                ["linked_id"] = OSD.FromUUID(target),
                ["type"] = OSD.FromInteger((int)LibreMetaverse.AssetType.Link),
            });
        }

        bool ok = await _client.AisClient.SlamFolderAsync(outfitFolder, contents, ct).ConfigureAwait(false);
        if (!ok)
            throw new InvalidOperationException($"AIS rejected the outfit slam for {outfitFolder}");

        // Reconcile the local store with what the server now holds.
        try { await FetchInventoryChildrenAsync(outfitFolder.Guid, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { }

        Console.Error.WriteLine($"[Outfits] slammed {contents.Count} link(s) into outfit {outfitFolder} from the Current Outfit folder");
        return contents.Count;
    }

    /// <summary>Pre-AISv3 replace path (OpenSim): delete the outfit folder's links, then re-link
    /// the current worn set. See <see cref="ReplaceOutfitWithCurrentAsync"/>.</summary>
    private async Task<int> ReplaceOutfitLegacyAsync(
        LibreMetaverse.UUID folderUuid, Guid outfitFolderId, CancellationToken ct)
    {
        var worn = await GetWornItemsWithNamesAsync(ct).ConfigureAwait(false);

        IReadOnlyList<InventoryEntry> existing;
        try { existing = await FetchInventoryChildrenAsync(outfitFolderId, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { existing = Array.Empty<InventoryEntry>(); }

        var (linkGuids, nonLinkItemIds) = SelectOutfitLinksToClear(existing);
        if (nonLinkItemIds.Count > 0)
            Console.Error.WriteLine(
                $"[Outfits] outfit {outfitFolderId} holds {nonLinkItemIds.Count} real item(s), not links — " +
                "leaving them in place; replace only rewrites the outfit's links");

        if (linkGuids.Count > 0)
        {
            var linkIds = linkGuids.Select(g => new LibreMetaverse.UUID(g)).ToList();
            try { await _client.Inventory.RemoveItemsAsync(linkIds, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Console.Error.WriteLine($"[Outfits] RemoveItemsAsync threw: {ex.Message}"); }
        }

        var skip = new HashSet<Guid>(nonLinkItemIds);
        return await LinkWornIntoAsync(folderUuid, worn, skip, ct).ConfigureAwait(false);
    }

    /// <summary>The contents of a saved outfit folder as resolved <see cref="WornItem"/>s — each
    /// link's <b>target</b> name / asset type (not the link's own), category, and whether that
    /// item is worn right now (<c>Live</c>). Fires <c>RequestFetchInventory</c> for any target the
    /// store doesn't have yet, so a second call fills the gaps. FEAT-INV-04.</summary>
    public async Task<IReadOnlyList<WornItem>> GetOutfitContentsAsync(Guid outfitFolderId, CancellationToken ct = default)
    {
        var children = await FetchInventoryChildrenAsync(outfitFolderId, ct).ConfigureAwait(false);
        var store = _client.Inventory.Store;

        // Match the outfit's links against what is worn by id AND by underlying asset. The asset
        // fallback covers a #Library item: the outfit links our owned COPY, but the avatar may be
        // wearing the Library original (or a different copy) — same AssetUUID — and a plain id
        // compare would show it as "not worn" (reported live: worn boots/hair not marked).
        var wornNow = new HashSet<Guid>();
        var wornAssets = new HashSet<Guid>();
        foreach (var w in GetWornItems())
        {
            if (!w.Live || w.ItemId == Guid.Empty) continue;
            wornNow.Add(w.ItemId);
            if (store?.GetNodeOrDefault(new LibreMetaverse.UUID(w.ItemId))?.Data is LibreMetaverse.InventoryItem wi
                && wi.AssetUUID != LibreMetaverse.UUID.Zero)
                wornAssets.Add(wi.AssetUUID.Guid);
        }

        var result = new List<WornItem>();
        var toFetch = new Dictionary<LibreMetaverse.UUID, LibreMetaverse.UUID>();
        var seen = new HashSet<Guid>();

        foreach (var e in children)
        {
            if (e.IsFolder) continue;

            var targetId = e.IsLink && e.LinkTargetId != Guid.Empty ? e.LinkTargetId : e.Id;
            if (targetId != Guid.Empty && !seen.Add(targetId)) continue; // dedup duplicate links
            var targetUuid = new LibreMetaverse.UUID(targetId);
            var target = store?.GetNodeOrDefault(targetUuid)?.Data as LibreMetaverse.InventoryItem;

            int assetType = target != null ? (int)target.AssetType : e.AssetType;
            string name = target?.Name ?? string.Empty;
            // The link's own name is only useful if it isn't the "Link" placeholder.
            if (name.Length == 0 && !string.Equals(e.Name, "Link", StringComparison.OrdinalIgnoreCase))
                name = e.Name;
            if (name.Length == 0 && targetUuid != LibreMetaverse.UUID.Zero
                && _wornDetailFetchRequested.Add(targetUuid.Guid))
                toFetch[targetUuid] = _client.Self.AgentID;

            var cat = assetType == (int)LibreMetaverse.AssetType.Bodypart ? WornCategory.BodyPart
                : assetType == (int)LibreMetaverse.AssetType.Clothing ? WornCategory.Clothing
                : assetType == (int)LibreMetaverse.AssetType.Object ? WornCategory.Attachment
                : WornCategory.Clothing; // unresolved link — usually a wearable; refines once fetched

            bool live = wornNow.Contains(targetId)
                || (target != null && target.AssetUUID != LibreMetaverse.UUID.Zero
                    && wornAssets.Contains(target.AssetUUID.Guid));
            result.Add(new WornItem(targetId, name, cat, null, assetType, Live: live));
        }

        if (toFetch.Count > 0)
        {
            try { _client.Inventory.RequestFetchInventory(toFetch); } catch { }
        }
        return result;
    }

    /// <summary>
    /// FEAT-INV-05: removes one item from ONE saved outfit — the "Aus diesem Outfit entfernen"
    /// action. Deletes the <b>link</b> to <paramref name="itemId"/> inside
    /// <paramref name="outfitFolderId"/> and nothing else: not the inventory item, not the same
    /// item's link in any other outfit, and not its Current-Outfit link (taking a thing off is
    /// <see cref="DetachItemAsync"/>, a different verb the menu offers separately).
    ///
    /// <para><b>Only links are ever deleted.</b> An outfit folder normally holds nothing else, but
    /// if a real item has been dropped into one, deleting it would destroy inventory over a menu
    /// entry that promises to edit an outfit — so a non-link match is refused and reported instead.
    /// </para>
    ///
    /// <para><c>RemoveItemsAsync</c>, not <c>MoveItem → Trash</c>: a move 400s on SL and the entry
    /// simply reappears on the next refetch (BUG-INV-01 / v0.20.33 switched the Current-Outfit
    /// cleanup off that same path for the same reason). Returns how many links were removed.</para>
    /// </summary>
    /// <summary>The decision half of <see cref="RemoveItemFromOutfitFolderAsync"/>, pure so it can
    /// be tested without a grid: which of a folder's entries are LINKS to <paramref name="itemId"/>
    /// (all of them — a duplicate link left behind looks like the action failed), and how many
    /// matches were real items rather than links, which the caller must refuse to delete.</summary>
    internal static (List<Guid> LinkIds, int NonLinkMatches) SelectOutfitLinksToRemove(
        IEnumerable<InventoryEntry> children, Guid itemId)
    {
        var linkIds = new List<Guid>();
        int nonLinkMatches = 0;
        if (itemId == Guid.Empty) return (linkIds, 0);

        foreach (var e in children)
        {
            if (e.IsFolder) continue;
            var target = e.IsLink && e.LinkTargetId != Guid.Empty ? e.LinkTargetId : e.Id;
            if (target != itemId) continue;

            if (e.IsLink) linkIds.Add(e.Id);
            else nonLinkMatches++;
        }
        return (linkIds, nonLinkMatches);
    }

    public async Task<int> RemoveItemFromOutfitFolderAsync(Guid outfitFolderId, Guid itemId, CancellationToken ct = default)
    {
        if (outfitFolderId == Guid.Empty || itemId == Guid.Empty) return 0;

        var children = await FetchInventoryChildrenAsync(outfitFolderId, ct).ConfigureAwait(false);
        var (linkGuids, nonLinkMatches) = SelectOutfitLinksToRemove(children, itemId);
        var linkIds = linkGuids.Select(g => new LibreMetaverse.UUID(g)).ToList();

        if (nonLinkMatches > 0)
            Console.Error.WriteLine(
                $"[Outfits] {itemId} sits in outfit {outfitFolderId} as a REAL item, not a link — " +
                "refusing to delete it; move it out by hand if that is what you want");

        if (linkIds.Count == 0) return 0;

        await _client.Inventory.RemoveItemsAsync(linkIds, ct).ConfigureAwait(false);
        Console.Error.WriteLine($"[Outfits] removed {linkIds.Count} link(s) to {itemId} from outfit {outfitFolderId}");
        return linkIds.Count;
    }

    /// <summary>One entry of a saved outfit reduced to what the wear ORDER depends on — its
    /// asset/wearable type and the outfit link's layer-order token. Engine-neutral so the ordering
    /// rule can be tested without a grid.</summary>
    internal readonly record struct OutfitWearRow(
        Guid ItemId, int AssetType, int WearableType, string? OrderToken, string Name);

    /// <summary>Pure: the order a saved outfit's items have to be put on in.
    ///
    /// <para><b>Body parts first.</b> Shape, skin, hair and eyes replace rather than layer, and
    /// everything else composites over them — putting them on first means the rest of the outfit
    /// is never briefly stacked onto the previous body.</para>
    ///
    /// <para><b>Then clothing, bottom layer first, per type.</b> <c>WearWearableAsync</c> gives a
    /// newly worn layer the index "however many of that type are already on", i.e. the top of the
    /// stack — so the order they are worn in <i>is</i> the resulting stack order. A saved outfit's
    /// links carry the viewer's <c>build_order_string</c> token in their description (see
    /// <see cref="WearableLayerOrder"/>), which is the stack the outfit was saved with; without
    /// this a five-layer tattoo outfit comes back in whatever order AIS happened to list it.</para>
    ///
    /// <para>Attachments last, together with anything whose target the inventory store has not
    /// resolved yet: <see cref="AttachItemAsync"/> re-classifies every item when it reaches it, so
    /// an unresolved row still takes the right path — just not a chosen position.</para></summary>
    internal static List<Guid> OrderOutfitForWearing(IEnumerable<OutfitWearRow> rows)
    {
        static bool IsBodyPart(OutfitWearRow r) => r.AssetType == (int)LibreMetaverse.AssetType.Bodypart;
        static bool IsClothing(OutfitWearRow r) => r.AssetType == (int)LibreMetaverse.AssetType.Clothing;

        var all = rows.ToList();
        var ordered = new List<Guid>(all.Count);

        ordered.AddRange(all.Where(IsBodyPart).Select(r => r.ItemId));

        foreach (var group in all.Where(IsClothing).GroupBy(r => r.WearableType))
        {
            ordered.AddRange(WearableLayerOrder
                .Sort(group, group.Key, r => r.OrderToken, r => r.Name)
                .Select(r => r.ItemId));
        }

        ordered.AddRange(all.Where(r => !IsBodyPart(r) && !IsClothing(r)).Select(r => r.ItemId));
        return ordered;
    }

    /// <summary>Reads a saved outfit folder's links out of the inventory store — the per-layer
    /// order token lives on the <b>link</b>, the target item only carries its type — and returns
    /// <paramref name="targetIds"/> in <see cref="OrderOutfitForWearing"/> order. The caller has
    /// already fetched the folder, so this costs no round trip.</summary>
    private List<Guid> OrderOutfitTargetsForWearing(Guid outfitFolderId, IEnumerable<Guid> targetIds)
    {
        var store = _client.Inventory.Store;

        var tokens = new Dictionary<Guid, string?>();
        var folderNode = store?.GetNodeOrDefault(new LibreMetaverse.UUID(outfitFolderId));
        if (folderNode != null)
        {
            foreach (var child in folderNode.Nodes.Values.ToList())
            {
                if (child.Data is not LibreMetaverse.InventoryItem link) continue;
                var target = link.IsLink() && link.ResolvedItemID != LibreMetaverse.UUID.Zero
                    ? link.ResolvedItemID
                    : link.UUID;
                if (target != LibreMetaverse.UUID.Zero) tokens[target.Guid] = link.Description;
            }
        }

        var rows = new List<OutfitWearRow>();
        foreach (var id in targetIds)
        {
            var item = store?.GetNodeOrDefault(new LibreMetaverse.UUID(id))?.Data as LibreMetaverse.InventoryItem;
            rows.Add(new OutfitWearRow(
                id,
                item != null ? (int)item.AssetType : -1,
                item is LibreMetaverse.InventoryWearable iw ? (int)iw.WearableType : -1,
                tokens.GetValueOrDefault(id),
                item?.Name ?? string.Empty));
        }
        return OrderOutfitForWearing(rows);
    }

    /// <summary>Wears a saved outfit <b>on top of</b> what is on now — the additive "Zu aktuellem
    /// Outfit hinzufügen". Attachments are attached and system wearables are put on, both through
    /// <see cref="AttachItemAsync"/>, which routes a Clothing/Bodypart layer to the COF +
    /// <c>AgentIsNowWearing</c> path. Returns how many items were sent.
    ///
    /// <para>This used to skip Clothing and Bodypart outright ("waits on FEAT-AVATAR-01 Phase 2"),
    /// from when wearing a system layer was not possible at all. It is now, and the leftover skip
    /// was the whole of "changing the skin through an outfit does nothing, doing it by hand works"
    /// (reported live 2026-09-09). The rebake that makes the change visible is debounced
    /// (<c>ScheduleRebakeAfterWearableEdit</c>), so a whole outfit still costs exactly one.</para>
    ///
    /// <para>FEAT-INV-04 / FEAT-AVATAR-01.</para></summary>
    public async Task<int> WearOutfitAsync(Guid outfitFolderId, CancellationToken ct = default)
    {
        var children = await FetchInventoryChildrenAsync(outfitFolderId, ct).ConfigureAwait(false);

        var targets = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var e in children)
        {
            if (e.IsFolder) continue;
            var target = e.IsLink && e.LinkTargetId != Guid.Empty ? e.LinkTargetId : e.Id;
            if (target != Guid.Empty && seen.Add(target)) targets.Add(target);
        }

        int sent = 0;
        foreach (var id in OrderOutfitTargetsForWearing(outfitFolderId, targets))
        {
            ct.ThrowIfCancellationRequested();
            await AttachItemAsync(id, replace: false).ConfigureAwait(false);
            sent++;
        }
        return sent;
    }

    /// <summary>Makes the avatar match a saved outfit: takes off every worn attachment and every
    /// worn clothing layer the outfit does not contain, then puts on everything the outfit has that
    /// is not already on. Items in both are left alone. Returns (removed, worn).
    ///
    /// <para><b>Body parts are never taken off.</b> An avatar always has exactly one shape, skin,
    /// hair and eyes — <c>RemoveWearableAsync</c> refuses to remove one, and an outfit that lists
    /// no skin means "keep the one you have", not "have none". The outfit's own body parts replace
    /// the worn ones in place as they go on (<c>WearWearableAsync</c> drops the old link of the
    /// same type first), which is why the skip removed here stayed invisible until an outfit was
    /// the only way someone tried to change a skin.</para>
    ///
    /// <para>Removal runs before the wear pass so a new clothing layer's stack index counts only
    /// the layers the outfit itself wants. FEAT-INV-04 / FEAT-AVATAR-01.</para>
    ///
    /// <para><b>Returns (-1, -1) when the scene has not caught up yet</b> and nothing was done —
    /// see the readiness gate below (BUG-INV-05).</para></summary>
    public async Task<(int Removed, int Worn)> ReplaceWornWithOutfitAsync(
        Guid outfitFolderId, CancellationToken ct = default)
    {
        var contents = await GetOutfitContentsAsync(outfitFolderId, ct).ConfigureAwait(false);
        if (contents.Any(w => string.IsNullOrEmpty(w.Name)))
        {
            try { await Task.Delay(800, ct).ConfigureAwait(false); } catch (OperationCanceledException) { throw; }
            contents = await GetOutfitContentsAsync(outfitFolderId, ct).ConfigureAwait(false);
        }

        // Everything the outfit references, so nothing it wants kept is taken off first.
        var targetAll = new HashSet<Guid>(contents.Select(w => w.ItemId));

        // BUG-INV-05: the removal pass reads the SCENE for "what am I wearing", and the scene is
        // the last thing to arrive after a teleport or a relogin. An empty read there does not
        // mean "nothing is on" -- it means the prims have not rezzed yet -- and taking it at face
        // value removes nothing and then wears the new outfit ON TOP of the old one. That is the
        // in-world report: "ich hab beide Outfits an", with a log showing wearable removals, no
        // detaches at all, and an accepted appearance update.
        //
        // Same rule CleanUpCurrentOutfit already applies as its `sceneReady` half, which is why
        // this bug survived: the gate existed one method away. The COF is the cross-check -- it
        // knows which attachments SHOULD be on regardless of what has rezzed, so an avatar that
        // genuinely wears no objects is not held up by this.
        var sceneAttachments = GetSceneWornAttachments();
        if (sceneAttachments.Count == 0 && CofExpectsAttachments())
        {
            // One grace period first: a second or two after arrival this resolves itself, and a
            // deferral the user has to repeat by hand is worse than a short wait.
            try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { throw; }
            sceneAttachments = GetSceneWornAttachments();
        }
        if (sceneAttachments.Count == 0 && CofExpectsAttachments())
        {
            Console.Error.WriteLine(
                "[Outfit] replace deferred -- the Current Outfit folder lists attachments but none " +
                "have rezzed yet; wearing now would leave both outfits on. Nothing changed.");
            return (-1, -1);
        }

        int removed = 0, worn = 0;

        foreach (var id in sceneAttachments.Keys.ToList())
        {
            if (targetAll.Contains(id)) continue;
            ct.ThrowIfCancellationRequested();
            await DetachItemAsync(id).ConfigureAwait(false);
            removed++;
        }

        var wornClothing = GetWornItems()
            .Where(w => w.Live && w.Category == WornCategory.Clothing)
            .Select(w => w.ItemId)
            .ToList();
        foreach (var id in wornClothing)
        {
            if (targetAll.Contains(id)) continue;
            ct.ThrowIfCancellationRequested();
            var result = await DetachItemAsync(id).ConfigureAwait(false);
            if (result.WearableRemoved) removed++;
        }

        // Live is GetOutfitContentsAsync's own "this entry is already on", which also matches a
        // #Library original by asset id — a plain id compare would put a second copy on.
        var missing = contents.Where(w => !w.Live).Select(w => w.ItemId).ToList();
        foreach (var id in OrderOutfitTargetsForWearing(outfitFolderId, missing))
        {
            ct.ThrowIfCancellationRequested();
            await AttachItemAsync(id, replace: false).ConfigureAwait(false);
            worn++;
        }

        await SetCurrentOutfitLinkAsync(outfitFolderId, ct).ConfigureAwait(false);
        return (removed, worn);
    }

    /// <summary>Whether the Current Outfit folder references any object attachment at all.
    /// The authority for "should something be on my avatar right now", independent of whether it
    /// has rezzed — which is exactly what makes it usable as a cross-check against the scene
    /// (BUG-INV-05).</summary>
    /// <remarks>
    /// Counts links whether or not they are live: an entry that is referenced but not live is the
    /// very case this exists to catch. HUD points count too — a HUD is an attachment and rezzes on
    /// the same schedule.
    /// </remarks>
    private bool CofExpectsAttachments()
    {
        try
        {
            foreach (var w in GetWornItems())
                if (w.Category is WornCategory.Attachment or WornCategory.Hud) return true;
        }
        catch { }
        return false;
    }

    /// <summary>Points the Current Outfit Folder at a saved outfit — trashes any existing
    /// folder-link in the COF and creates one to <paramref name="outfitFolderId"/>. This is the
    /// marker SL / Firestorm (and <see cref="GetSavedOutfitsAsync"/>) use for "the outfit you're
    /// wearing". FEAT-INV-04.</summary>
    private async Task SetCurrentOutfitLinkAsync(Guid outfitFolderId, CancellationToken ct)
    {
        if (outfitFolderId == Guid.Empty) return;
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        if (cofUuid == LibreMetaverse.UUID.Zero) return;

        try
        {
            await FetchInventoryChildrenAsync(cofUuid.Guid, ct).ConfigureAwait(false);
            var cofNode = _client.Inventory.Store?.GetNodeOrDefault(cofUuid);
            if (cofNode != null)
            {
                // DELETE, not MoveItem → Trash: moving a COF link 400s on AIS (SL) and the link
                // stays put -- same reason BUG-INV-01 switched the other COF cleanups off that path.
                var oldFolderLinks = cofNode.Nodes.Values.ToList()
                    .Where(n => n.Data is LibreMetaverse.InventoryItem it
                                && it.AssetType == LibreMetaverse.AssetType.LinkFolder)
                    .Select(n => n.Data!.UUID)
                    .ToList();
                if (oldFolderLinks.Count > 0)
                    await _client.Inventory.RemoveItemsAsync(oldFolderLinks, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        var name = (_client.Inventory.Store?.GetNodeOrDefault(new LibreMetaverse.UUID(outfitFolderId))?.Data
            as LibreMetaverse.InventoryFolder)?.Name ?? "Outfit";
        try
        {
            await _client.Inventory.CreateLinkAsync(
                cofUuid, new LibreMetaverse.UUID(outfitFolderId), name, string.Empty,
                LibreMetaverse.InventoryType.Folder, LibreMetaverse.UUID.Zero, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    /// <summary>Takes a saved outfit off — detaches every currently worn attachment the outfit
    /// contains and removes every worn clothing layer it contains. Body parts are left on: an
    /// avatar always has exactly one shape, skin, hair and eyes. Returns how many came off.
    /// FEAT-INV-04.</summary>
    public async Task<int> RemoveOutfitFromWornAsync(Guid outfitFolderId, CancellationToken ct = default)
    {
        var contents = await GetOutfitContentsAsync(outfitFolderId, ct).ConfigureAwait(false);
        var ids = new HashSet<Guid>(contents.Select(w => w.ItemId));
        var wornAttach = GetSceneWornAttachments().Keys.ToHashSet();

        int removed = 0;
        foreach (var id in wornAttach)
        {
            if (!ids.Contains(id)) continue;
            ct.ThrowIfCancellationRequested();
            await DetachItemAsync(id).ConfigureAwait(false);
            removed++;
        }

        // FEAT-AVATAR-01: the outfit's system wearables too. This used to stop at attachments
        // ("Kleidung & Körper unverändert (Phase 2)") only because a wearable could not be removed
        // at all -- DetachAttachmentIntoInv is a server-side no-op for a Clothing/Bodypart layer.
        // DetachItemAsync now routes those through the COF + AgentIsNowWearing path instead, so the
        // limitation is gone. Only the ones actually worn: an outfit lists what it contains, and
        // GetWornItems says what is on right now.
        var wornWearables = GetWornItems()
            .Where(w => w.Live && w.Category is WornCategory.Clothing or WornCategory.BodyPart)
            .Select(w => w.ItemId)
            .ToHashSet();

        foreach (var id in wornWearables)
        {
            if (!ids.Contains(id)) continue;
            ct.ThrowIfCancellationRequested();
            var result = await DetachItemAsync(id).ConfigureAwait(false);
            if (result.WearableRemoved) removed++;
        }

        return removed;
    }

    /// <summary>Renames any inventory folder. FEAT-INV-04 named it after outfits because that was
    /// the only caller; nothing in it is outfit-specific, and FEAT-INV-08 needed the same thing for
    /// ordinary folders.</summary>
    /// <remarks>
    /// Keeps the folder's <c>PreferredType</c>. Dropping it would turn a system folder — Objects,
    /// Clothing, #Outfits — into a plain one on a rename, and the grid places new content by that
    /// type.
    /// </remarks>
    public bool RenameFolder(Guid folderId, string newName)
    {
        newName = newName?.Trim() ?? string.Empty;
        if (folderId == Guid.Empty || newName.Length == 0) return false;
        var node = _client.Inventory.Store?.GetNodeOrDefault(new LibreMetaverse.UUID(folderId));
        if (node?.Data is not LibreMetaverse.InventoryFolder f) return false;
        _client.Inventory.UpdateFolderProperties(f.UUID, f.ParentUUID, newName, f.PreferredType);
        return true;
    }

    /// <summary>Deletes any inventory folder (recoverable — on SL an AIS category delete lands it
    /// in Trash; the linked items stay in inventory). FEAT-INV-04.
    ///
    /// <para><c>RemoveFolderAsync</c> (AIS <c>DELETE {cap}/category/{id}</c> on SL, a
    /// <c>RemoveInventoryObjects</c> packet on OpenSim), <b>not</b> <c>MoveFolder → Trash</c>:
    /// a <c>parent_id</c> PATCH of an <c>#Outfits</c> subfolder HTTP-400s on SL
    /// (<c>warn: Move category … Bad Request</c>) and the outfit stayed visible — the same
    /// move-to-Trash trap BUG-INV-01 already retired for items and COF links.</para></summary>
    public bool DeleteFolder(Guid folderId)
    {
        if (folderId == Guid.Empty) return false;
        var folderUuid = new LibreMetaverse.UUID(folderId);
        if (_client.Inventory.Store?.GetNodeOrDefault(folderUuid)?.Data is not LibreMetaverse.InventoryFolder)
            return false;

        if (_client.AisClient?.IsAvailable == true)
        {
            _ = _client.Inventory.RemoveFolderAsync(folderUuid, System.Threading.CancellationToken.None);
        }
        else
        {
            if (TrashFolderId is not { } trashId || trashId == Guid.Empty) return false;
            _client.Inventory.MoveFolder(folderUuid, new LibreMetaverse.UUID(trashId));
        }

        var node = _client.Inventory.Store?.GetNodeOrDefault(folderUuid);
        if (node != null) node.Parent?.Nodes.Remove(folderUuid);
        return true;
    }

    /// <summary>Whether a folder is one the grid maintains itself — Objects, Clothing, Trash,
    /// #Outfits, Current Outfit and the rest. FEAT-INV-08.</summary>
    /// <remarks>
    /// The test is <c>PreferredType</c>, which is also what the grid routes new content by: a
    /// received object lands in the folder whose preferred type is Object, not in the one called
    /// "Objects". Renaming such a folder is therefore survivable but confusing, and deleting one
    /// takes the destination for a whole class of arrivals with it — so the UI refuses both rather
    /// than letting the user find out.
    /// </remarks>
    public bool IsSystemFolder(Guid folderId)
    {
        if (folderId == Guid.Empty) return false;
        var node = _client.Inventory.Store?.GetNodeOrDefault(new LibreMetaverse.UUID(folderId));
        return node?.Data is LibreMetaverse.InventoryFolder f
               && f.PreferredType != LibreMetaverse.FolderType.None;
    }

    /// <summary>Creates a new inventory subfolder — used for the Create Landmark dialog's
    /// "new folder" affordance, but generic. Note: the 3-arg <c>CreateFolder</c> overload that
    /// takes a <c>FolderType</c> de-dupes on preferred type and would hand back the *existing*
    /// system folder of that type instead of creating a new one, so this always uses the
    /// 2-arg (plain, <c>FolderType.None</c>) overload.</summary>
    public Guid CreateInventoryFolder(Guid parentId, string name) =>
        _client.Inventory.CreateFolder(new LibreMetaverse.UUID(parentId), name).Guid;

    /// <summary>Creates a landmark asset for the agent's current region + position and uploads
    /// it as a new inventory item in <paramref name="folderId"/> (typically <see
    /// cref="LandmarksFolderId"/> or a subfolder of it). Requires the grid's
    /// <c>NewFileAgentInventory</c> CAP — present on modern OpenSim/SL, but reported as a
    /// neutral failure rather than thrown if missing, same as <see cref="LoginAsync"/>.</summary>
    public async Task<LandmarkCreateResult> CreateLandmarkHereAsync(
        string name, string description, Guid folderId, CancellationToken ct = default)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null) return new LandmarkCreateResult(false, null, null, "Not connected.");

        try
        {
            // The official viewer sends a CreateInventoryItem UDP request (wrapped here by CreateItemAsync)
            // for landmarks. It does NOT upload an AssetLandmark via CAPS. When the grid receives
            // a CreateInventoryItem request for AssetType.Landmark, it automatically generates the
            // landmark asset using the agent's current region and position, then creates the item.
            var item = await _client.Inventory.CreateItemAsync(
                new LibreMetaverse.UUID(folderId),
                name,
                description,
                AssetType.Landmark,
                LibreMetaverse.UUID.Zero, // No asset upload transaction
                InventoryType.Landmark,
                PermissionMask.Copy | PermissionMask.Transfer,
                ct).ConfigureAwait(false);

            if (item != null)
            {
                // The item created via UDP is immediately valid and carries the correct AssetType/InventoryType
                // on the server, avoiding CAPS asset-type corruption.
                return new LandmarkCreateResult(true, item.UUID.Guid, item.AssetUUID.Guid, "Success");
            }
            return new LandmarkCreateResult(false, null, null, "Failed to create landmark (no response).");
        }
        catch (Exception ex)
        {
            return new LandmarkCreateResult(false, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Gets the detailed properties of an inventory item from the local store cache.
    /// Returns null if not found or if the item is a folder.
    /// </summary>
    public InventoryItemProperties? GetItemProperties(Guid itemId)
    {
        var node = _client.Inventory.Store?.GetNodeFor(new LibreMetaverse.UUID(itemId));
        if (node?.Data is LibreMetaverse.InventoryItem item)
        {
            var next = item.Permissions.NextOwnerMask;
            return new InventoryItemProperties(
                itemId,
                item.Name,
                item.Description,
                next.HasFlag(LibreMetaverse.PermissionMask.Copy),
                next.HasFlag(LibreMetaverse.PermissionMask.Modify),
                next.HasFlag(LibreMetaverse.PermissionMask.Transfer)
            );
        }
        return null;
    }

    /// <summary>
    /// Updates an inventory item's name, description, and next owner permissions.
    /// </summary>
    public void UpdateItemProperties(Guid itemId, string newName, string newDescription, bool nextCopy, bool nextModify, bool nextTransfer)
    {
        if (!_client.Network.Connected) return;

        var node = _client.Inventory.Store?.GetNodeFor(new LibreMetaverse.UUID(itemId));
        if (node?.Data is LibreMetaverse.InventoryItem item)
        {
            item.Name = newName;
            item.Description = newDescription;

            // Build new next-owner mask
            uint nextOwnerMask = 0;
            if (nextCopy) nextOwnerMask |= (uint)LibreMetaverse.PermissionMask.Copy;
            if (nextModify) nextOwnerMask |= (uint)LibreMetaverse.PermissionMask.Modify;
            if (nextTransfer) nextOwnerMask |= (uint)LibreMetaverse.PermissionMask.Transfer;

            // Only update next-owner permissions; others shouldn't be touched by UI directly yet
            var perms = item.Permissions;
            perms.NextOwnerMask = (LibreMetaverse.PermissionMask)nextOwnerMask;
            item.Permissions = perms;

            _client.Inventory.RequestUpdateItem(item);
        }
    }

    private volatile bool _inventoryStoreIndexed;
}
