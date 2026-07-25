using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

public class J2kByteSizeEstimatorTests
{
    [Fact]
    public void LowerDiscardLevel_RequestsMoreBytes()
    {
        // discard 0 (full res) must always need at least as many bytes as any coarser level.
        int prev = 0;
        for (int discard = J2kByteSizeEstimator.MaxDiscardLevel; discard >= 0; discard--)
        {
            int size = J2kByteSizeEstimator.CalcDataSizeJ2C(512, 512, discard);
            Assert.True(size >= prev, $"discard {discard} ({size} bytes) should be >= discard {discard + 1} ({prev} bytes)");
            prev = size;
        }
    }

    [Fact]
    public void FullResolution_IsLargerThanCoarsestByMoreThanAnOrderOfMagnitude()
    {
        int finest = J2kByteSizeEstimator.CalcDataSizeJ2C(1024, 1024, 0);
        int coarsest = J2kByteSizeEstimator.CalcDataSizeJ2C(1024, 1024, J2kByteSizeEstimator.MaxDiscardLevel);
        Assert.True(finest > coarsest * 10, $"expected finest ({finest}) to dwarf coarsest ({coarsest})");
    }

    [Fact]
    public void UnknownDimensions_FallBackToConservativeEstimate()
    {
        // 0x0 (unknown) must assume a worst-case dimension, never less than a known small texture.
        int unknown = J2kByteSizeEstimator.CalcDataSizeJ2C(0, 0, 2);
        int knownSmall = J2kByteSizeEstimator.CalcDataSizeJ2C(64, 64, 2);
        Assert.True(unknown >= knownSmall);
    }

    [Fact]
    public void AlwaysReturnsAtLeastTheHeaderEstimate()
    {
        int size = J2kByteSizeEstimator.CalcDataSizeJ2C(32, 32, J2kByteSizeEstimator.MaxDiscardLevel);
        Assert.True(size > 0);
    }
}
