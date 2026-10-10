using SLNG.Assets;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// BUG-PERF-13: Pure arithmetic tests for avatar texture distance LOD and VRAM budget.
/// </summary>
// FEAT-PERF-25: TextureLod.GlobalLodBias is static; every class that reads or sets it shares one
// collection, so xUnit never runs them in parallel.
[Collection("TextureLod.GlobalLodBias")]
public class AvatarTextureLodTests
{
    private const float AvatarRadius = 0.95f; // half of 1.9m humanoid height
    private const float Fov = 70f;
    private const float ViewportHeight = 1080f;

    [Fact]
    public void ScreenPixelAreaForSphere_ReturnsZero_ForInvalidInputs()
    {
        Assert.Equal(0f, TextureLod.ScreenPixelAreaForSphere(0f, AvatarRadius, Fov, ViewportHeight));
        Assert.Equal(0f, TextureLod.ScreenPixelAreaForSphere(-5f, AvatarRadius, Fov, ViewportHeight));
        Assert.Equal(0f, TextureLod.ScreenPixelAreaForSphere(10f, 0f, Fov, ViewportHeight));
        Assert.Equal(0f, TextureLod.ScreenPixelAreaForSphere(10f, AvatarRadius, Fov, 0f));
    }

    [Fact]
    public void ScreenPixelAreaForSphere_DecreasesMonotonicallyWithDistance()
    {
        float previousArea = float.MaxValue;
        float[] distances = { 2f, 5f, 10f, 15f, 25f, 50f, 100f };

        foreach (float dist in distances)
        {
            float area = TextureLod.ScreenPixelAreaForSphere(dist, AvatarRadius, Fov, ViewportHeight);
            Assert.True(area > 0f, $"Area at {dist}m should be positive");
            Assert.True(area < previousArea, $"Area at {dist}m ({area}) should be less than at previous distance ({previousArea})");
            previousArea = area;
        }
    }

    [Fact]
    public void DiscardLevel_ForOwnAvatar_IsAlwaysZero()
    {
        // Own avatar passes screenPixelArea: 0f
        Assert.Equal(0, TextureLod.DiscardLevelFor(1024, 1024, 0f));
        Assert.Equal(0, TextureLod.DiscardLevelFor(2048, 2048, 0f));
        Assert.Equal(0, TextureLod.DiscardLevelFor(512, 512, 0f));
    }

    [Fact]
    public void DiscardLevel_NearAvatarsStaySharp()
    {
        // Up close (< 3m), an avatar covers substantial screen area -> 1024x1024 stays at discard 0
        float areaAt2m = TextureLod.ScreenPixelAreaForSphere(2.5f, AvatarRadius, Fov, ViewportHeight);
        Assert.True(areaAt2m >= 1024 * 1024 / 4f, "Avatar at 2.5m should cover enough pixels for discard 0");
        Assert.Equal(0, TextureLod.DiscardLevelFor(1024, 1024, areaAt2m));
    }

    [Fact]
    public void DiscardLevel_MonotonicallyIncreasesWithDistance()
    {
        float[] distances = { 2f, 4f, 8f, 16f, 32f, 64f, 128f };
        int previousDiscard = 0;

        foreach (float dist in distances)
        {
            float area = TextureLod.ScreenPixelAreaForSphere(dist, AvatarRadius, Fov, ViewportHeight);
            int discard = TextureLod.DiscardLevelFor(1024, 1024, area);

            Assert.InRange(discard, previousDiscard, TextureLod.MaxDiscardLevel);
            previousDiscard = discard;
        }

        // At 128m, distant avatar texture should reach max discard
        Assert.Equal(TextureLod.MaxDiscardLevel, previousDiscard);
    }

    [Fact]
    public void AvatarCrowdVramBudget_ReducesDramaticallyWithDistanceLod()
    {
        // 15 avatars, 10 textures of 1024x1024 each
        // 1 self avatar + 14 other avatars at distances 3m to 60m
        float[] distances = {
            2.5f,  // near (protected)
            4.0f,  // near (protected)
            5.0f,  // near (protected)
            10.0f, 12.0f, 15.0f, 18.0f, 22.0f, 28.0f, 35.0f, 40.0f, 48.0f, 55.0f, 60.0f
        };

        long unconstrainedBytes = 0;
        long distanceLodBytes = 0;

        // 1 self avatar at full resolution
        for (int t = 0; t < 10; t++)
        {
            long fullTex = TextureAdmission.TextureBytes(1024, 1024, mipmaps: true);
            unconstrainedBytes += fullTex;
            distanceLodBytes += fullTex;
        }

        // 14 remote avatars
        foreach (float dist in distances)
        {
            float area = TextureLod.ScreenPixelAreaForSphere(dist, AvatarRadius, Fov, ViewportHeight);
            int discard = TextureLod.DiscardLevelFor(1024, 1024, area);
            int reducedW = TextureLod.DimensionAfterDiscard(1024, discard);
            int reducedH = TextureLod.DimensionAfterDiscard(1024, discard);

            for (int t = 0; t < 10; t++)
            {
                unconstrainedBytes += TextureAdmission.TextureBytes(1024, 1024, mipmaps: true);
                distanceLodBytes += TextureAdmission.TextureBytes(reducedW, reducedH, mipmaps: true);
            }
        }

        // Full resolution: 15 avatars * 10 * ~5.59 MB = ~83.8 MB
        // With distance LOD: distant avatars take 4x, 16x, 64x less VRAM
        double reductionFactor = (double)unconstrainedBytes / distanceLodBytes;
        Assert.True(reductionFactor >= 3.0, $"Expected >= 3x VRAM reduction, got {reductionFactor:F2}x (from {unconstrainedBytes / (1024 * 1024)}MB to {distanceLodBytes / (1024 * 1024)}MB)");
    }
}
