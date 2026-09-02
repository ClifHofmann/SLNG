using System;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-AVATAR-02 follow-up — <see cref="GridSession.BuildBakeTextureUrl"/> must put the id of the
/// avatar WEARING the bake into the path, mirroring <c>LLVOAvatar::getImageURL</c>
/// (<c>url = appearance_service_url + "texture/" + getID().asString() + "/" + mDefaultImageName +
/// "/" + uuid.asString()</c> — <c>getID()</c> is the displayed avatar, not the viewer). Sending
/// our own id for someone else's bake 403s the CDN and leaves every other mesh-body avatar
/// untextured (found live on Agni 2026-09-02: only the local avatar's own bakes resolved).
/// </summary>
public class BakeTextureUrlTests
{
    [Fact]
    public void Puts_the_wearing_avatars_id_in_the_path_not_the_viewers()
    {
        var wearer = Guid.Parse("8a2f74fd-aaea-e792-a86b-22b6304e3669");
        var texture = Guid.Parse("2cae1bdb-db93-bdfa-ba05-201b99bd383b");

        var url = GridSession.BuildBakeTextureUrl("agni", wearer, "upper", texture);

        Assert.Equal(
            $"http://bake-texture.glb.agni.lindenlab.com/texture/{wearer}/upper/{texture}",
            url.ToString());
    }

    [Theory]
    [InlineData("agni")]
    [InlineData("aditi")]
    public void Bakes_the_grid_short_name_into_the_host(string grid)
    {
        var url = GridSession.BuildBakeTextureUrl(grid, Guid.NewGuid(), "head", Guid.NewGuid());

        Assert.Equal($"bake-texture.glb.{grid}.lindenlab.com", url.Host);
    }

    [Fact]
    public void Two_avatars_sharing_a_bake_slot_get_distinct_urls()
    {
        var texture = Guid.NewGuid();
        var a = GridSession.BuildBakeTextureUrl("agni", Guid.NewGuid(), "lower", texture);
        var b = GridSession.BuildBakeTextureUrl("agni", Guid.NewGuid(), "lower", texture);

        Assert.NotEqual(a.ToString(), b.ToString());
    }
}
