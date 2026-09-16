namespace SLNG.Core.Avatars;

/// <summary>
/// FEAT-ANIM-07: Sustained local hold modes that freeze the avatar in a specific neutral pose
/// without advancing animation clips.
/// </summary>
public enum AvatarHoldMode
{
    /// <summary>Normal animation playback from active network clips and predicted locomotion.</summary>
    None = 0,

    /// <summary>Raw skeleton rest pose (T-Pose) for mesh-fit and rigging inspection. Position is not locked.</summary>
    BindPose = 1,

    /// <summary>Relaxed natural standing pose (held built-in STAND clip). Locks world position and facing for photography/posing.</summary>
    PoseStand = 2,
}
