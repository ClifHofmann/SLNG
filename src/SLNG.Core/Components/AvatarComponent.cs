using System;
using SLNG.Core.ECS;

namespace SLNG.Core.Components;

/// <summary>
/// Represents an avatar in the world.
/// </summary>
public class AvatarComponent : IComponent
{
    public Guid AgentId { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public bool IsLocalAgent { get; set; }

    public AvatarComponent(Guid agentId, string firstName, string lastName, bool isLocalAgent)
    {
        AgentId = agentId;
        FirstName = firstName;
        LastName = lastName;
        IsLocalAgent = isLocalAgent;
    }
}
