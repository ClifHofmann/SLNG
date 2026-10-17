using System;
using Godot;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>FEAT-PERF-23 Phase 1: the real outfit merge on a hand-built remote avatar (see
    /// <c>AvatarRenderer.SelfTestOutfitMerge</c>).</summary>
    private static Check CheckOutfitMerge(SceneTree tree)
    {
        const string Name = "outfit merge skins like separate items";
        var renderer = new AvatarRenderer();
        try
        {
            tree.Root.AddChild(renderer);
            var (passed, detail) = renderer.SelfTestOutfitMerge();
            return new Check(Name, passed, detail);
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            renderer.GetParent()?.RemoveChild(renderer);
            renderer.Free();
        }
    }
}
