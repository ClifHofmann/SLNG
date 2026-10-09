using System;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class JellyDollColorTests
{
    private static Guid WithFirstByte(byte first)
    {
        var bytes = new byte[16];
        bytes[0] = first;
        return new Guid(bytes, bigEndian: true);
    }

    [Fact]
    public void FirstByte_IsTheFirstTwoHexDigitsOfTheTextualUuid()
    {
        // LLUUID::mData[0] is the byte written first: "a1b2c3d4-..." -> 0xA1.
        var id = Guid.Parse("a1b2c3d4-0000-0000-0000-000000000000");
        Assert.Equal(0xA1, JellyDollColor.FirstByte(id));
    }

    [Fact]
    public void ForFirstByte_ZeroIsRedToneDownedBy028()
    {
        var (r, g, b) = JellyDollColor.ForFirstByte(0);
        Assert.Equal(JellyDollColor.Brightness, r, 5);
        Assert.Equal(0f, g, 5);
        Assert.Equal(0f, b, 5);
    }

    [Fact]
    public void ForFirstByte_128LandsExactlyOnTheCyanStop()
    {
        // 128 / 256 * 6 = 3.0 -> the fourth stop of red, magenta, blue, cyan, green, yellow, red.
        var (r, g, b) = JellyDollColor.ForFirstByte(128);
        float edge = JellyDollColor.Brightness / MathF.Sqrt(2f);
        Assert.Equal(0f, r, 5);
        Assert.Equal(edge, g, 5);
        Assert.Equal(edge, b, 5);
    }

    [Fact]
    public void ForFirstByte_HalfwayBetweenStopsIsInterpolatedBeforeNormalising()
    {
        // 64 / 256 * 6 = 1.5 -> halfway between magenta (1,0,1) and blue (0,0,1) = (0.5, 0, 1).
        var (r, g, b) = JellyDollColor.ForFirstByte(64);
        float length = MathF.Sqrt(0.25f + 1f);
        Assert.Equal(0.5f / length * JellyDollColor.Brightness, r, 5);
        Assert.Equal(0f, g, 5);
        Assert.Equal(1f / length * JellyDollColor.Brightness, b, 5);
    }

    [Fact]
    public void ForFirstByte_EveryByteHasTheSameMagnitudeAndNeverReadsPastTheRing()
    {
        for (int i = 0; i <= 255; i++)
        {
            var (r, g, b) = JellyDollColor.ForFirstByte((byte)i);
            float magnitude = MathF.Sqrt(r * r + g * g + b * b);
            Assert.Equal(JellyDollColor.Brightness, magnitude, 4);
            Assert.True(r >= 0f && g >= 0f && b >= 0f);
        }
    }

    [Fact]
    public void ForAgent_DependsOnlyOnTheFirstByte()
    {
        var one = JellyDollColor.ForAgent(WithFirstByte(0x40));
        var other = JellyDollColor.ForAgent(Guid.Parse("40ffffff-ffff-ffff-ffff-ffffffffffff"));
        Assert.Equal(one, other);
        Assert.NotEqual(one, JellyDollColor.ForAgent(WithFirstByte(0xC0)));
    }
}
