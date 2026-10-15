using System;
using Godot;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>FEAT-PERF-20 Phase 0: see <see cref="ObjectRenderer.SelfTestBatchPotential"/>. The
    /// passing detail is the [BatchPotential] line the hand-counted scene produced.</summary>
    private static Check CheckBatchPotential(SceneTree tree)
    {
        const string Name = "batch potential tally";
        var objects = new ObjectRenderer();
        try
        {
            tree.Root.AddChild(objects);
            var world = new SLNG.Core.ECS.World();
            objects.Initialize(world, null!, null!);
            var (passed, detail) = objects.SelfTestBatchPotential(world);
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

    /// <summary>FEAT-PERF-20 Phase 0: see <see cref="ObjectRenderer.SelfTestBatchPotentialCost"/>. The
    /// passing detail is the measured cost per visual.</summary>
    private static Check CheckBatchPotentialCost(SceneTree tree)
    {
        const string Name = "batch potential cost";
        var objects = new ObjectRenderer();
        try
        {
            tree.Root.AddChild(objects);
            var world = new SLNG.Core.ECS.World();
            objects.Initialize(world, null!, null!);
            var (passed, detail) = objects.SelfTestBatchPotentialCost(world);
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
}
