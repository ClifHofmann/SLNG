using System;

namespace SLNG.Core;

/// <summary>
/// The Second Life viewer's terrain detail-texture composition, ported from
/// <c>LLVLComposition::generateHeights</c> (<c>indra/newview/llvlcomposition.cpp</c>).
///
/// The viewer turns elevation into a single continuous value in [0, 3] that indexes the region's
/// four detail textures — detail 0 at the bottom, detail 3 at the top — and blends between the two
/// neighbouring textures. There is no slope/cliff texture anywhere in the viewer: all four slots
/// are chosen by elevation alone.
///
/// The part that dominates how terrain actually looks is <c>twiddle</c>: Perlin noise added to the
/// elevation <em>before</em> the band lookup, which is what turns geometric contour bands into the
/// organic mottling the real viewer shows. It is sampled in global coordinates, so the pattern is
/// continuous across region borders.
///
/// This class is the engine-agnostic reference implementation.
/// <c>app/materials/sl_terrain_composition.gdshaderinc</c> mirrors it per-fragment on the GPU and
/// takes its noise tables from <see cref="SlPerlinNoise"/> via a lookup texture, so there is one
/// source of truth for the tables.
/// </summary>
public static class SlTerrainComposition
{
    /// <summary>Number of detail texture slots (<c>LLTerrainMaterials::ASSET_COUNT</c>).</summary>
    public const int AssetCount = 4;

    // Constants verbatim from generateHeights().
    private const float SlopeSquared = 1.5f * 1.5f;   // 2.25
    private const float XyScale = 4.9215f;
    private const float NoiseMagnitude = 2f;

    /// <summary>
    /// Detail texture UV scale, in metres per texture repeat: the viewer's
    /// <c>RenderTerrainScale</c> (settings.xml default 12.0), used as <c>1/RenderTerrainScale</c>
    /// for the texgen planes in <c>LLDrawPoolTerrain</c>.
    /// </summary>
    public const float DetailScaleMetres = 12f;

    /// <summary>
    /// The noise term added to elevation before the band lookup.
    /// <paramref name="globalX"/>/<paramref name="globalY"/> are SL global metres
    /// (region origin + position within the region).
    ///
    /// Note what is <em>not</em> here: generateHeights fills a third component
    /// <c>vec[2] = height/zScale</c>, but both <c>noise2()</c> and <c>turbulence2()</c> read only
    /// the first two components, so elevation never enters the noise and <c>zScale</c> is dead.
    /// The field is purely horizontal.
    /// </summary>
    public static float Twiddle(float globalX, float globalY)
    {
        float vx = globalX / XyScale;
        float vy = globalY / XyScale;

        // Low-frequency component for large divisions: ~22 m period, the dominant term.
        float twiddle = SlPerlinNoise.Noise2(vx * 0.2222222222f, vy * 0.2222222222f) * 6.5f;

        // High-frequency component: two octaves at ~4.9 m and ~2.5 m.
        twiddle += SlPerlinNoise.Turbulence2(vx, vy, 2f) * SlopeSquared;

        return twiddle * NoiseMagnitude;
    }

    /// <summary>
    /// The composition value in [0, 3]: which detail texture (and how far between it and the next)
    /// belongs at this point.
    /// </summary>
    public static float Value(float globalX, float globalY, float height, float startHeight, float heightRange)
    {
        // Settings not in yet. The viewer draws no terrain at all in this state, so there is no
        // right answer — but it has to be the LOWEST band, not the highest. Clamping a
        // divide-by-almost-zero lands on 3.0 and paints the whole region in the top detail
        // texture, which is quiet enough with a rock texture to go unnoticed for a long time.
        if (MathF.Abs(heightRange) < 0.001f) return 0f;

        float scaled = (height + Twiddle(globalX, globalY) - startHeight) * AssetCount / heightRange;
        return Math.Clamp(scaled, 0f, 3f);
    }

