using System;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// Pins the port of the viewer's terrain detail-texture composition. These are structural and
/// known-answer checks against the C source in scratch/slviewer rather than against a screenshot:
/// the failure mode this port has is "plausible noise that is not the viewer's noise", which looks
/// fine in isolation and only shows up in an A/B.
/// </summary>
public class SlTerrainCompositionTests
{
    // The Microsoft CRT rand() from a known seed. If this drifts, every gradient and permutation
    // entry below drifts with it and the terrain pattern silently stops matching Firestorm.
    [Fact]
    public void MsvcRand_MatchesKnownSequence()
    {
        var rng = new SlPerlinNoise.MsvcRand(1);
        int[] expected = { 41, 18467, 6334, 26500, 19169, 15724, 11478, 29358 };
        foreach (int e in expected)
        {
            Assert.Equal(e, rng.Next());
        }
    }

    [Fact]
    public void Permutation_IsAPermutationOf0To255()
    {
        var seen = new bool[SlPerlinNoise.B];
        foreach (int v in SlPerlinNoise.Permutation)
        {
            Assert.InRange(v, 0, SlPerlinNoise.B - 1);
            Assert.False(seen[v], $"permutation entry {v} appears twice");
            seen[v] = true;
        }
    }

    [Fact]
    public void Permutation_IsShuffled_NotIdentity()
    {
        // init() fills p[i] = i and then shuffles. A shuffle that silently no-ops (e.g. an
        // off-by-one in the `while (--i)` loop) would leave an identity table, which still passes
        // the permutation test above but produces axis-aligned noise.
        int fixedPoints = 0;
        var p = SlPerlinNoise.Permutation;
        for (int i = 0; i < p.Length; i++)
        {
            if (p[i] == i) fixedPoints++;
        }
        Assert.True(fixedPoints < 20, $"table looks unshuffled: {fixedPoints} fixed points");
    }

    [Fact]
    public void Gradients_AreUnitLength()
    {
        var g = SlPerlinNoise.Gradients2D;
        for (int i = 0; i < SlPerlinNoise.B; i++)
        {
            float x = g[i * 2], y = g[i * 2 + 1];
            Assert.Equal(1.0, Math.Sqrt(x * x + y * y), 4);
        }
    }

    [Fact]
    public void Tables_AreDeterministic()
    {
        var p = new int[SlPerlinNoise.B];
        var g = new float[SlPerlinNoise.B * 2];
        SlPerlinNoise.Init(p, g);

        Assert.Equal(SlPerlinNoise.Permutation.ToArray(), p);
        Assert.Equal(SlPerlinNoise.Gradients2D.ToArray(), g);
    }

