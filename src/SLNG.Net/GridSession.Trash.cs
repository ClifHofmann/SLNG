using LibreMetaverse;
using LibreMetaverse.Packets;

namespace SLNG.Net;

// GridSession, Trash part. FEAT-INV-10.
//
// Since BUG-INV-09 a Delete moves things into the Trash, which made the Trash the one place
// nothing ever left. This is the rest of the reference viewer's Trash: empty it, delete one thing
// in it for good, and take one thing back out.
public sealed partial class GridSession
{
    /// <summary>Upper bound on folders read while counting the Trash for the "empty it?" prompt.
    /// Beyond it the count is reported as a lower bound rather than waited for.</summary>
    private const int MaxTrashFolderFetches = 200;

    /// <summary>Whether this id is the agent's Trash folder — checked against the folder itself,
    /// not taken on trust from <see cref="TrashFolderId"/>. See <see cref="VerifiedTrashFolder"/>.</summary>
    public bool IsTrashFolder(Guid id)
        => id != Guid.Empty && VerifiedTrashFolder(_client.Inventory.Store, new UUID(id)) != UUID.Zero;

    /// <summary>Whether something sits inside the Trash folder, at any depth. The Trash itself is
    /// not inside itself.</summary>
    public bool IsInTrash(Guid id)
    {
        if (id == Guid.Empty) return false;
        var store = _client.Inventory.Store;
        var trash = VerifiedTrash();
        return trash != UUID.Zero && IsBelow(store, new UUID(id), trash);
    }

    /// <summary>
    /// Counts what is in the Trash, loading any of its folders that have never been fetched first,
    /// so the "empty it?" question can say how much is about to go.
    /// </summary>
    /// <remarks>
    /// The reference viewer counts its local model, which its background fetch has filled by then.
    /// Ours is filled the same way (FEAT-INV-07), so this is normally free; the walk only costs
    /// anything for a folder that fetch has not reached yet.
    /// </remarks>
    /// <returns>Null when there is no inventory yet or no Trash folder we can be sure of.</returns>
    public async Task<TrashSummary?> GetTrashSummaryAsync(CancellationToken ct = default)
    {
        var store = _client.Inventory.Store;
        var trash = VerifiedTrash();
        if (store == null || trash == UUID.Zero) return null;

        bool truncated = false;
        var pending = new Queue<Guid>();
        pending.Enqueue(trash.Guid);
        for (int fetches = 0; pending.Count > 0; fetches++)
        {
            if (fetches >= MaxTrashFolderFetches) { truncated = true; break; }
            ct.ThrowIfCancellationRequested();
            foreach (var child in await FetchInventoryChildrenAsync(pending.Dequeue(), ct).ConfigureAwait(false))
                if (child.IsFolder) pending.Enqueue(child.Id);
        }

        var summary = SummarizeBelow(store, trash, LiveWornItemIds());
        return truncated ? summary with { Complete = false } : summary;
    }

