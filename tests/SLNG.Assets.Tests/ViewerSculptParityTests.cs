using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using SLNG.Assets;
using Xunit;
using Xunit.Abstractions;

namespace SLNG.Assets.Tests;

/// <summary>
/// TEMPORARY diagnostic (fix/broken-mesh-objects): compares SLNG's sculpt mesher output against a
/// faithful C# port of the real SL viewer's sculpt algorithm
/// (LLVolume::sculpt / sculpt_calc_mesh_resolution / LLVolume::sculptGenerateMapVertices,
/// indra/llmath/llvolume.cpp) run on the SAME real sculpt map, so "does our geometry match
/// Firestorm's" stops being a screenshot argument.
/// </summary>
public class ViewerSculptParityTests
{
    private readonly ITestOutputHelper _out;
    public ViewerSculptParityTests(ITestOutputHelper output) => _out = output;

    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Godot", "app_userdata", "Puris Viewer", "cache", "assets");

    // --- viewer port -----------------------------------------------------------------

    private const int SCULPT_REZ_4 = 32;

    private static void CalcMeshResolution(int width, int height, float detail, out int s, out int t)
    {
        int maxVertsLod = SCULPT_REZ_4 * SCULPT_REZ_4;      // detail 4.0 (Highest)
        int maxVertsMap = width * height / 4;
        int vertices = maxVertsMap > 0 ? Math.Min(maxVertsLod, maxVertsMap) : maxVertsLod;

        float ratio = (width == 0 || height == 0) ? 1f : (float)width / height;

        s = (int)MathF.Sqrt(vertices / ratio);
        s = Math.Max(s, 4);
        t = vertices / s;
        t = Math.Max(t, 4);
        s = vertices / t;
    }

    private static Vector3 Sample(byte[] rgba, int w, int h, int x, int y)
    {
        int i = (y * w + x) * 4;
        return new Vector3(rgba[i] / 255f - 0.5f, rgba[i + 1] / 255f - 0.5f, rgba[i + 2] / 255f - 0.5f);
    }

    /// <summary>Port of LLVolume::sculptGenerateMapVertices. Returns the sizeS x sizeT grid.</summary>
    private static List<Vector3> ViewerMesh(byte[] rgba, int w, int h, byte sculptType, out int sizeS, out int sizeT)
    {
        int stitching = sculptType & 0x07;
        bool invert = (sculptType & 0x40) != 0;
        bool mirror = (sculptType & 0x80) != 0;
        bool reverseHorizontal = invert ? !mirror : mirror;   // XOR

        CalcMeshResolution(w, h, 4.0f, out int s0, out int t0);
        // genNGon emits sides+1 points, so the grid closes on itself (the last row/column is the
        // stitching duplicate the switch below relies on).
        sizeS = s0 + 1;
        sizeT = t0 + 1;

        var mesh = new List<Vector3>(sizeS * sizeT);
        for (int s = 0; s < sizeS; s++)
        {
            for (int t = 0; t < sizeT; t++)
            {
                int reversedT = reverseHorizontal ? sizeT - t - 1 : t;

                int x = (int)((float)reversedT / (sizeT - 1) * w);
                int y = (int)((float)s / (sizeS - 1) * h);

                if (y == 0 && stitching == 1) x = w / 2;                 // sphere top pinch
                if (y == h)
                {
                    y = stitching == 2 ? 0 : h - 1;                      // torus wraps, else clamp
                    if (stitching == 1) x = w / 2;                       // sphere bottom pinch
                }
                if (x == w)
                {
                    x = (stitching == 1 || stitching == 2 || stitching == 4) ? 0 : w - 1;
                }

                var p = Sample(rgba, w, h, x, y);
                if (mirror) p.X = -p.X;
                mesh.Add(p);
            }
        }
        return mesh;
    }

    // --- comparison ------------------------------------------------------------------

