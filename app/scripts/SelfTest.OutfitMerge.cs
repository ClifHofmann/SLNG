using System;
using Godot;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>FEAT-PERF-23 Phase 1: the real outfit merge on a hand-built remote avatar (see
    /// <c>AvatarRenderer.SelfTestOutfitMerge</c>).</summary>
    private static Check CheckOutfitMerge(SceneTree tree) =>
        RunOnRenderer(tree, "outfit merge skins like separate items", r => r.SelfTestOutfitMerge());

    /// <summary>FEAT-PERF-23 fix: with the merge off, a parked avatar draws no worn instance and the cap holds
    /// (<c>AvatarRenderer.SelfTestParkedOutfitHidden</c>).</summary>
    private static Check CheckParkedOutfitHidden(SceneTree tree) =>
        RunOnRenderer(tree, "parked avatars draw no outfit, cap holds (merge off)", r => r.SelfTestParkedOutfitHidden());

    /// <summary>FEAT-PERF-23 fix: the runtime switch off, on, off again -- also mid-un-merge
    /// (<c>AvatarRenderer.SelfTestOutfitMergeToggle</c>).</summary>
    private static Check CheckOutfitMergeToggle(SceneTree tree) =>
        RunOnRenderer(tree, "outfit merge switch off/on/off", r => r.SelfTestOutfitMergeToggle());

    private static Check RunOnRenderer(SceneTree tree, string name, Func<AvatarRenderer, (bool Passed, string Detail)> test)
    {
        var renderer = new AvatarRenderer();
        try
        {
            tree.Root.AddChild(renderer);
            var (passed, detail) = test(renderer);
            return new Check(name, passed, detail);
        }
        catch (Exception ex)
        {
            return new Check(name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            renderer.GetParent()?.RemoveChild(renderer);
            renderer.Free();
        }
    }
}
