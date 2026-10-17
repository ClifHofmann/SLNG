using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>FEAT-PERF-23 Phase 0: the count probe and the merge toggle on a hand-built avatar (see
    /// <c>AvatarRenderer.SelfTestOutfitProbe</c>).</summary>
    private static Check CheckOutfitProbe(SceneTree tree)
    {
        const string Name = "outfit probe counts, merges and restores";
        var renderer = new AvatarRenderer();
        try
        {
            var (passed, detail) = renderer.SelfTestOutfitProbe(tree);
            return new Check(Name, passed, detail);
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            renderer.Free();
        }
    }

    /// <summary>FEAT-PERF-23 Phase 0, probe 2: the main-thread cost of packing and handing one skinned
    /// surface to the engine at 10k, 50k and 65k vertices. Prints the numbers; never fails on timing,
    /// only if the bench itself breaks. Headless means the dummy renderer, so this is the CPU part
    /// (<c>mesh_create_surface_data_from_arrays</c>), which runs on the calling thread with any renderer.</summary>
    private static Check CheckOutfitMergeUploadBench()
    {
        const string Name = "outfit merge upload bench";
        try
        {
            var parts = new List<string>();
            foreach (int vertices in new[] { 10_000, 50_000, 65_000 })
            {
                var (packMs, uploadMs) = AvatarRenderer.BenchSkinnedUpload(vertices);
                parts.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0}k verts: pack {1:F2} ms, AddSurfaceFromArrays {2:F2} ms", vertices / 1000, packMs, uploadMs));
            }
            string detail = string.Join("; ", parts) + " (median of 5, main thread, headless)";
            GD.Print($"[OutfitProbe] upload bench: {detail}");
            return new Check(Name, true, detail);
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