    /// <summary>
    /// Bilinear blend of a per-corner region parameter (start height or height range), matching
    /// the <c>bilinear()</c> helper in llvlcomposition.cpp.
    ///
    /// <paramref name="corners"/> is indexed the way the RegionHandshake fields arrive and the way
    /// <c>LLVLComposition::ECorner</c> numbers them: 0 = SW, 1 = SE, 2 = NW, 3 = NE.
    /// <paramref name="east"/> and <paramref name="north"/> are 0..1 fractions across the region.
    /// </summary>
    public static float BilinearCorners(ReadOnlySpan<float> corners, float east, float north)
    {
        float west = MathLerp(corners[0], corners[1], north);   // SW -> SE
        float eastEdge = MathLerp(corners[2], corners[3], north); // NW -> NE
        return MathLerp(west, eastEdge, east);
    }

    private static float MathLerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// Scale of the Perlin field that drives the blend ramp's V axis, from
    /// <c>LLSurfacePatch::eval</c>: <c>xyScale = 4.9215 * 7</c>, then the same 0.2222 factor the
    /// composition noise uses — a period of roughly 155 m, about one and a half cycles across a
    /// 256 m region.
    /// </summary>
    public const float RampNoiseScale = (1f / (4.9215f * 7f)) * 0.2222222222f;

    /// <summary>
    /// The ramp's V coordinate at a point, in global metres — <c>LLSurfacePatch::eval</c>'s
    /// <c>rand_val</c>. Measured distribution: mean 0.500, sd 0.159, effectively spanning ramp
    /// rows 59..195.
    /// </summary>
    public static float RampV(float globalX, float globalY)
    {
        float n = SlPerlinNoise.Noise2(globalX * RampNoiseScale, globalY * RampNoiseScale);
        return Math.Clamp(n * 0.75f + 0.5f, 0f, 1f);
    }

    /// <summary>
    /// A CPU stand-in for the blend ramp: the mean of the real gradient's curve family, which a
    /// smoothstep fits to within 3.7/255 (a linear ramp would be off by 32.6/255).
    ///
    /// <b>This is not what renders.</b> The terrain shader samples the viewer's actual
    /// <c>alpha_gradient_2d.j2c</c> (shipped as <c>app/textures/sl_alpha_gradient_2d.png</c>),
    /// including its V axis, because the V drift is what makes texture boundaries crisp and no
    /// fitted curve reproduces it — see the spec's record of that failed attempt.
    ///
    /// This exists only for the CPU-side composition diagnostic, which reports per-slot AREA
    /// splits. Averaged over an area the mean curve is within 0.088 slot units of the real ramp,
    /// so it is accurate enough for that and nothing else. Do not use it to reason about contrast:
    /// by construction it has none.
    /// </summary>
    public static float Ramp(float u)
    {
        const float Edge0 = 0.0118f;
        const float Edge1 = 1.0627f;
        float t = Math.Clamp((u - Edge0) / (Edge1 - Edge0), 0f, 1f);
        return 1f - t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Per-texture weights for a composition value, matching the three-tap ramp lookup in
    /// <c>terrainF.glsl</c>:
    /// <c>mix( mix(detail3, detail2, ramp(v-2)), mix(detail1, detail0, ramp(v)), ramp(v-1) )</c>.
    /// Because the ramp saturates within one unit this reduces to a crossfade between the two
    /// neighbouring textures — but with the eased profile above, not a linear one.
    /// </summary>
    public static void Weights(float value, Span<float> weights)
    {
        if (weights.Length != AssetCount) throw new ArgumentException("expected 4 weights", nameof(weights));

        float alpha1 = Ramp(value);
        float alphaFinal = Ramp(value - 1f);
        float alpha2 = Ramp(value - 2f);

        // mix(detail3, detail2, alpha2) -> low pair; mix(detail1, detail0, alpha1) -> high pair.
        float w3 = 1f - alpha2;
        float w2 = alpha2;
        float w1 = 1f - alpha1;
        float w0 = alpha1;

        weights[0] = w0 * alphaFinal;
        weights[1] = w1 * alphaFinal;
        weights[2] = w2 * (1f - alphaFinal);
        weights[3] = w3 * (1f - alphaFinal);
    }
}