    /// <summary>
    /// Deletes everything in the Trash for good. The caller asks first — this does not.
    /// </summary>
    /// <remarks>
    /// <para><b>Does not use LibreMetaverse's <c>EmptyTrashAsync</c> or
    /// <c>RemoveDescendantsAsync</c>,</b> for three reasons found in the pinned 3.1.6 source.
    /// Both drop the Trash folder <i>itself</i> from the local store afterwards: their
    /// <c>RemoveLocalUi(folder)</c> removes the node it is given along with its descendants
    /// (InventoryManager.cs:1054-1131). With the Trash gone, <c>FindFolderForType(Trash)</c> falls
    /// back to the inventory root, and the next Delete would have moved things to the root instead
    /// of the Trash. The AIS branch of <c>EmptyTrashAsync</c> also reports success when the request
    /// threw (InventoryAISClient.cs:904-927). And its UDP branch sends <c>RemoveInventoryObjects</c>
    /// for only the children the local store happens to know, so an unopened subfolder's contents
    /// would survive.</para>
    ///
    /// <para>What is sent instead is what the reference viewer sends: an AIS
    /// <c>DELETE {cap}/category/{trash}/children</c> (<c>purge_descendents_of</c> →
    /// <c>AISAPI::PurgeDescendents</c>, llviewerinventory.cpp:1648-1702, llaisapi.cpp:300-330).
    /// Where there is no AIS — OpenSim — the legacy <c>PurgeInventoryDescendents</c>, which OpenSim
    /// runs as <c>PurgeFolder</c> and accepts only for the Trash or Lost and Found
    /// (XInventoryService <c>ParentIsTrashOrLost</c>).</para>
    ///
    /// <para><b>The Trash id is verified, never taken on trust.</b> LibreMetaverse answers "no such
    /// folder" with the inventory root, and a purge of the root's descendants is the whole
    /// inventory. See <see cref="VerifiedTrashFolder"/>.</para>
    /// </remarks>
    public async Task<TrashPurgeResult> EmptyTrashAsync(CancellationToken ct = default)
    {
        if (!_client.Network.Connected) return TrashPurgeResult.Unavailable;
        var store = _client.Inventory.Store;
        var trash = VerifiedTrash();
        if (store == null || trash == UUID.Zero) return TrashPurgeResult.Unavailable;

        var summary = SummarizeBelow(store, trash, LiveWornItemIds());
        // Checked again here, not only in the prompt: something can be put on between the question
        // and the answer, and a worn attachment whose item has been purged has nowhere to go back to.
        if (summary.WornNames.Count > 0) return TrashPurgeResult.WornItemsInside;
        // The viewer's purge_descendents_of returns early on CHILDREN_NO, and so does this. An
        // unfetched Trash is not known to be empty and is purged all the same.
        if (summary.IsEmpty && summary.Complete) return TrashPurgeResult.NothingToDo;

        bool ais = _client.AisClient.IsAvailable;
        if (ais)
        {
            if (!await _client.AisClient.PurgeDescendentsAsync(trash, ct).ConfigureAwait(false))
            {
                Console.Error.WriteLine($"[Inventory] PurgeDescendents {trash} (Trash) refused by AIS");
                return TrashPurgeResult.GridRefused;
            }
        }
        else
        {
            _client.Network.SendPacket(new PurgeInventoryDescendentsPacket
            {
                AgentData = { AgentID = _client.Self.AgentID, SessionID = _client.Self.SessionID },
                InventoryData = { FolderID = trash },
            });
        }

        // UDP has no reply, so the local store is brought in line straight away — the old viewer's
        // UDP path did the same, "because there is no callback mechanism".
        int dropped = ForgetContents(store, trash);
        Console.Error.WriteLine(
            $"[Inventory] PurgeDescendents {trash} (Trash) via {(ais ? "AIS" : "UDP")}: " +
            $"{summary.Items} item(s), {summary.Folders} folder(s); {dropped} entr(y/ies) dropped locally");
        return TrashPurgeResult.Purged;
    }

    /// <summary>
    /// Deletes one item or folder in the Trash for good — the viewer's "Purge Item". The caller
    /// asks first — this does not.
    /// </summary>
    /// <remarks>
    /// Refuses anything outside the Trash, on purpose and not only in the menu: OpenSim deletes an
    /// ITEM wherever it is (<c>XInventoryService.DeleteItems</c> has no Trash check, unlike
    /// <c>DeleteFolders</c>), so this method is the last place that can say no. The reference viewer
    /// offers "Purge Item" only inside the Trash and disables it for anything worn
    /// (<c>addTrashContextMenuOptions</c>, llinventorybridge.cpp:1062-1080;
    /// <c>get_is_item_removable</c>, llinventoryfunctions.cpp:738-771).
    /// </remarks>
    public async Task<TrashPurgeResult> PurgeFromTrashAsync(Guid id, CancellationToken ct = default)
    {
        if (!_client.Network.Connected) return TrashPurgeResult.Unavailable;
        var store = _client.Inventory.Store;
        var trash = VerifiedTrash();
        if (store == null || trash == UUID.Zero) return TrashPurgeResult.Unavailable;

        var uuid = new UUID(id);
        if (!IsBelow(store, uuid, trash)) return TrashPurgeResult.NotInTrash;
        if (WornBelow(store, uuid, LiveWornItemIds()).Count > 0) return TrashPurgeResult.WornItemsInside;

        bool isFolder = store.GetNodeOrDefault(uuid)?.Data is InventoryFolder;
        bool ais = _client.AisClient.IsAvailable;
        if (ais)
        {
            // AISAPI::RemoveCategory / RemoveItem: DELETE {cap}/category/{id} or {cap}/item/{id}
            // (llaisapi.cpp:182-240). A 410 counts as done -- it is already gone.
            bool ok = isFolder
                ? await _client.AisClient.RemoveCategoryAsync(uuid, ct).ConfigureAwait(false)
                : await _client.AisClient.RemoveItemAsync(uuid, ct).ConfigureAwait(false);
            if (!ok)
            {
                Console.Error.WriteLine($"[Inventory] Remove{(isFolder ? "Category" : "Item")} {uuid} refused by AIS");
                return TrashPurgeResult.GridRefused;
            }
        }
        else
        {
            _client.Network.SendPacket(new RemoveInventoryObjectsPacket
            {
                AgentData = { AgentID = _client.Self.AgentID, SessionID = _client.Self.SessionID },
                FolderData = isFolder
                    ? new[] { new RemoveInventoryObjectsPacket.FolderDataBlock { FolderID = uuid } }
                    : Array.Empty<RemoveInventoryObjectsPacket.FolderDataBlock>(),
                ItemData = isFolder
                    ? Array.Empty<RemoveInventoryObjectsPacket.ItemDataBlock>()
                    : new[] { new RemoveInventoryObjectsPacket.ItemDataBlock { ItemID = uuid } },
            });
        }

        // RemoveNodeFor takes the node's whole subtree with it.
        if (store.GetNodeOrDefault(uuid)?.Data is { } gone) store.RemoveNodeFor(gone);
        Console.Error.WriteLine(
            $"[Inventory] Purge{(isFolder ? "Folder" : "Item")} {uuid} via {(ais ? "AIS" : "UDP")}");
        return TrashPurgeResult.Purged;
    }

