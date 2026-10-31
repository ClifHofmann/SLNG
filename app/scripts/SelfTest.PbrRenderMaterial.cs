using System;
using Godot;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>BUG-RENDER-49: a world-prim face with a render material id builds a PBR material
    /// (<c>ObjectRenderer.SelfTestPbrRenderMaterial</c>).</summary>
    private static Check CheckPbrRenderMaterialWorld(SceneTree tree)
    {
        const string Name = "PBR render material on a world prim";
        var objects = new ObjectRenderer();
        try
        {
            tree.Root.AddChild(objects);
            objects.Initialize(new SLNG.Core.ECS.World(), null!, null!);
            var (passed, detail) = objects.SelfTestPbrRenderMaterial();
            return new Check(Name, passed, detail);
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(objects)) objects.QueueFree();
        }
    }

    /// <summary>BUG-RENDER-49: a worn rigged mesh's face with a render material id builds a PBR material
    /// (<c>AvatarRenderer.SelfTestPbrRenderMaterial</c>).</summary>
    private static Check CheckPbrRenderMaterialWorn(SceneTree tree) =>
        RunOnRenderer(tree, "PBR render material on a worn rigged mesh", r => r.SelfTestPbrRenderMaterial());

    /// <summary>BUG-AVATAR-16: a worn rigged face's legacy material binds its normal and specular maps
    /// (<c>AvatarRenderer.SelfTestLegacyWornMaps</c>).</summary>
    private static Check CheckLegacyWornMaps(SceneTree tree) =>
        RunOnRenderer(tree, "legacy normal/specular maps on a worn rigged mesh", r => r.SelfTestLegacyWornMaps());
}
