using System;
using SLNG.Core.ECS;

namespace SLNG.Core.Components;

/// <summary>
/// Component holding intrinsic metadata about an object.
/// </summary>
public class MetadataComponent : IComponent
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid CreatorId { get; set; } = Guid.Empty;
    public Guid OwnerId { get; set; } = Guid.Empty;
    public Guid GroupId { get; set; } = Guid.Empty;

    /// <summary>True when the object owner's Move permission has been removed -- SL's "Locked"
    /// build-floater checkbox is a UI concept, not a wire flag; the sim expresses it by omitting
    /// PermissionMask.Move from the owner's permission mask.</summary>
    public bool Locked { get; set; }

    /// <summary>Owner's Modify/Copy/Transfer permission bits. These gate what the connected
    /// agent can actually do server-side when they are the owner: e.g. OpenSim's
    /// SceneGraph.UpdatePrimFlags (Physical/Temporary/Phantom) requires Modify, while
    /// position/rotation/scale only require the separate Move permission (see Locked above) --
    /// a no-modify object accepts transform edits but silently drops flag changes.</summary>
    public bool OwnerCanModify { get; set; } = true;
    public bool OwnerCanCopy { get; set; } = true;
    public bool OwnerCanTransfer { get; set; } = true;

    public MetadataComponent() { }

    public MetadataComponent(Guid id)
    {
        Id = id;
    }
}