    /// <summary>Where <see cref="RestoreFromTrash"/> would put this, or <see cref="Guid.Empty"/> if
    /// it is not in the Trash. For a menu that says where before the user picks it.</summary>
    public Guid RestoreDestinationOf(Guid id)
    {
        var store = _client.Inventory.Store;
        var trash = VerifiedTrash();
        var uuid = new UUID(id);
        if (store == null || trash == UUID.Zero || !IsBelow(store, uuid, trash)) return Guid.Empty;
        return RestoreDestination(store, uuid).Guid;
    }

    /// <summary>
    /// Takes one item or folder out of the Trash — the viewer's "Restore Item". Returns the folder
    /// it went to, or <see cref="Guid.Empty"/> if nothing was sent.
    /// </summary>
    /// <remarks>
    /// <b>Not back to where it came from.</b> Nobody knows that: the grid keeps no record of an
    /// item's previous folder, and neither does the reference viewer. Its Restore sends an item to
    /// the system folder for its type — Objects, Clothing, Photo Album for a snapshot — and a folder
    /// to the top of the inventory (<c>LLItemBridge::restoreItem</c>, llinventorybridge.cpp:1953-1967;
    /// <c>LLFolderBridge::restoreItem</c>, :3884-3895). This does the same. It is an ordinary move,
    /// so it goes out as the UDP reparent every move here uses, and without restamping the date.
    /// </remarks>
    public Guid RestoreFromTrash(Guid id)
    {
        if (!_client.Network.Connected) return Guid.Empty;
        var store = _client.Inventory.Store;
        var trash = VerifiedTrash();
        var uuid = new UUID(id);
        if (store == null || trash == UUID.Zero || !IsBelow(store, uuid, trash)) return Guid.Empty;

        var destination = RestoreDestination(store, uuid);
        if (destination == UUID.Zero) return Guid.Empty;

        bool isFolder = store.GetNodeOrDefault(uuid)?.Data is InventoryFolder;
        string label = store.GetNodeOrDefault(destination)?.Data?.Name ?? string.Empty;
        _ = MoveInventoryAsync(id, destination.Guid, isFolder, label, restamp: false);
        return destination.Guid;
    }

    /// <summary>Names of the things at or below this item or folder that are being worn right now.
    /// Not empty means it must not be deleted for good.</summary>
    public IReadOnlyList<string> WornNamesIn(Guid id)
    {
        var store = _client.Inventory.Store;
        if (store == null || id == Guid.Empty) return Array.Empty<string>();
        return WornBelow(store, new UUID(id), LiveWornItemIds());
    }

    /// <summary>The Trash folder's id, verified; <see cref="UUID.Zero"/> if there is none.</summary>
    private UUID VerifiedTrash()
    {
        var store = _client.Inventory.Store;
        if (store?.RootFolder == null) return UUID.Zero;
        return VerifiedTrashFolder(store, _client.Inventory.FindFolderForType(FolderType.Trash));
    }

