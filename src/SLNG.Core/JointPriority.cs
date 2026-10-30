namespace SLNG.Core;

/// <summary>
/// BUG-ANIM-07: the priority one animation's key on one joint competes at in a
/// <see cref="JointBlendTable{T}"/>.
/// </summary>
public static class JointPriority
{
    /// <summary>The locomotion boost wins over everything while it lasts; otherwise the joint's own
    /// priority when the animation gives one (<c>&gt;= 0</c> -- 0 is a real priority), else the
    /// animation's. The viewer's <c>LLJoint::USE_MOTION_PRIORITY</c> (-1) is the "unset" case
    /// (llpose.cpp:490-496).</summary>
    public static int Effective(bool boosted, int boostPriority, int jointPriority, int animationPriority)
        => boosted ? boostPriority : (jointPriority >= 0 ? jointPriority : animationPriority);
}
