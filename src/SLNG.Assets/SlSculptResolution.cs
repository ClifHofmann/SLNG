using System;

namespace SLNG.Assets;

/// <summary>
/// Port of the viewer's <c>sculpt_calc_mesh_resolution</c> (llvolume.cpp:3163-3196): how many
/// vertices a sculpt is sampled at, in each direction, for a given map size and detail level.
///
/// <para>PrimMesher does this differently and the difference is visible. It halves BOTH map
/// dimensions together until the total pixel count fits the LOD budget, which keeps the map's
/// aspect ratio only in powers of two. The viewer instead spends its whole vertex budget in the
/// proportion the map actually has.</para>
///
/// <para>Measured on OSGrid 2026-08-23, the Dangazi Forest reef rock (sculpt map
/// <c>0eed1d17</c>, native <b>512x16</b>): the viewer samples a 6x205 grid, 1230 vertices;
/// PrimMesher's halving gives 129x5, 645 vertices. Half the resolution along the long axis --
/// which on that rock is the axis the strata run along, so the relief came out shallower and a
/// cube resting on it floated above the surface instead of sinking into it as in Firestorm. Both
/// meshes stay within the 5% surface tolerance the existing parity test uses, which is why it
/// never caught this.</para>
///
/// <para>Square maps are unaffected: a 64x64 map yields 32x32 under either rule, which is why the
/// reef's flat wave sculpts matched exactly while the rock did not.</para>
/// </summary>
public static class SlSculptResolution
{
    // SCULPT_REZ_1..4 (llvolume.cpp:3134-3137). The comment on the first is the viewer's own:
    // "changed from 4 to 6 - 6 looks round whereas 4 looks square".
    private const int SculptRez1 = 6;
    private const int SculptRez2 = 8;
    private const int SculptRez3 = 16;
    private const int SculptRez4 = 32;

    /// <summary>Vertices per side allowed at this detail level (<c>sculpt_sides</c>,
    /// llvolume.cpp:3139). Detail is one of 1, 1.5, 2.5 or 4.</summary>
    public static int SidesForDetail(float detail) =>
        detail <= 1f ? SculptRez1 :
        detail <= 2f ? SculptRez2 :
        detail <= 3f ? SculptRez3 : SculptRez4;

    /// <summary>
    /// Vertex counts along the path (<paramref name="s"/>) and the profile (<paramref name="t"/>).
    ///
    /// <para>Note which is which: <c>sculptGenerateMapVertices</c> reads
    /// <c>x = t/(sizeT-1) * sculpt_width</c> and <c>y = s/(sizeS-1) * sculpt_height</c>, so
    /// <b>t runs across the map's WIDTH and s down its HEIGHT</b> -- the opposite of what the
    /// names suggest, and easy to transpose.</para>
    ///
    /// <para>The viewer's three stated properties, kept verbatim: the mesh's aspect ratio is as
    /// close to the map's as possible while still using every available vertex; it never exceeds
    /// what the LOD allows; and it never exceeds what the map can supply.</para>
    /// </summary>
    public static void Calc(int mapWidth, int mapHeight, float detail, out int s, out int t)
    {
        int sides = SidesForDetail(detail);
        int maxVerticesLod = sides * sides;
        int maxVerticesMap = mapWidth * mapHeight / 4;

        int vertices = maxVerticesMap > 0 ? Math.Min(maxVerticesLod, maxVerticesMap) : maxVerticesLod;

        float ratio = (mapWidth == 0 || mapHeight == 0) ? 1f : (float)mapWidth / mapHeight;

        s = (int)MathF.Sqrt(vertices / ratio);
        s = Math.Max(s, 4);          // "no degenerate sizes, please"
        t = vertices / s;
        t = Math.Max(t, 4);
        s = vertices / t;
    }
}