    /// <summary>What the avatar is wearing right now: attachments from the scene, wearables from the
    /// live set. Not the Current Outfit links — those can be stale, and a stale link must not block
    /// emptying the Trash for ever.</summary>
    private HashSet<Guid> LiveWornItemIds()
    {
        var ids = new HashSet<Guid>(GetSceneWornAttachments().Keys);
        try
        {
            foreach (var w in _client.Appearance.GetWearables())
                if (w.ItemID != UUID.Zero) ids.Add(w.ItemID.Guid);
        }
        catch
        {
            // Appearance not set up yet: nothing is worn that we know of.
        }
        return ids;
    }

    // ---- The decisions, as pure functions over a store, so they can be pinned by tests ----------

    /// <summary>
    /// <paramref name="candidate"/> if the store holds it as a folder of type Trash, otherwise
    /// <see cref="UUID.Zero"/>.
    /// </summary>
    /// <remarks>
    /// <c>FindFolderForType</c> answers "no such folder" with the inventory ROOT rather than with
    /// nothing (InventoryManager.cs:611-633), which <see cref="TrashFolderId"/> passes on. For a move
    /// that is a nuisance; for a purge it is the whole inventory. So every destructive path checks
    /// the folder itself.
    /// </remarks>
    internal static UUID VerifiedTrashFolder(Inventory? store, UUID candidate)
    {
        if (store == null || candidate == UUID.Zero) return UUID.Zero;
        if (store.RootFolder?.UUID == candidate) return UUID.Zero;
        return store.GetNodeOrDefault(candidate)?.Data is InventoryFolder { PreferredType: FolderType.Trash }
            ? candidate
            : UUID.Zero;
    }

    /// <summary>Whether <paramref name="id"/> sits somewhere below <paramref name="ancestor"/>. The
    /// ancestor itself does not count, and neither does an id the store does not hold.</summary>
    internal static bool IsBelow(Inventory? store, UUID id, UUID ancestor)
    {
        if (store == null || ancestor == UUID.Zero || id == ancestor) return false;

        // A bound instead of trusting the parent chain: a broken tree must not hang the caller.
        int depth = 0;
        for (var n = store.GetNodeOrDefault(id)?.Parent; n != null && depth < 256; n = n.Parent, depth++)
            if (n.Data?.UUID == ancestor) return true;
        return false;
    }

    /// <summary>Counts everything below a folder and names what in there is worn. See
    /// <see cref="TrashSummary"/>.</summary>
    internal static TrashSummary SummarizeBelow(Inventory store, UUID folder, ICollection<Guid> wornItemIds)
    {
        var top = store.GetNodeOrDefault(folder);
        if (top == null) return new TrashSummary(0, 0, Complete: false, Array.Empty<string>());

        int items = 0, folders = 0;
        bool complete = !top.NeedsUpdate;
        var worn = new List<string>();
        foreach (var node in Descendants(top))
        {
            switch (node.Data)
            {
                case InventoryFolder:
                    folders++;
                    if (node.NeedsUpdate) complete = false;
                    break;
                case InventoryItem item:
                    items++;
                    if (wornItemIds.Contains(item.UUID.Guid)) worn.Add(item.Name);
                    break;
            }
        }
        return new TrashSummary(items, folders, complete, worn);
    }

    /// <summary>Names of the worn things at or below an item or folder.</summary>
    internal static List<string> WornBelow(Inventory store, UUID id, ICollection<Guid> wornItemIds)
    {
        var worn = new List<string>();
        var node = store.GetNodeOrDefault(id);
        if (node == null) return worn;

        if (node.Data is InventoryItem self)
        {
            if (wornItemIds.Contains(self.UUID.Guid)) worn.Add(self.Name);
            return worn;
        }
        foreach (var d in Descendants(node))
            if (d.Data is InventoryItem item && wornItemIds.Contains(item.UUID.Guid)) worn.Add(item.Name);
        return worn;
    }

    /// <summary>
    /// Drops everything below a folder from the local store and keeps the folder. Returns how many
    /// direct children went (each one takes its own subtree with it).
    /// </summary>
    /// <remarks>Keeping the folder is the point — see <see cref="EmptyTrashAsync"/> for what went
    /// wrong when LibreMetaverse's own purge dropped the Trash as well.</remarks>
    internal static int ForgetContents(Inventory store, UUID folder)
    {
        var node = store.GetNodeOrDefault(folder);
        if (node == null) return 0;

        int dropped = 0;
        foreach (var child in ChildrenOf(node))
        {
            if (child.Data == null) continue;
            store.RemoveNodeFor(child.Data);
            dropped++;
        }
        return dropped;
    }

