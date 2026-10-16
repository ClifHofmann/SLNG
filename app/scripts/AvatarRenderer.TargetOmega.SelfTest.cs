using System;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App;

public partial class AvatarRenderer
{
    /// <summary>FEAT-RENDER-27: an avatar sitting a metre out on a seat that target omega has turned a
    /// quarter about Z is carried round the seat's centre and turned with it; on a seat that does not
    /// turn it stays exactly where UpdateVisual put it.</summary>
    internal (bool Passed, string Detail) SelfTestCarriedBySeat()
    {
        const ulong Region = 11437119954698752UL;
        var savedWorld = _world;
        var world = new World();
        _world = world;
        var visual = new AvatarVisual();
        AddChild(visual.Root);
        try
        {
            var at = new System.Numerics.Vector3(100f, 100f, 20f);
            var seat = world.GetOrCreateEntity(Region, 7001);
            seat.SetComponent(new TransformComponent(at, System.Numerics.Quaternion.Identity));
            var spin = new SpinComponent();
            spin.ApplyUpdate(System.Numerics.Quaternion.Identity, new System.Numerics.Vector3(0f, 0f, MathF.PI / 2f),
                isPhysical: false, zeroOmegaResets: true);
            spin.Advance(1f);
            seat.SetComponent(spin);

            var sitter = world.GetOrCreateEntity(Region, 7002);
            var avatar = new AvatarComponent(Guid.NewGuid(), "Seat", "Tester", false) { SittingOnLocalId = 7001 };
            sitter.SetComponent(avatar);

            // Where UpdateVisual leaves it: a metre out along SL +X, half a metre up, facing SL +X.
            var placed = RenderConfig.ToGodot(Region, at + new System.Numerics.Vector3(1f, 0f, 0.5f));
            visual.Root.Position = placed;
            visual.Root.Quaternion = Godot.Quaternion.Identity;
            CarryWithTurningSeat(visual, sitter, avatar);

            var expectedAt = RenderConfig.ToGodot(Region, at + new System.Numerics.Vector3(0f, 1f, 0.5f));
            var quarter = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, MathF.PI / 2f);
            var expectedRotation = new Godot.Quaternion(quarter.X, quarter.Z, -quarter.Y, quarter.W);
            if (visual.Root.Position.DistanceTo(expectedAt) > 1e-3f)
                return (false, $"sitter not carried round the seat: {visual.Root.Position} vs {expectedAt}");
            if (Mathf.Abs(visual.Root.Quaternion.Dot(expectedRotation)) < 0.9995f)
                return (false, $"sitter not turned with the seat: {visual.Root.Quaternion}");
            if (visual.GodotPos != visual.Root.Position)
                return (false, "GodotPos (name tag, pixel area) left behind");

            seat.RemoveComponent<SpinComponent>();
            visual.Root.Position = placed;
            visual.Root.Quaternion = Godot.Quaternion.Identity;
            CarryWithTurningSeat(visual, sitter, avatar);
            if (visual.Root.Position != placed || visual.Root.Quaternion != Godot.Quaternion.Identity)
                return (false, "a seat that does not turn moved its sitter");

            return (true, "sitter carried a quarter turn round its turning seat; a still seat leaves it alone");
        }
        finally
        {
            _world = savedWorld;
            visual.Root.QueueFree();
        }
    }
}
