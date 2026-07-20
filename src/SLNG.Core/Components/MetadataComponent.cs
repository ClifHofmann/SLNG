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

    public MetadataComponent() { }

    public MetadataComponent(Guid id)
    {
        Id = id;
    }
}
