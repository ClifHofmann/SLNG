using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-ANIM-07. The priority an animation's key on one joint competes at: the joint's own
/// when the animation gives one, else the animation's (LLJoint::USE_MOTION_PRIORITY = -1,
/// llpose.cpp:491), with the locomotion boost above both.</summary>
public class JointPriorityTests
{
    [Theory]
    [InlineData(true, 6, 2, 3, 6)]    // the locomotion boost beats everything
    [InlineData(true, 6, -1, 3, 6)]
    [InlineData(false, 6, 2, 3, 2)]   // the joint's own priority
    [InlineData(false, 6, 0, 3, 0)]   // 0 is a real joint priority, not "unset"
    [InlineData(false, 6, -1, 3, 3)]  // unset (negative) falls back to the animation's
    public void Effective_FollowsBoostThenJointThenAnimation(
        bool boosted, int boostPriority, int jointPriority, int animationPriority, int expected)
    {
        Assert.Equal(expected, JointPriority.Effective(boosted, boostPriority, jointPriority, animationPriority));
    }

    [Fact]
    public void AJointPriority_DecidesWhoWinsInATable()
    {
        // A priority-2 pose that sets one joint at priority 4 beats a priority-3 animation there,
        // and loses every joint it did not raise.
        var table = new JointBlendTable<float>();
        const int Hip = 1, Knee = 2;
        const float Walk = 1f, Pose = 2f;
        static float Pick(float t, float below, float above) => t >= 0.5f ? above : below;

        table.Offer(Hip, JointPriority.Effective(false, 6, 4, 2), 1f, Pose);
        table.Offer(Knee, JointPriority.Effective(false, 6, -1, 2), 1f, Pose);
        table.Offer(Hip, JointPriority.Effective(false, 6, -1, 3), 1f, Walk);
        table.Offer(Knee, JointPriority.Effective(false, 6, -1, 3), 1f, Walk);

        Assert.True(table.TryResolve(Hip, Pick, out float hip));
        Assert.True(table.TryResolve(Knee, Pick, out float knee));
        Assert.Equal(Pose, hip);
        Assert.Equal(Walk, knee);
    }
}
