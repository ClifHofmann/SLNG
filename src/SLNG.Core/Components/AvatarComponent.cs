using System;
using System.Collections.Generic;
using SLNG.Core.ECS;

namespace SLNG.Core.Components;

public enum AvatarBakeIndex
{
    Head = 8,
    UpperBody = 9,
    LowerBody = 10,
    Eyes = 11,
    Skirt = 19,
    Hair = 20,
    LeftArm = 40,
    LeftLeg = 41,
    Aux1 = 42,
    Aux2 = 43,
    Aux3 = 44
}

/// <summary>
/// Represents an avatar in the world.
/// </summary>
public class AvatarComponent : IComponent
{
    public Guid AgentId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsLocalAgent { get; set; }

    /// <summary>
    /// The latest visual parameters received from AvatarAppearance.
    /// Used to deform the skeleton and meshes.
    /// </summary>
    public byte[]? VisualParams { get; set; }

    /// <summary>
    /// The latest baked textures received from AvatarAppearance.
    /// </summary>
    public Dictionary<int, Guid>? BakedTextures { get; set; }
    public List<Guid>? ActiveAnimations { get; set; }

    /// <summary>
    /// AppearanceHover Z offset from AvatarAppearance (see <see cref="SLNG.Core.AvatarAppearanceEvent"/>'s
    /// doc comment) — a per-avatar user-configured height nudge the real viewer adds directly onto
    /// the root position, on top of the halfBodySize/PelvisToFoot correction. 0 until an
    /// AvatarAppearance event has been applied.
    /// </summary>
    public float HoverOffsetZ { get; set; }

    /// <summary>DIAGNOSTIC ONLY (see <see cref="SLNG.Core.AvatarUpdateEvent"/>'s doc comment) — the
    /// avatar's own wire-transmitted Scale.Z, a quantity the real viewer treats as distinct from
    /// its own <c>computeBodySize()</c> output. Not used in any rendering/position math yet.</summary>
    public float ScaleZ { get; set; }

    public AvatarComponent(Guid agentId, string firstName, string lastName, bool isLocalAgent)
    {
        AgentId = agentId;
        FirstName = firstName;
        LastName = lastName;
        IsLocalAgent = isLocalAgent;
    }
}
