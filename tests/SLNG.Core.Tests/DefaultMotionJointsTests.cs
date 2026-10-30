using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-ANIM-09. The joints the viewer's own default motions keep driving. SLNG has none of those
/// motions, so a joint on this list must not be held after an animation stops driving it -- the viewer
/// would put it back at once; SLNG would leave it stuck.</summary>
public class DefaultMotionJointsTests
{
    [Theory]
    [InlineData("mPelvis")]
    [InlineData("mTorso")]
    [InlineData("mChest")]
    [InlineData("mNeck")]
    [InlineData("mHead")]
    [InlineData("mEyeLeft")]
    [InlineData("mEyeRight")]
    [InlineData("mFaceEyeAltLeft")]
    [InlineData("mFaceEyeAltRight")]
    public void TheDefaultMotionsDriveThese(string joint)
        => Assert.True(DefaultMotionJoints.Drives(joint));

    [Theory]
    [InlineData("LEFT_PEC")]       // the eBody Waifu deformer's joints: collision volumes
    [InlineData("RIGHT_PEC")]
    [InlineData("mFaceLipLower")]  // Bento face, tail, wings, hind limbs: no default motion
    [InlineData("mTail1")]
    [InlineData("mWing1Left")]
    [InlineData("mHindLimb1Left")]
    [InlineData("mElbowLeft")]
    [InlineData("mHipLeft")]
    public void NothingElseIsDriven(string joint)
        => Assert.False(DefaultMotionJoints.Drives(joint));
}
