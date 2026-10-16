using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App;

/// <summary>FEAT-RENDER-27: draws objects turning under client-side target omega (llTargetOmega).
///
/// <para>The turning itself is <see cref="WorldSimulation.AdvanceSpins"/>'s, once per frame before
/// this runs; this only moves the nodes, and only those of <see cref="SpinDriven"/> -- the turning
/// prims and the prims linked under them, a few dozen on a busy region against tens of thousands of
/// objects. Nothing is allocated per frame. A spin-driven prim keeps its own node (it is never put in
/// a MultiMesh group, see <see cref="EvaluateInstancing"/>), so its collider, light, particles and
/// split surfaces, all children of that node, turn with it.</para>
///
/// <para>Avatars sitting on a turning seat are carried by <c>AvatarRenderer</c>, which places them
/// every frame anyway.</para></summary>
public partial class ObjectRenderer
{
    /// <summary>The world simulation's <see cref="WorldSimulation.SpinDrivenEntities"/>, set at login.
    /// Read only on the main thread.</summary>
    internal IReadOnlyList<Entity>? SpinDriven { get; set; }

    /// <summary>--diag: turning objects already named once.</summary>
    private readonly HashSet<Guid> _spinLogged = new();

    private void TickTargetOmega()
    {
        var driven = SpinDriven;
        if (driven == null || driven.Count == 0 || _world == null) return;

        double now = NowSeconds();
        for (int i = 0; i < driven.Count; i++)
        {
            var entity = driven[i];
            if (!_visuals.TryGetValue(entity.Id, out var state) || state.PendingRemoval) continue;
            var transform = entity.GetComponent<TransformComponent>();
            if (transform == null
                || !TargetOmega.TryGetRenderTransform(_world, entity, transform, out var position, out var rotation))
                continue;

            if (!state.SpinDriven)
            {
                state.SpinDriven = true;
                _instanceGroups?.Leave(entity.Id);
            }
            // The [BatchPotential] tally's "moved" -- a turning object is never static scenery.
            state.LastMovedAt = now;
            if (Diagnostics.Enabled) LogTurning(entity, transform);

            // A hidden prim is placed the frame it is shown again; the cull sweep never moves a node.
            if (!state.Shown || !IsInstanceValid(state.MeshInstance)) continue;

            state.Pos = RenderConfig.ToGodot(entity.RegionHandle, position);
            state.PlacedRotation = rotation;
            state.MeshInstance.Position = state.Pos;
            state.MeshInstance.Quaternion = new Godot.Quaternion(rotation.X, rotation.Z, -rotation.Y, rotation.W);
            // A child turning round its root covers ground: its dormant margin no longer holds.
            state.DormantUntilTravel = double.NegativeInfinity;
        }
    }

    /// <summary>--diag: one line per object that turns by itself, the first frame it does -- never per
    /// frame. Its linked children and its sitters turn with it and are not listed.</summary>
    private void LogTurning(Entity entity, TransformComponent transform)
    {
        if (entity.GetComponent<SpinComponent>() is not { IsTurning: true } spin || !_spinLogged.Add(entity.Id)) return;

        var meta = entity.GetComponent<MetadataComponent>();
        string id = meta == null || meta.Id == Guid.Empty ? "?" : meta.Id.ToString();
        string name = string.IsNullOrEmpty(meta?.Name) ? "" : $" \"{meta!.Name}\"";
        var omega = spin.AngularVelocity;
        float rate = omega.Length();
        string role = transform.ParentLocalId == 0 ? "root" : $"child of {transform.ParentLocalId}";
        bool physical = entity.GetComponent<PrimitiveComponent>()?.IsPhysical == true;
        GD.Print($"[TargetOmega] turning localId={entity.LocalId} id={id}{name} {role} " +
                 $"at=({transform.Position.X:0.##}, {transform.Position.Y:0.##}, {transform.Position.Z:0.##}) " +
                 $"omega=({omega.X:0.###}, {omega.Y:0.###}, {omega.Z:0.###}) rate={rate:0.###}rad/s " +
                 $"period={(rate > 0f ? MathF.Tau / rate : 0f):0.#}s dilation={spin.TimeDilation:0.##}" +
                 (physical ? " physical" : ""));
    }
}
