using System.Numerics;
using SLNG.Core.ECS;

namespace SLNG.Core.Components;

/// <summary>
/// Component representing spatial position and orientation in the world.
/// </summary>
public class TransformComponent : IComponent
{
    /// <summary>World-space position (region-local metres). For a linked child prim this is
    /// composed from the parent; the renderer always reads this.</summary>
    public Vector3 Position { get; set; }

    /// <summary>World-space orientation.</summary>
    public Quaternion Rotation { get; set; }

    /// <summary>Position as sent by the simulator: relative to the parent for a linked child,
    /// world-space for a root. Kept so the world transform can be recomposed when the parent
    /// arrives or moves.</summary>
    public Vector3 LocalPosition { get; set; }

    /// <summary>Orientation as sent by the simulator (relative for a child).</summary>
    public Quaternion LocalRotation { get; set; }

    /// <summary>LocalID of the parent prim in a linkset, or 0 if this is a root/unlinked.</summary>
    public uint ParentLocalId { get; set; }

    /// <summary>Last network-reported world-space velocity (m/s), for avatars only. Used by
    /// WorldSimulation.ExtrapolateMovement to dead-reckon Position between packets instead of
    /// leaving it static (or, for the local agent, fighting it with client-side input prediction —
    /// see the real viewer's LLViewerObject::interpolateLinearMotion, which this mirrors).</summary>
    public Vector3 Velocity { get; set; }

    /// <summary>Seconds since Position/Velocity were last set from a network update. Reset to 0 by
    /// WorldSimulation.ApplyAvatarUpdate; advanced every frame by ExtrapolateMovement, which phases
    /// the velocity extrapolation out as this grows (see PhaseOutSeconds/MaxExtrapolationSeconds).</summary>
    public float TimeSinceUpdate { get; set; }

    /// <summary>Originating simulator's time dilation (0-1) as of the last network update -- see
    /// AvatarUpdateEvent.TimeDilation's doc comment. Scales ExtrapolateMovement's per-frame
    /// dead-reckoning step so a laggy/busy sim doesn't extrapolate faster than it's actually
    /// simulating. Defaults to 1 (full speed) for a freshly-created entity.</summary>
    public float TimeDilation { get; set; } = 1f;

    public TransformComponent()
    {
        Position = Vector3.Zero;
        Rotation = Quaternion.Identity;
        LocalRotation = Quaternion.Identity;
    }

    public TransformComponent(Vector3 position, Quaternion rotation)
    {
        Position = position;
        Rotation = rotation;
        LocalPosition = position;
        LocalRotation = rotation;
    }
}
