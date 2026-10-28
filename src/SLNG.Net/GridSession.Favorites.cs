using SLNG.Core;
using SLNG.Core.Landmarks;

namespace SLNG.Net;

// GridSession, Favorites part (FEAT-UI-72, phase 1: read-only).
//
// The inventory "Favorites" system folder (folder type 23) -- the one the reference viewer's favorites bar and the
// login screen's start-location list are made from. Phase 1 only READS it: nothing here writes to the user's
// inventory, creates a folder, or sends a message to the grid beyond the ordinary folder fetches the inventory
// panel makes anyway.
public sealed partial class GridSession
{
    /// <summary>
    /// Folder id of the inventory Favorites folder, or null when there is no inventory yet or the grid has no such
    /// folder. <b>Strict</b>: it is never <c>FindFolderForType(Favorites)</c>, which LibreMetaverse answers with the
    /// inventory ROOT when the type is missing -- on a grid without the folder the whole inventory would then look
    /// like "Favorites". The decision is the pure <see cref="FavoritesFolderResolver"/>; this only feeds it the root's
    /// direct children. The folder is never created (the reference viewer does not create it either).
    /// </summary>
    public Guid? FavoritesFolderId
    {
        get
        {
            var store = _client.Inventory?.Store;
            var root = store?.RootFolder;
            if (store == null || root == null) return null;

            var rootNode = store.GetNodeOrDefault(root.UUID);
            if (rootNode == null) return null;

            try
            {
                var children = new List<(Guid Id, int PreferredType)>();
                foreach (var child in rootNode.Nodes.Values)
                {
                    if (child.Data is LibreMetaverse.InventoryFolder f)
                        children.Add((f.UUID.Guid, (int)f.PreferredType));
                }
                return FavoritesFolderResolver.Resolve(root.UUID.Guid, children);
            }
            catch (InvalidOperationException)
            {
                // The store changed under the enumeration (a network thread added a node). "Unknown right now" is a
                // true answer; the callers ask again.
                return null;
            }
        }
    }