    // Classic Perlin is zero at every lattice point, because every gradient is dotted with a
    // zero offset vector there. Simplex noise is not, so this also catches substituting the
    // wrong noise variant -- the specific trap scratch/noise2D.glsl sets.
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(17f, 43f)]
    [InlineData(255f, 3f)]
    public void Noise2_IsZeroAtLatticePoints(float x, float y)
    {
        Assert.Equal(0f, SlPerlinNoise.Noise2(x, y), 5);
    }

    [Fact]
    public void Noise2_IsWithinGradientNoiseBounds()
    {
        // 2D gradient noise with unit gradients is bounded by sqrt(2)/2.
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < 4000; i++)
        {
            float v = SlPerlinNoise.Noise2(i * 0.37f, i * 0.11f);
            min = MathF.Min(min, v);
            max = MathF.Max(max, v);
        }
        Assert.InRange(min, -0.75f, -0.2f);
        Assert.InRange(max, 0.2f, 0.75f);
    }

    [Fact]
    public void Noise2_Is256Periodic()
    {
        // The shader relies on this to reduce SL's global coordinates into a range where float32
        // still has the precision the noise needs. If it were false, the terrain pattern in the
        // client would not be the pattern this reference implementation describes.
        for (int i = 0; i < 20; i++)
        {
            float x = i * 3.7f, y = i * 1.9f;
            Assert.Equal(SlPerlinNoise.Noise2(x, y), SlPerlinNoise.Noise2(x + 256f, y + 256f), 4);
        }
    }

    [Fact]
    public void Turbulence2_WithFreq2_IsTwoOctaves()
    {
        // turbulence2(v, 2) runs freq = 2 then freq = 1, i.e. noise2(2v)/2 + noise2(v).
        float x = 12.3f, y = -4.5f;
        float expected = SlPerlinNoise.Noise2(2f * x, 2f * y) / 2f + SlPerlinNoise.Noise2(x, y);
        Assert.Equal(expected, SlPerlinNoise.Turbulence2(x, y, 2f), 5);
    }

    [Fact]
    public void Twiddle_DoesNotDependOnElevation()
    {
        // generateHeights fills vec[2] = height/zScale, but noise2/turbulence2 read only the first
        // two components. The noise field is purely horizontal; if a future change starts feeding
        // height into it, that is a regression against the viewer, not an improvement.
        var method = typeof(SlTerrainComposition).GetMethod(nameof(SlTerrainComposition.Twiddle));
        Assert.NotNull(method);
        Assert.Equal(2, method!.GetParameters().Length);
    }

    [Fact]
    public void Twiddle_SwingsHeightByMetres()
    {
        // The whole point of the noise: it has to be large relative to a region's height_range,
        // or transitions stay geometric contour bands. A twiddle that only moved the height by
        // centimetres would be indistinguishable from having no noise at all.
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < 2000; i++)
        {
            float t = SlTerrainComposition.Twiddle(i * 1.7f, i * 0.9f);
            min = MathF.Min(min, t);
            max = MathF.Max(max, t);
        }
        Assert.True(max - min > 8f, $"twiddle range {max - min:F2} m is too small to be visible");
        Assert.True(max - min < 40f, $"twiddle range {max - min:F2} m is implausibly large");
    }

    [Fact]
    public void Value_IsClampedToAssetRange()
    {
        for (int i = 0; i < 200; i++)
        {
            float v = SlTerrainComposition.Value(i * 5f, i * 3f, i - 100f, 20f, 60f);
            Assert.InRange(v, 0f, 3f);
        }
    }

    [Fact]
    public void Value_RisesWithElevation()
    {
        // Averaged over the noise, higher ground must select a higher detail slot.
        float lowSum = 0f, highSum = 0f;
        for (int i = 0; i < 500; i++)
        {
            lowSum += SlTerrainComposition.Value(i * 2.3f, i * 1.1f, 22f, 20f, 60f);
            highSum += SlTerrainComposition.Value(i * 2.3f, i * 1.1f, 70f, 20f, 60f);
        }
        Assert.True(highSum > lowSum);
    }

    [Fact]
    public void Ramp_RunsFromOneToZero()
    {
        // alpha_gradient.tga is 255 at u=0 and 0 at u=1. That polarity is what makes detail 0 the
        // LOW texture and detail 3 the HIGH one; inverting it swaps the whole terrain.
        Assert.Equal(1f, SlTerrainComposition.Ramp(-0.2f), 2);
        Assert.Equal(0f, SlTerrainComposition.Ramp(1.3f), 2);
        Assert.True(SlTerrainComposition.Ramp(0.2f) > SlTerrainComposition.Ramp(0.8f));
    }

    [Fact]
    public void Weights_SumToOne()
    {
        var w = new float[4];
        for (float v = 0f; v <= 3f; v += 0.05f)
        {
            SlTerrainComposition.Weights(v, w);
            Assert.Equal(1f, w[0] + w[1] + w[2] + w[3], 3);
            foreach (float x in w) Assert.InRange(x, 0f, 1f);
        }
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 1)]
    [InlineData(2f, 2)]
    [InlineData(3f, 3)]
    public void Weights_SelectTheExpectedDetailSlot(float value, int expectedSlot)
    {
        var w = new float[4];
        SlTerrainComposition.Weights(value, w);

        int best = 0;
        for (int i = 1; i < 4; i++)
        {
            if (w[i] > w[best]) best = i;
        }
        Assert.Equal(expectedSlot, best);
    }

    [Fact]
    public void Weights_AreEffectivelyACrossfadeBetweenNeighbours()
    {
        // The ramp all but saturates within one composition unit, so the two neighbouring
        // textures carry essentially all the weight. It does not saturate *exactly*: the shipped
        // gradient still reads about 2/255 at u = 1, so a third slot bleeds in at a fraction of a
        // percent near the band edges. That tail is the viewer's behaviour, not an error -- what
        // would be an error is a third slot contributing visibly, which is what a wrong ramp
        // offset produces.
        var w = new float[4];
        for (float v = 0f; v <= 3f; v += 0.01f)
        {
            SlTerrainComposition.Weights(v, w);

            var sorted = (float[])w.Clone();
            Array.Sort(sorted);
            float topTwo = sorted[3] + sorted[2];
            Assert.True(topTwo > 0.99f, $"value {v:F2}: two neighbours carry only {topTwo:P1}");
        }
    }

    [Fact]
    public void BilinearCorners_ReturnsEachCornerAtItsCorner()
    {
        // Corner order is the RegionHandshake / LLVLComposition::ECorner order: SW, SE, NW, NE.
        float[] c = { 10f, 20f, 30f, 40f };
        Assert.Equal(10f, SlTerrainComposition.BilinearCorners(c, 0f, 0f), 4); // SW
        Assert.Equal(20f, SlTerrainComposition.BilinearCorners(c, 0f, 1f), 4); // SE
        Assert.Equal(30f, SlTerrainComposition.BilinearCorners(c, 1f, 0f), 4); // NW
        Assert.Equal(40f, SlTerrainComposition.BilinearCorners(c, 1f, 1f), 4); // NE
        Assert.Equal(25f, SlTerrainComposition.BilinearCorners(c, 0.5f, 0.5f), 4);
    }
}
