using System;
using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>
/// BUG-AVATAR-23: Governs outfit unparking and recovery of unfinished worn items.
/// When an avatar is promoted from parked state (BUG-PERF-16), items that are already
/// finished remain untouched ("instantaneous unpark, no rebuild"), while items that
/// were interrupted mid-build (anchor created but mesh/rig dropped or never committed)
/// or arrived while reduced are identified for re-requesting.
/// </summary>
public static class ParkedOutfitPolicy
{
    /// <summary>
    /// Describes the visual state of a worn attachment.
    /// </summary>
    /// <param name="EntityId">Attachment entity UUID.</param>
    /// <param name="IsRigged">True if a rigged instance or merged bucket is active on wearer.</param>
    /// <param name="HasAnchor">True if a BoneAttachment3D anchor node exists for this item.</param>
    /// <param name="AnchorHasMesh">True if the anchor node has a built mesh child.</param>
    public readonly record struct WornItemState(Guid EntityId, bool IsRigged, bool HasAnchor, bool AnchorHasMesh);

    /// <summary>
    /// Determines whether a worn attachment has a completed visual representation.
    /// An anchor alone (without a built mesh or rigged instance) does not count as finished.
    /// </summary>
    public static bool IsWornItemFinished(bool isRigged, bool hasAnchor, bool anchorHasMesh) =>
        isRigged || (hasAnchor && anchorHasMesh);

    /// <summary>
    /// Determines whether a worn attachment must be re-requested upon unpark.
    /// </summary>
    public static bool ShouldReRequestOnUnpark(bool isRigged, bool hasAnchor, bool anchorHasMesh) =>
        !IsWornItemFinished(isRigged, hasAnchor, anchorHasMesh);

    /// <summary>
    /// Filters a collection of worn item states and returns the entity IDs of items needing unpark re-requests.
    /// </summary>
    public static IReadOnlyList<Guid> FindItemsNeedingUnparkReRequest(IEnumerable<WornItemState> items)
    {
        var result = new List<Guid>();
        foreach (var item in items)
        {
            if (ShouldReRequestOnUnpark(item.IsRigged, item.HasAnchor, item.AnchorHasMesh))
            {
                result.Add(item.EntityId);
            }
        }
        return result;
    }
}

