using System;

namespace SLNG.Core.Landmarks;

/// <summary>
/// Engine- and protocol-neutral representation of a landmark item in the inventory,
/// including its parent folder context and creation date for grouping and deduplication.
/// </summary>
public sealed record LandmarkInventoryItem(
    Guid Id,
    Guid ParentId,
    Guid AssetId,
    string Name,
    string FolderName,
    string FolderPath,
    DateTime CreationDate = default)
{
    public override string ToString() => $"Landmark({Name}, Folder={FolderPath}, Asset={AssetId})";
}
