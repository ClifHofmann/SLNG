namespace SLNG.Core;

/// <summary>
/// FEAT-PERF-14: how far away an object of a given size stops being worth drawing, in the reference
/// viewer's screen-size terms. Pure arithmetic over a radius, the viewport and the Object Detail
/// factor; the renderer turns the answer into <c>GeometryInstance3D.VisibilityRangeEnd</c>, so the
/// engine culls in C++ and none of our C# runs per frame.
///
/// <para><b>What the reference viewer actually has.</b> It has no per-object "too small to draw"
/// cull. Three pieces of its source are the anchors, and the rule below is built from them rather
/// than invented:</para>
/// <list type="bullet">
/// <item><c>LLFace::calcPixelArea</c> (<c>llface.cpp:2382-2398</c>): an object's screen size is
///   <c>atan(halfExtent / distance) * sCurPixelAngle</c> pixels of radius, with
///   <c>sCurPixelAngle = windowHeight / verticalFov</c>. The same measure <c>ObjectRenderer</c>
///   already feeds the texture discard decision, so a "pixel" here means what it means there.</item>
/// <item><c>LLViewerOctreeCull::checkProjectionArea</c> (<c>llvieweroctree.cpp:1446</c>) with
///   <c>SceneLoadFrontPixelThreshold = 100</c> / <c>SceneLoadRearPixelThreshold = 400</c>
///   (<c>llvocache.cpp:467-473</c>, <c>settings.xml</c>): the viewer does refuse to LOAD an object
///   whose projected size is under a pixel threshold. That is the one place it treats "too small to
///   matter" as a reason to skip an object, and it is expressed in screen pixels, not metres.</item>
/// <item><c>LLVOVolume::calcLOD</c> (<c>llvovolume.cpp:1507</c>), ported as
///   <see cref="VolumeLod"/>: <c>RenderVolumeLODFactor</c> appears in the tangent numerator and in
///   <c>distance *= 1 - factor * 0.1</c>, so every distance the viewer derives from an object's size
///   scales with <c>factor / (1 - 0.1 * factor)</c>. <see cref="LodScale"/> is that ratio, so the
///   Object Detail slider moves this cull the way it moves the mesh LOD thresholds.</item>
/// </list>
///
/// <para><b>The rule.</b> Hide an object once the screen radius of its bounding sphere is under
/// <c>pixelRadius</c> pixels (at LOD factor 1), i.e. beyond
/// <c>radius / tan(pixelRadius / sCurPixelAngle) * LodScale(lodFactor)</c> metres. Never inside
/// <see cref="MinEndMetres"/>, never for an object of <see cref="NeverCullRadius"/> or more, and
/// never beyond the draw distance (the sweep already hides there).</para>
/// </summary>
public static class SmallObjectCull
{
    /// <summary>The default threshold: screen radius, in pixels, under which a prim is not drawn at LOD
    /// factor 1. 3 px is a 6 px dot at 1080p / 60 degrees.</summary>
    public const float DefaultPixelRadius = 3f;

    /// <summary>The shadow threshold is this many times the main one: a shadow is blurred (soft-shadow
    /// filter) and carries less detail than the object, so it can go earlier.</summary>
    public const float ShadowPixelFactor = 4f;

    /// <summary>Nothing is culled closer than this, whatever its size. A zoomed camera or a person
    /// inspecting jewellery at arm's length must always see it.</summary>
    public const float MinEndMetres = 15f;

    /// <summary>An object of at least this bounding radius (half the diagonal of its bounds) is never
    /// culled by size. Beyond any draw distance it would be under the threshold anyway; this makes the
    /// guarantee independent of the draw distance setting.</summary>
    public const float NeverCullRadius = 1.5f;

    /// <summary>With fade off the engine uses the margin as hysteresis: an instance that is hidden
    /// must come this much inside the end to reappear, one that is shown stays until this much
    /// outside it. As a fraction of the end distance; stops an object at the edge flickering as the
    /// camera bobs.</summary>
    public const float MarginFraction = 0.1f;

    /// <summary>The ratio the viewer scales every size-derived distance by, relative to LOD factor 1
    /// (<c>factor / (1 - 0.1 * factor)</c>, normalised so that 1.0 gives 1.0). 1.25 gives 1.29, 2.0
    /// gives 2.25, 3.0 gives 3.86.</summary>
    public static float LodScale(float lodFactor)
    {
        if (!(lodFactor > 0f)) return 1f;
        float f = System.Math.Min(lodFactor, 8f);
        float distanceFactor = System.Math.Max(1f - f * 0.1f, 0.1f);
        return 0.9f * f / distanceFactor;
    }

    /// <summary>
    /// The distance beyond which an object of bounding radius <paramref name="radius"/> is not drawn,
    /// or 0 for "no limit" (the value <c>VisibilityRangeEnd</c> reads as "never culled").
    /// </summary>
    /// <param name="pixelsPerRadian">The viewer's <c>sCurPixelAngle</c>: viewport height in pixels over
    /// the vertical field of view in radians.</param>
    /// <param name="pixelRadius">The threshold; 0 or less turns the cull off.</param>
    /// <param name="lodFactor">The Object Detail setting (<c>RenderVolumeLODFactor</c>).</param>
    /// <param name="hideDistance">Where the cull sweep hides the object anyway. A range at or beyond it
    /// would never act, so it is reported as "no limit" and the engine skips the check.</param>
    public static float EndDistance(float radius, float pixelsPerRadian, float pixelRadius, float lodFactor,
                                    float hideDistance)
    {
        if (!(pixelRadius > 0f) || !(radius > 0f) || !(pixelsPerRadian > 0f)) return 0f;
        if (radius >= NeverCullRadius) return 0f;

        float d = Distance(radius, pixelsPerRadian, pixelRadius) * LodScale(lodFactor);
        if (!(d > 0f)) return 0f;
        d = System.Math.Max(d, MinEndMetres);
        return d >= hideDistance ? 0f : d;
    }

    /// <summary>
    /// The distance beyond which the object stops casting a shadow, or 0 for "no extra limit". The
    /// renderer takes the smaller of this and <c>RenderConfig.ShadowCasterDistance</c>.
    /// </summary>
    public static float ShadowEndDistance(float radius, float pixelsPerRadian, float pixelRadius, float lodFactor)
    {
        if (!(pixelRadius > 0f) || !(radius > 0f) || !(pixelsPerRadian > 0f)) return 0f;

        float d = Distance(radius, pixelsPerRadian, pixelRadius * ShadowPixelFactor) * LodScale(lodFactor);
        if (!(d > 0f)) return 0f;
        return System.Math.Max(d, MinEndMetres);
    }

    /// <summary>The engine's hysteresis margin for a given end distance.</summary>
    public static float Margin(float end) => end > 0f ? System.Math.Max(end * MarginFraction, 1f) : 0f;

    /// <summary>The inverse of <c>calcPixelArea</c>'s radius: how far away a sphere of
    /// <paramref name="radius"/> metres is when it spans <paramref name="pixels"/> pixels of radius.</summary>
    private static float Distance(float radius, float pixelsPerRadian, float pixels)
    {
        float angle = pixels / pixelsPerRadian;
        // A threshold of half the screen or more means "cull everything", which is a mistake in the
        // setting, not a request; fall back to the small-angle form instead of tan blowing up.
        return angle < 1.2f ? radius / (float)System.Math.Tan(angle) : radius / angle;
    }
}
