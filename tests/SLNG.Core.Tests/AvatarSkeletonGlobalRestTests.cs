using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class AvatarSkeletonGlobalRestTests
{
    private const string Xml = @"<linden_skeleton version=""2.0"">
  <bone name=""mPelvis"" pos=""0 0 1.067"" rot=""0 0 0"" scale=""1 1 1"" end=""0 0 0"">
    <bone name=""mTorso"" pos=""0 0 0.2"" rot=""0 0 0"" scale=""1 1 1"" end=""0 0 0"">
      <bone name=""mChest"" pos=""0 0 0.2"" rot=""0 0 0"" scale=""1 1 1"" end=""0 0 0"" />
    </bone>
  </bone>
</linden_skeleton>";

    [Fact]
    public void GlobalRestPosition_accumulates_up_the_chain()
    {
        var skeleton = AvatarSkeleton.LoadFromXml(Xml);

        Assert.Equal(1.067, skeleton.GetGlobalRestPosition("mPelvis").Z, 3);
        Assert.Equal(1.267, skeleton.GetGlobalRestPosition("mTorso").Z, 3);
        Assert.Equal(1.467, skeleton.GetGlobalRestPosition("mChest").Z, 3); // 1.067 + 0.2 + 0.2
    }

    [Fact]
    public void GlobalRestPosition_unknown_bone_is_zero()
    {
        var skeleton = AvatarSkeleton.LoadFromXml(Xml);
        Assert.Equal(System.Numerics.Vector3.Zero, skeleton.GetGlobalRestPosition("nope"));
    }
}