    private static (Vector3 min, Vector3 max) Aabb(IReadOnlyList<Vector3> v)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in v) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        return (min, max);
    }

    private static (float max, float mean) OneSidedHausdorff(IReadOnlyList<Vector3> from, IReadOnlyList<Vector3> to)
    {
        float worst = 0, sum = 0;
        foreach (var p in from)
        {
            float best = float.MaxValue;
            foreach (var q in to)
            {
                float d = Vector3.DistanceSquared(p, q);
                if (d < best) best = d;
            }
            best = MathF.Sqrt(best);
            sum += best;
            if (best > worst) worst = best;
        }
        return (worst, sum / from.Count);
    }

    [Theory]
    // The Dangazi Forest tree linkset (docs/HANDOVER_TREE_BUG.md)
    [InlineData("18129dff-d039-4ac3-a7ce-bfc786341367", (byte)4)]   // root trunk, cylinder
    [InlineData("44af13fe-bd70-4ab4-bd7f-65fd848eec44", (byte)(4 | 0x80))] // branch, cylinder + mirror
    // A plain sphere-stitched sculpt and a plane-stitched one, for cross-type coverage
    [InlineData("7f7173db-c802-4c7b-9870-48102b61b6a6", (byte)1)]
    [InlineData("78d64a57-4020-4cb5-8c22-5076fc7f66f8", (byte)3)]
    public void OurSculptMesh_MatchesViewerAlgorithm(string id, byte sculptType)
    {
        string file = Path.Combine(CacheDir, id + "_v5.j2c");
        if (!File.Exists(file))
        {
            // Needs a real sculpt map from an actual client session's asset cache -- there's
            // nothing to check into the repo as a fixture (it's grid content, not ours to
            // redistribute), so CI and any machine without that local cache skip rather than
            // fail. Re-run locally after logging in and viewing the object to get real coverage.
            _out.WriteLine($"SKIPPED (no local cache): {file}");
            return;
        }

        var tex = AssetService.DecodeTexture(File.ReadAllBytes(file), isSculpt: true);
        Assert.NotNull(tex);
        int w = tex!.Width, h = tex.Height;

        var ours = PrimMeshService.GenerateSculpt(tex.Rgba, w, h, sculptType);
        Assert.NotNull(ours);
        var ourVerts = new List<Vector3>();
        foreach (var sm in ours!.Submeshes) ourVerts.AddRange(sm.Positions);

        var theirs = ViewerMesh(tex.Rgba, w, h, sculptType, out int sizeS, out int sizeT);

        var (oMin, oMax) = Aabb(ourVerts);
        var (vMin, vMax) = Aabb(theirs);
        var (maxOT, meanOT) = OneSidedHausdorff(ourVerts, theirs);
        var (maxTO, meanTO) = OneSidedHausdorff(theirs, ourVerts);

        _out.WriteLine($"map {id} type={sculptType} native={w}x{h}");
        _out.WriteLine($"  ours   verts={ourVerts.Count,5}  aabb min={oMin} max={oMax}");
        _out.WriteLine($"  viewer verts={theirs.Count,5} ({sizeS}x{sizeT}) aabb min={vMin} max={vMax}");
        _out.WriteLine($"  ours->viewer  max={maxOT:F4} mean={meanOT:F4}");
        _out.WriteLine($"  viewer->ours  max={maxTO:F4} mean={meanTO:F4}");

        // A sculpt lives in [-0.5,0.5]; 0.05 = 5% of the prim's full extent. Anything above that
        // is a genuinely different surface, not a resampling difference.
        Assert.True(Math.Max(maxOT, maxTO) < 0.05f,
            $"sculpt surface deviates from the viewer's by {Math.Max(maxOT, maxTO):F4} units");
    }

    /// <summary>
    /// Pins the invariant both mesh caches were violating: the sculpt TYPE byte materially changes
    /// the geometry, so it is part of the cache identity, not a detail of the map. One sculpt map
    /// reused with different flags inside a linkset is normal SL authoring (a tree's left and right
    /// branch), and keying either the in-flight table (AssetService) or the GpuCache
    /// (ObjectRenderer) by the map UUID alone handed the second prim the first prim's mesh.
    /// </summary>
    [Fact]
    public void MirrorFlag_ProducesDifferentGeometry_SoTheTypeByteIsPartOfTheCacheKey()
    {
        const string id = "44af13fe-bd70-4ab4-bd7f-65fd848eec44";  // the tree's branch sculpt
        string file = Path.Combine(CacheDir, id + "_v5.j2c");
        if (!File.Exists(file))
        {
            // See the identical skip in OurSculptMesh_MatchesViewerAlgorithm above.
            _out.WriteLine($"SKIPPED (no local cache): {file}");
            return;
        }

        var tex = AssetService.DecodeTexture(File.ReadAllBytes(file), isSculpt: true);
        Assert.NotNull(tex);

        var plain = PrimMeshService.GenerateSculpt(tex!.Rgba, tex.Width, tex.Height, 4);
        var mirrored = PrimMeshService.GenerateSculpt(tex.Rgba, tex.Width, tex.Height, 4 | 0x80);
        Assert.NotNull(plain);
        Assert.NotNull(mirrored);

        var a = plain!.Submeshes[0].Positions;
        var b = mirrored!.Submeshes[0].Positions;
        Assert.Equal(a.Length, b.Length);

        float maxDelta = 0;
        for (int i = 0; i < a.Length; i++) maxDelta = MathF.Max(maxDelta, Vector3.Distance(a[i], b[i]));

        _out.WriteLine($"mirror vs plain: max per-vertex delta = {maxDelta:F4}");
        Assert.True(maxDelta > 0.1f,
            "Mirroring must visibly change the geometry — if it did not, sharing one cached mesh " +
            "between mirrored and unmirrored prims would be harmless, and it is not.");
    }
}
