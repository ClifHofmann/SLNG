using Xunit;
using SLNG.Core;
using System.IO;
using System.Linq;

namespace SLNG.Core.Tests;

public class AvatarSkeletonTests
{
    private AvatarSkeleton LoadTestSkeleton()
    {
        // The skeleton XML is in the app assets. For testing, we resolve relative to the test project.
        string path = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "..", "app", "assets", "avatar", "avatar_skeleton.xml");
        path = Path.GetFullPath(path);

        Assert.True(File.Exists(path), $"avatar_skeleton.xml not found at {path}");
        return AvatarSkeleton.LoadFromFile(path);
    }

    [Fact]
    public void LoadFromFile_ParsesExpectedBoneCount()
    {
        var skeleton = LoadTestSkeleton();

        // The XML declares 133 bones + 26 collision volumes = 159 total entries
        int realBones = skeleton.RealBones.Count();
        int collisionVolumes = skeleton.Bones.Count(b => b.IsCollisionVolume);

        Assert.Equal(133, realBones);
        Assert.Equal(26, collisionVolumes);
    }

    [Fact]
    public void LoadFromFile_RootBoneIsMPelvis()
    {
        var skeleton = LoadTestSkeleton();

        var root = skeleton.RealBones.First();
        Assert.Equal("mPelvis", root.Name);
        Assert.Null(root.ParentName);
    }

    [Fact]
    public void LoadFromFile_HeadParentIsNeck()
    {
        var skeleton = LoadTestSkeleton();

        var head = skeleton.GetBone("mHead");
        Assert.NotNull(head);
        Assert.Equal("mNeck", head!.ParentName);
    }

    [Fact]
    public void LoadFromFile_NeckParentIsChest()
    {
        var skeleton = LoadTestSkeleton();

        var neck = skeleton.GetBone("mNeck");
        Assert.NotNull(neck);
        Assert.Equal("mChest", neck!.ParentName);
    }

    [Fact]
    public void LoadFromFile_HasFingerBones()
    {
        var skeleton = LoadTestSkeleton();

        // Bento added finger bones
        var thumb = skeleton.GetBone("mHandThumb1Left");
        Assert.NotNull(thumb);
        Assert.False(thumb!.IsCollisionVolume);
    }

    [Fact]
    public void LoadFromFile_HasFaceBones()
    {
        var skeleton = LoadTestSkeleton();

        // Bento face bones
        var eyebrow = skeleton.GetBone("mFaceEyebrowOuterLeft");
        Assert.NotNull(eyebrow);
    }

    [Fact]
    public void LoadFromFile_PelvisHasNonZeroPosition()
    {
        var skeleton = LoadTestSkeleton();

        var pelvis = skeleton.GetBone("mPelvis");
        Assert.NotNull(pelvis);
        // mPelvis pos is roughly (0, 0, 1.067) in SL coordinates
        Assert.True(pelvis!.Position.Z > 0.5f, "Pelvis Z should be above ground");
    }
}