    /// <summary>
    /// Asks the grid for the Favorites folder and every folder below it (like the viewer's
    /// <c>collectDescendentsIf</c>, capped like the landmark walks) and returns the landmark items in them.
    /// <see cref="FavoritesSnapshot.Confirmed"/> is true only when the folder exists and every folder looked at has
    /// had its contents confirmed by the grid (<c>NeedsUpdate == false</c>); a refused fetch reads as an empty folder
    /// in LibreMetaverse, so an empty-but-unconfirmed answer must never be believed.
    /// </summary>
    public async Task<FavoritesSnapshot> GetFavoritesAsync(CancellationToken ct = default)
    {
        if (FavoritesFolderId is not { } folder) return FavoritesSnapshot.Unconfirmed;

        try
        {
            var pending = new Queue<Guid>();
            pending.Enqueue(folder);
            for (int fetches = 0; pending.Count > 0 && fetches < MaxLandmarkFolderFetches; fetches++)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var child in await FetchInventoryChildrenAsync(pending.Dequeue(), ct).ConfigureAwait(false))
                    if (child.IsFolder) pending.Enqueue(child.Id);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LoginStart] the Favorites folder could not be read: {ex.Message}");
            return FavoritesSnapshot.Unconfirmed;
        }

        return ReadFavoritesFromStore();
    }

    /// <summary>
    /// The Favorites folder as the local inventory store holds it right now -- no network. The store is kept current
    /// by the grid's own inventory updates, so this reflects a favorite added in another viewer during the session
    /// once the folder has been fetched. <see cref="FavoritesSnapshot.Confirmed"/> follows the same rule as
    /// <see cref="GetFavoritesAsync"/>: every folder in the subtree must be current.
    /// </summary>
    public FavoritesSnapshot ReadFavoritesFromStore()
    {
        var store = _client.Inventory?.Store;
        if (store?.RootFolder == null || FavoritesFolderId is not { } folder) return FavoritesSnapshot.Unconfirmed;

        var trash = TrashFolderId is { } t && t != Guid.Empty ? new LibreMetaverse.UUID(t) : LibreMetaverse.UUID.Zero;

        try
        {
            var items = new List<LandmarkInventoryItem>();
            bool confirmed = true;
            var stack = new Stack<(LibreMetaverse.UUID Id, string Path)>();
            stack.Push((new LibreMetaverse.UUID(folder), ""));
            while (stack.Count > 0)
            {
                var (id, parentPath) = stack.Pop();
                var node = store.GetNodeOrDefault(id);
                if (node == null) { confirmed = false; continue; }
                if (node.NeedsUpdate) confirmed = false;

                string folderName = node.Data?.Name ?? "";
                string path = parentPath.Length == 0 ? folderName : parentPath + "/" + folderName;
                foreach (var child in node.Nodes.Values)
                {
                    switch (child.Data)
                    {
                        case LibreMetaverse.InventoryFolder f:
                            if (f.UUID != trash) stack.Push((f.UUID, path));
                            break;
                        case LibreMetaverse.InventoryItem i
                            when !i.IsLink()
                            && (i.InventoryType == LibreMetaverse.InventoryType.Landmark || i is LibreMetaverse.InventoryLandmark)
                            && i.ResolvedAssetID != LibreMetaverse.UUID.Zero:
                            items.Add(new LandmarkInventoryItem(
                                i.UUID.Guid, i.ParentUUID.Guid, i.ResolvedAssetID.Guid, i.Name, folderName, path, i.CreationDate));
                            break;
                    }
                }
            }
            return new FavoritesSnapshot(confirmed, items);
        }
        catch (InvalidOperationException)
        {
            // See FavoritesFolderId: the store changed under the walk; say "not sure" rather than half an answer.
            return FavoritesSnapshot.Unconfirmed;
        }
    }

    // ---- phase 2: the bar is this folder -----------------------------------------------------------------------------------

    /// <summary>The inventory changed in a way that can change the Favorites folder: a landmark or a folder was added,
    /// updated or removed. Raised on a network thread, often many times in a row (a login fills the whole tree), so the
    /// listener debounces and then reads <see cref="ReadFavoritesFromStore"/>, which needs no network.</summary>
    public event EventHandler? FavoritesChanged;

    private int _favoritesWatching;

    /// <summary>Starts listening to the inventory store for <see cref="FavoritesChanged"/>. Call once after login;
    /// further calls do nothing.</summary>
    public void StartFavoritesWatch()
    {
        var store = _client.Inventory?.Store;
        if (store == null || Interlocked.Exchange(ref _favoritesWatching, 1) == 1) return;
        store.InventoryObjectAdded += (_, e) => NoteFavoritesRelevant(e.Obj);
        store.InventoryObjectUpdated += (_, e) => NoteFavoritesRelevant(e.NewObject);
        store.InventoryObjectRemoved += (_, e) => NoteFavoritesRelevant(e.Obj);
    }

    private void NoteFavoritesRelevant(LibreMetaverse.InventoryBase? obj)
    {
        // A landmark, or a folder (one moved to the Trash takes its landmarks with it). Anything else cannot matter.
        bool relevant = obj is LibreMetaverse.InventoryFolder
            || obj is LibreMetaverse.InventoryLandmark
            || obj is LibreMetaverse.InventoryItem { InventoryType: LibreMetaverse.InventoryType.Landmark };
        if (relevant) FavoritesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Makes a landmark a favorite by copying it into the Favorites folder. The decision is
    /// <see cref="FavoritesAddPolicy"/>, made before anything is sent; <see cref="FavoriteAddVerdict.Add"/> means the copy was
    /// <b>requested</b>, not that it happened (<see cref="CopyItemAsync"/> explains why its result is no proof) -- the
    /// folder is read again when the grid's update arrives, and that is what shows up in the bar.
    /// </summary>
    public async Task<FavoriteAddVerdict> AddFavoriteAsync(Guid itemId, CancellationToken ct = default)
    {
        var folder = FavoritesFolderId;
        var item = _client.Inventory.Store?.GetNodeOrDefault(new LibreMetaverse.UUID(itemId))?.Data as LibreMetaverse.InventoryItem;
        bool isLandmark = item is LibreMetaverse.InventoryLandmark
            || item?.InventoryType == LibreMetaverse.InventoryType.Landmark;
        // The CURRENT owner's Copy right: a favorite is a copy and the grid refuses one the person may not make.
        bool canCopy = item != null && (item.Permissions.OwnerMask & LibreMetaverse.PermissionMask.Copy) != LibreMetaverse.PermissionMask.None;

        var verdict = FavoritesAddPolicy.Decide(
            folder != null, isLandmark, itemId, item?.ResolvedAssetID.Guid ?? Guid.Empty, canCopy,
            ReadFavoritesFromStore().Items);
        if (verdict != FavoriteAddVerdict.Add) return verdict;

        await CopyItemAsync(itemId, folder!.Value, "", ct).ConfigureAwait(false);
        return verdict;
    }

    /// <summary>Takes a favorite out of the bar by moving its copy to the Trash (what the viewer's <c>removeItem</c> does).
    /// Only the copy in the Favorites folder goes; the original in Landmarks is another item.</summary>
    public async Task RemoveFavoriteAsync(Guid itemId)
    {
        await MoveToTrashAsync(itemId, isFolder: false).ConfigureAwait(false);
        FavoritesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Creates a landmark for where the agent stands, straight in the Favorites folder. Null when the grid has no
    /// such folder.</summary>
    public async Task<LandmarkCreateResult?> AddCurrentLocationAsync(string name, CancellationToken ct = default)
    {
        if (FavoritesFolderId is not { } folder) return null;
        return await CreateLandmarkHereAsync(name, "", folder, ct).ConfigureAwait(false);
    }
}
