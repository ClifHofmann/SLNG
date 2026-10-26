namespace SLNG.Core;

/// <summary>An inventory item was taken out of the local inventory without the user deleting it -- a
/// no-copy object rezzed into the world (FEAT-INV-15). The grid removes it too; the local copy goes at
/// once so it cannot be rezzed a second time on a laggy connection. A folder showing it should refresh.</summary>
/// <param name="ItemId">The item that went.</param>
/// <param name="FolderId">The folder it sat in.</param>
public record InventoryItemRemovedEvent(Guid ItemId, Guid FolderId);
