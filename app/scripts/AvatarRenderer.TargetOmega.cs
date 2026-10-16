using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App;

public partial class AvatarRenderer
{
    /// <summary>--diag: sitters already reported as carried by a turning seat, with the seat.</summary>
    private readonly HashSet<(Guid Avatar, uint Seat)> _carriedSittersLogged = new();

    /// <summary>FEAT-RENDER-27: moves a sitter with a seat that client-side target omega has turned
    /// (the hammock rocking, a carousel going round). The viewer hangs a sitter's drawable under its
    /// seat's (<c>LLVOAvatar::sitOnObject</c>), so whatever turns the seat -- its own omega or its
    /// root's -- turns the sitter rigidly with it, about the seat. GridSession resolved the sitter's
    /// world transform at packet time from the simulator's seat transform, which knows nothing of the
    /// client-side turn; that turn is put on top here, on the already placed Root.
    ///
    /// <para>Runs every frame for a seated avatar (WorldSimulation.ExtrapolateMovement notifies every
    /// avatar each frame), after the objects were turned for the frame. A seat that does not turn
    /// costs one component lookup.</para></summary>
    private void CarryWithTurningSeat(AvatarVisual visual, Entity entity, AvatarComponent avatar)
    {
        if (_world == null) return;
        var seat = _world.GetEntity(entity.RegionHandle, avatar.SittingOnLocalId);
        var seatTransform = seat?.GetComponent<TransformComponent>();
        if (seat == null || seatTransform == null
            || !TargetOmega.TryGetSeatTurn(_world, seat, seatTransform, out var seatAt, out var seatDrawnAt, out var turn))
            return;

        // The same rigid move in Godot's axes (SL Z up, SL Y = -Godot Z): a proper rotation of the
        // axes, so the quaternion converts like any other.
        var from = RenderConfig.ToGodot(seat.RegionHandle, seatAt);
        var to = RenderConfig.ToGodot(seat.RegionHandle, seatDrawnAt);
        var godotTurn = new Godot.Quaternion(turn.X, turn.Z, -turn.Y, turn.W);

        var root = visual.Root;
        var position = to + godotTurn * (root.Position - from);
        root.Position = position;
        visual.GodotPos = position;
        root.Quaternion = (godotTurn * root.Quaternion).Normalized();

        if (Diagnostics.Enabled && _carriedSittersLogged.Add((visual.AgentId, avatar.SittingOnLocalId)))
        {
            GD.Print($"[TargetOmega] sitter avatar={visual.AgentId.ToString()[..8]} ({avatar.FirstName}) " +
                     $"carried by turning seat localId={avatar.SittingOnLocalId}");
        }
    }
}