    /// <summary>
    /// Which folder type a thing taken out of the Trash goes back to, as the wire value of
    /// <c>FolderType</c>. The reference viewer's rule: an item goes to the folder for its asset type
    /// (<c>LLFolderType::assetTypeToFolderType</c> is an identity cast, llfoldertype.cpp:209-216),
    /// a snapshot to the Photo Album, a folder to the top of the inventory — a folder's asset type is
    /// <c>AT_CATEGORY</c>, and that value is <c>FT_ROOT_INVENTORY</c>.
    /// </summary>
    /// <remarks>An unknown type (a negative value) goes to the top as well. Passed on as it is, -1
    /// would be <c>FolderType.None</c> — which every plain folder has, so it would have picked one of
    /// the user's own folders at random.</remarks>
    internal static int RestoreFolderTypeFor(bool isFolder, int assetType, int inventoryType)
    {
        if (isFolder || assetType < 0) return (int)FolderType.Root;
        if (inventoryType == (int)InventoryType.Snapshot) return (int)FolderType.Snapshot;
        return assetType;
    }

    /// <summary>The folder <see cref="RestoreFromTrash"/> sends a thing to. Never the Trash itself;
    /// the inventory root when nothing better exists.</summary>
    internal static UUID RestoreDestination(Inventory store, UUID id)
    {
        var root = store.RootFolder?.UUID ?? UUID.Zero;
        var data = store.GetNodeOrDefault(id)?.Data;
        if (root == UUID.Zero || data == null) return UUID.Zero;

        int folderType = data switch
        {
            InventoryFolder => RestoreFolderTypeFor(true, -1, -1),
            // The viewer asks a link for its type and gets the type of what it points at
            // (LLViewerInventoryItem::getType). A folder link is a folder; a link whose target is not
            // in the store has no type to go by.
            InventoryItem { AssetType: AssetType.LinkFolder } => RestoreFolderTypeFor(true, -1, -1),
            InventoryItem { AssetType: AssetType.Link } link =>
                store.GetNodeOrDefault(link.AssetUUID)?.Data is InventoryItem target
                    ? RestoreFolderTypeFor(false, (int)target.AssetType, (int)target.InventoryType)
                    : RestoreFolderTypeFor(false, -1, -1),
            InventoryItem item => RestoreFolderTypeFor(false, (int)item.AssetType, (int)item.InventoryType),
            _ => RestoreFolderTypeFor(false, -1, -1),
        };

        var destination = FindSystemFolder(store, folderType);
        return store.GetNodeOrDefault(destination)?.Data is InventoryFolder { PreferredType: FolderType.Trash }
            ? root
            : destination;
    }

    /// <summary>The top-level folder with this preferred type, or the inventory root if there is
    /// none — the same answer <c>FindFolderForType</c> gives, over a store the caller hands in.</summary>
    internal static UUID FindSystemFolder(Inventory store, int folderType)
    {
        var root = store.RootFolder?.UUID ?? UUID.Zero;
        if (root == UUID.Zero || folderType < 0 || folderType == (int)FolderType.Root) return root;

        var rootNode = store.GetNodeOrDefault(root);
        if (rootNode == null) return root;
        foreach (var child in ChildrenOf(rootNode))
            if (child.Data is InventoryFolder f && (int)f.PreferredType == folderType) return f.UUID;
        return root;
    }

    /// <summary>Every node below <paramref name="top"/>, not including it.</summary>
    private static IEnumerable<InventoryNode> Descendants(InventoryNode top)
    {
        var visited = new HashSet<UUID>();
        var stack = new Stack<InventoryNode>();
        stack.Push(top);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Data == null || !visited.Add(node.Data.UUID)) continue; // a loop is not a tree
            foreach (var child in ChildrenOf(node))
            {
                yield return child;
                if (child.Data is InventoryFolder) stack.Push(child);
            }
        }
    }

    /// <summary>A snapshot of a node's children, taken under the same lock the store uses.</summary>
    private static List<InventoryNode> ChildrenOf(InventoryNode node)
    {
        lock (node.Nodes.SyncRoot)
            return node.Nodes.Values.ToList();
    }
}
