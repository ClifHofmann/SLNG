using System;
using Godot;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>FEAT-RENDER-27: see <see cref="ObjectRenderer.SelfTestTargetOmega"/>. The passing
    /// detail ends with what the per-frame pass costs per prim.</summary>
    private static Check CheckTargetOmega(SceneTree tree)
    {
        const string Name = "target omega (llTargetOmega) objects";
        var objects = new ObjectRenderer();
        try
        {
            tree.Root.AddChild(objects);
            var world = new SLNG.Core.ECS.World();
            objects.Initialize(world, null!, null!);
            var (passed, detail) = objects.SelfTestTargetOmega(world);
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

    /// <summary>FEAT-RENDER-27: see <see cref="AvatarRenderer.SelfTestCarriedBySeat"/>.</summary>
    private static Check CheckTargetOmegaSitter(SceneTree tree)
    {
        const string Name = "target omega carries sitters";
        var renderer = new AvatarRenderer();
        try
        {
            tree.Root.AddChild(renderer);
            var (passed, detail) = renderer.SelfTestCarriedBySeat();
            return new Check(Name, passed, detail);
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(renderer)) renderer.QueueFree();
        }
    }
}
