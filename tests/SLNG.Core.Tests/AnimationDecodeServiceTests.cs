using SLNG.Assets;
using Xunit;

namespace SLNG.Core.Tests;

public class AnimationDecodeServiceTests
{
    [Fact]
    public void Decode_null_returns_null()
    {
        Assert.Null(AnimationDecodeService.Decode(null!));
    }

    [Fact]
    public void Decode_too_short_returns_null()
    {
        // Minimum valid header is much larger than 3 bytes.
        Assert.Null(AnimationDecodeService.Decode(new byte[3]));
    }

    [Fact]
    public void Decode_zero_length_returns_null()
    {
        Assert.Null(AnimationDecodeService.Decode(System.Array.Empty<byte>()));
    }

    [Fact]
    public void Decode_invalid_payload_returns_null_not_exception()
    {
        // 64 random-looking bytes are not a valid SL binary BVH — must return null, not throw.
        var garbage = new byte[64];
        for (int i = 0; i < garbage.Length; i++) garbage[i] = (byte)(i * 7 + 3);
        Assert.Null(AnimationDecodeService.Decode(garbage));
    }
}
