using System;

namespace SLNG.Core;

/// <summary>
/// BUG-ANIM-09: the joints the reference viewer's own default motions keep driving, whatever else plays.
/// </summary>
/// <remarks>
/// <para>An avatar starts <c>LLHeadRotMotion</c>, <c>LLEyeMotion</c>, <c>LLBodyNoiseMotion</c>,
/// <c>LLBreatheMotionRot</c>, <c>LLPhysicsMotionController</c>, <c>LLHandMotion</c> and
/// <c>LLPelvisFixMotion</c> (<c>LLVOAvatar::startDefaultMotions</c>, llvoavatar.cpp:2107-2118). Their joint
/// states: head rotation mTorso / mNeck / mHead (llheadrotmotion.cpp:129-156), the eyes and the alternate
/// eyes (:318-356), body noise mTorso (llvoavatar.cpp:327), breathing mChest (:438), pelvis fix mPelvis
/// (:550), the physics motions mChest and mPelvis (llphysicsmotion.cpp:279-393). The hand motion only
/// sets visual params (llhandmotion.cpp:103-114), so no joint.</para>
///
/// <para>Because those motions drive these joints all the time, a joint an animation stops keying is
/// taken back by them at once. SLNG does not have the motions, so these joints are NOT held when an
/// animation stops driving them (<see cref="JointHold{T}"/>): holding them would leave eyes, head or
/// body stuck where the last animation put them. Every other joint -- collision volumes, the Bento
/// face, tail, wings and limbs, the ordinary limb joints -- is held, as in the viewer.</para>
/// </remarks>
public static class DefaultMotionJoints
{
    /// <summary>True when a default motion of the viewer drives <paramref name="joint"/>.</summary>
    public static bool Drives(string joint) => joint switch
    {
        "mPelvis" or "mTorso" or "mChest" or "mNeck" or "mHead" => true,
        "mEyeLeft" or "mEyeRight" or "mFaceEyeAltLeft" or "mFaceEyeAltRight" => true,
        _ => false,
    };
}
