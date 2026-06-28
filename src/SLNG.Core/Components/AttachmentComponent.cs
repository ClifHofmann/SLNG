using System;
using SLNG.Core.ECS;

namespace SLNG.Core.Components;

/// <summary>
/// Marks an entity as an object worn (attached) by an avatar.
/// The avatar is identified by its entity Guid; the bone is chosen at render time
/// from <see cref="AttachmentPoint"/> using the viewer's attachment-point-to-bone map.
/// </summary>
public class AttachmentComponent : IComponent
{
    /// <summary>Global entity ID of the avatar this object is attached to.</summary>
    public Guid AvatarEntityId { get; set; }

    /// <summary>
    /// SL AttachmentPoint enum byte value (e.g. 1 = Chest, 2 = Skull, 39 = Neck …).
    /// Zero means the point is unknown or not yet received.
    /// </summary>
    public byte AttachmentPoint { get; set; }

    public AttachmentComponent(Guid avatarEntityId, byte attachmentPoint)
    {
        AvatarEntityId = avatarEntityId;
        AttachmentPoint = attachmentPoint;
    }
}
