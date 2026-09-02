using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// BUG-AVATAR-02 — every avatar bake channel returned HTTP 403 through the generic asset CDN;
/// Firestorm fetches the same texture ids from a completely different host and path,
/// <c>bake-texture.glb.{grid}.lindenlab.com/texture/{agent}/{slot}/{textureId}</c>. These pin the
/// slot-name table that URL depends on, read directly from the reference viewer's own bake-channel
/// definitions (<c>llavatarappearancedefines.cpp</c>) rather than inferred from the one name
/// ("eyes") actually observed in a live capture.
/// </summary>
public class BakeChannelNamesTests
{
    [Theory]
    [InlineData(8, "head")]
    [InlineData(9, "upper")]
    [InlineData(10, "lower")]
    [InlineData(11, "eyes")] // the one channel confirmed via a live Firestorm HTTP capture
    [InlineData(19, "skirt")]
    [InlineData(20, "hair")]
    [InlineData(40, "leftarm")]
    [InlineData(41, "leftleg")]
    [InlineData(42, "aux1")]
    [InlineData(43, "aux2")]
    [InlineData(44, "aux3")]
    public void Maps_every_known_bake_channel(int channel, string expectedName)
    {
        Assert.Equal(expectedName, BakeChannelNames.NameFor(channel));
    }

    [Theory]
    [InlineData(0)]   // an ordinary (non-baked) face texture index
    [InlineData(21)]  // LowerAlpha -- a real AvatarTextureIndex, but not a bake channel
    [InlineData(-1)]
    [InlineData(255)] // AvatarTextureIndex.Unknown
    public void Returns_null_for_anything_that_is_not_a_bake_channel(int channel)
    {
        Assert.Null(BakeChannelNames.NameFor(channel));
    }
}
