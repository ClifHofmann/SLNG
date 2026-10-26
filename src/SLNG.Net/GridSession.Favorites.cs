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
}
