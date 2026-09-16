namespace SLNG.Core;

/// <summary>
/// FEAT-PERF-07: which of an uploaded SL mesh asset's four baked LOD blocks an object should draw,
/// using the reference viewer's own arithmetic.
///
/// <para>Ported from <c>LLVOVolume::calcLOD</c> + <c>LLVOVolume::computeLODDetail</c>
/// (<c>indra/newview/llvovolume.cpp:1507-1660</c>) and <c>LLVolumeLODGroup::getDetailFromTan</c>
/// (<c>indra/llmath/llvolumemgr.cpp:328-340</c>). Copied rather than approximated because these
/// thresholds are what decide whether SLNG shows a mesh at the detail Firestorm shows it at from
/// the same spot -- an invented curve gets judged against the real client and loses.</para>
///
/// <para>Lives in <c>SLNG.Core</c> rather than next to the renderer that uses it because it is
/// pure arithmetic over a distance and a scale, with no engine or protocol in it -- and because
/// a viewer-parity formula that cannot be unit-tested is a formula nobody can safely touch again.
/// </para>
/// </summary>
public static class VolumeLod
{
    /// <summary>Second Life's own shipped default for <c>RenderVolumeLODFactor</c>
    /// (<c>indra/newview/app_settings/settings.xml</c>). Firestorm ships higher and its users
    /// routinely raise it further, which is the usual reason content "looks blockier in SLNG" --
    /// compare the two viewers' Object Detail sliders before chasing a rendering bug.</summary>
    public const float DefaultLodFactor = 1.0f;

    /// <summary><c>mLODScaleBias</c> for a MESH volume: a flat (0.5, 0.5, 0.5)
    /// (<c>llvolume.cpp:2065</c>). The cylinder (0.6, 0.6, 0) and circle-path (0.6, 0.6, 0.6)
    /// overrides below it are explicitly gated on the volume NOT being <c>LL_SCULPT_TYPE_MESH</c>,
    /// so an uploaded mesh never gets them however its prim params happen to read. Being uniform,
    /// it factors straight out of the vector length.</summary>
    private const float MeshLodScaleBias = 0.5f;

    /// <summary><c>BASE_THRESHOLD * {1, 2, 8}</c> with <c>BASE_THRESHOLD = 0.03f</c>
    /// (<c>llvolumemgr.cpp:33-39</c>). The table's fourth entry (100x) is unreachable: the
    /// viewer's loop stops at <c>NUM_LODS-1</c> and returns the top level by falling out.</summary>
    private static readonly float[] DetailThresholds = { 0.03f, 0.06f, 0.24f };

    /// <summary>
    /// The level to draw an object of <paramref name="scaleLength"/> metres (the LENGTH of its
    /// scale vector -- axis convention is irrelevant) seen from <paramref name="distance"/> metres.
    /// </summary>
    /// <param name="lodFactor">The viewer's <c>RenderVolumeLODFactor</c>, "Object Detail". It is
    /// used THREE times in here -- as the tangent numerator, as the near-ramp distance, and as
    /// <c>sDistanceFactor = 1 - factor*0.1</c> (<c>llappviewer.cpp:564</c>) -- which is why the
    /// viewer exposes one number rather than a set of distances.</param>
    public static MeshDetailLevel ForDistance(float distance, float scaleLength, float lodFactor)
    {
        float radius = scaleLength * MeshLodScaleBias;
        if (!(radius > 0f) || !(distance > 0f) || !(lodFactor > 0f)) return MeshDetailLevel.Highest;

        distance *= 1f - lodFactor * 0.1f;
        if (!(distance > 0f)) return MeshDetailLevel.Highest;

        // "Boost LOD when you're REALLY close" (llvovolume.cpp:1597-1606). Inside the ramp the
        // tangent grows with the SQUARE of proximity, which is what stops a large object going
        // coarse just because the camera is pressed against it and the distance is near zero.
        float rampDist = lodFactor * 2f;
        if (distance < rampDist)
        {
            distance *= 1f / rampDist;
            distance *= distance;
            distance *= rampDist;
        }

        distance *= (float)System.Math.PI / 3f;
        if (!(distance > 0f)) return MeshDetailLevel.Highest;

        return ForTangent(lodFactor * radius / distance);
    }

    /// <summary><c>getDetailFromTan</c>: the first threshold the angle falls under wins; falling
    /// under none means the highest level.</summary>
    public static MeshDetailLevel ForTangent(float tanAngle)
    {
        if (tanAngle <= DetailThresholds[0]) return MeshDetailLevel.Low;      // lowest_lod
        if (tanAngle <= DetailThresholds[1]) return MeshDetailLevel.Medium;   // low_lod
        if (tanAngle <= DetailThresholds[2]) return MeshDetailLevel.High;     // medium_lod
        return MeshDetailLevel.Highest;                                       // high_lod
    }

    /// <summary>How far past a threshold an object has to travel before its level actually
    /// changes, as a fraction of the distance.
    ///
    /// <para>The reference viewer has none of this -- it switches the instant <c>calcLOD</c>
    /// disagrees. It can afford to: it re-uses an <c>LLVolume</c> it already holds. A switch in
    /// SLNG re-keys the shared mesh and re-applies every face's material, so an object parked on
    /// a threshold while the camera bobs with the walk animation would pay that on every sweep
    /// tick, forever. Same shape and same order of magnitude as the visibility band (1.15x) and
    /// <c>RenderConfig.ShadowCasterDistance</c> (0.9x) already use for exactly this reason.</para>
    /// </summary>
    public const float Hysteresis = 0.1f;

    /// <summary>
    /// The level to move to from <paramref name="current"/>, or <paramref name="current"/> itself
    /// while the object is still inside the dead band around its threshold.
    ///
    /// <para>Asymmetric on purpose: the nudge always opposes the change being proposed, so both
    /// boundaries repel. Nudging in one fixed direction would instead make one of the two
    /// crossings easier than the other and bias the whole scene's detail.</para>
    /// </summary>
    public static MeshDetailLevel ForDistanceWithHysteresis(
        float distance, float scaleLength, float lodFactor, MeshDetailLevel current)
    {
        var want = ForDistance(distance, scaleLength, lodFactor);
        if (want == current) return current;

        // Re-ask with the object pretended nearer (resisting a drop) or further (resisting a
        // raise). Expressed as a distance scale because distance is the only input this takes,
        // and -- outside the near ramp -- the tangent is inversely proportional to it.
        float resist = want < current ? 1f / (1f + Hysteresis) : 1f + Hysteresis;
        return ForDistance(distance * resist, scaleLength, lodFactor) == current ? current : want;
    }
}
