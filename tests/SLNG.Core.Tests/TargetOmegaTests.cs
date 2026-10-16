using System;
using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-RENDER-27: client-side target omega, pinned against the viewer's rules in
/// llviewerobject.cpp (applyAngularVelocity :7305, the reset block in processUpdateMessage
/// :2349-2370, the OUT_FULL zero-omega reset :1416).</summary>
public class TargetOmegaTests
{
    private const float Eps = 1e-4f;
    private static readonly Vector3 SpinZ = new(0f, 0f, MathF.PI / 2f); // a quarter turn a second about Z

    private static void AssertRotation(Quaternion expected, Quaternion actual)
    {
        // q and -q are the same rotation.
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual)));
        Assert.True(dot > 1f - Eps, $"expected {expected}, got {actual} (|dot|={dot})");
    }

    private static void AssertVector(Vector3 expected, Vector3 actual, float eps = Eps)
        => Assert.True(Vector3.Distance(expected, actual) < eps, $"expected {expected}, got {actual}");

    private static Quaternion AboutZ(float radians) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians);

    private static void Run(SpinComponent spin, float seconds, int steps = 60)
    {
        for (int i = 0; i < steps; i++) spin.Advance(seconds / steps);
    }

    [Fact]
    public void Advance_turns_by_rate_times_time_about_the_omega_axis()
    {
        var spin = new SpinComponent();
        spin.ApplyUpdate(Quaternion.Identity, SpinZ, isPhysical: false, zeroOmegaResets: true);
        Run(spin, 1f);
        AssertRotation(AboutZ(MathF.PI / 2f), spin.Rotation);
    }

    [Fact]
    public void Below_the_viewer_threshold_nothing_turns()
    {
        var spin = new SpinComponent();
        spin.ApplyUpdate(Quaternion.Identity, new Vector3(0f, 0f, 0.003f), isPhysical: false, zeroOmegaResets: true); // 9e-6 rad^2/s^2
        Assert.False(spin.IsTurning);
        Run(spin, 10f);
        Assert.Equal(Quaternion.Identity, spin.Rotation);
    }

    /// <summary>The axis is in the PARENT's frame: LL applies the step after the object's own
    /// rotation (getRotation() * dQ, LL order). An object tipped 90 degrees about X and spun about
    /// region Z keeps its local +Y (which points up) fixed, not its local +Z.</summary>
    [Fact]
    public void The_turn_is_applied_after_the_server_rotation()
    {
        var tipped = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);
        var spin = new SpinComponent();
        spin.ApplyUpdate(tipped, SpinZ, isPhysical: false, zeroOmegaResets: true);
        Run(spin, 0.7f);

        var drawn = spin.Apply(tipped);
        AssertVector(Vector3.UnitZ, Vector3.Transform(Vector3.UnitY, drawn));
        Assert.True(Vector3.Distance(Vector3.Transform(Vector3.UnitZ, tipped), Vector3.Transform(Vector3.UnitZ, drawn)) > 0.1f,
            "the object's own Z axis must move: the spin is not about the local axis");
    }

    [Fact]
    public void A_new_server_rotation_starts_over()
    {
        var spin = new SpinComponent();
        spin.ApplyUpdate(Quaternion.Identity, SpinZ, isPhysical: false, zeroOmegaResets: false);
        Run(spin, 0.5f);
        Assert.NotEqual(Quaternion.Identity, spin.Rotation);

        spin.ApplyUpdate(AboutZ(0.2f), SpinZ, isPhysical: false, zeroOmegaResets: false);
        Assert.Equal(Quaternion.Identity, spin.Rotation);
    }

    /// <summary>The swing: its script only flips the omega's sign and never touches the rotation,
    /// so a non-physical object keeps what it turned and swings back from there to where it began.</summary>
    [Fact]
    public void A_flipped_omega_on_a_non_physical_object_swings_back()
    {
        var spin = new SpinComponent();
        spin.ApplyUpdate(Quaternion.Identity, SpinZ, isPhysical: false, zeroOmegaResets: false);
        Run(spin, 0.5f);
        AssertRotation(AboutZ(MathF.PI / 4f), spin.Rotation);

        spin.ApplyUpdate(Quaternion.Identity, -SpinZ, isPhysical: false, zeroOmegaResets: false);
        AssertRotation(AboutZ(MathF.PI / 4f), spin.Rotation); // kept, not snapped back
        Run(spin, 0.25f);
        AssertRotation(AboutZ(MathF.PI / 8f), spin.Rotation);
        Run(spin, 0.5f);
        AssertRotation(AboutZ(-MathF.PI / 8f), spin.Rotation); // through the start and out the other side
    }

    [Fact]
    public void A_changed_omega_on_a_physical_object_starts_over()
    {
        var spin = new SpinComponent();
        spin.ApplyUpdate(Quaternion.Identity, SpinZ, isPhysical: true, zeroOmegaResets: false);
        Run(spin, 0.5f);
        spin.ApplyUpdate(Quaternion.Identity, -SpinZ, isPhysical: true, zeroOmegaResets: false);
        Assert.Equal(Quaternion.Identity, spin.Rotation);
    }

    [Fact]
    public void An_update_that_changes_neither_keeps_what_was_turned()
    {
        var spin = new SpinComponent();
        spin.ApplyUpdate(Quaternion.Identity, SpinZ, isPhysical: false, zeroOmegaResets: true);
        Run(spin, 0.5f);
        var turned = spin.Rotation;
        spin.ApplyUpdate(Quaternion.Identity, SpinZ, isPhysical: false, zeroOmegaResets: true); // e.g. a texture change
        Assert.Equal(turned, spin.Rotation);
        Run(spin, 0.5f);
        AssertRotation(AboutZ(MathF.PI / 2f), spin.Rotation);
    }

    [Fact]
    public void A_new_axis_turns_on_from_where_the_object_is()
    {
        var spin = new SpinComponent();
        spin.ApplyUpdate(Quaternion.Identity, SpinZ, isPhysical: false, zeroOmegaResets: false);
        Run(spin, 1f); // a quarter turn about Z
        spin.ApplyUpdate(Quaternion.Identity, new Vector3(MathF.PI / 2f, 0f, 0f), isPhysical: false, zeroOmegaResets: false);
        Run(spin, 1f); // then a quarter turn about region X, after it

        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f) * AboutZ(MathF.PI / 2f);
        AssertRotation(expected, spin.Rotation);
    }

    /// <summary>Stopping a spinner: only an uncompressed full update with a zero omega drops what
    /// was turned (llviewerobject.cpp:1416). A compressed or terse one leaves the object where it stopped.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_zero_omega_resets_only_from_an_uncompressed_update(bool uncompressed)
    {
        var spin = new SpinComponent();
        spin.ApplyUpdate(Quaternion.Identity, SpinZ, isPhysical: false, zeroOmegaResets: false);
        Run(spin, 0.5f);
        var turned = spin.Rotation;

        spin.ApplyUpdate(Quaternion.Identity, Vector3.Zero, isPhysical: false, zeroOmegaResets: uncompressed);
        Assert.False(spin.IsTurning);
        Assert.Equal(uncompressed ? Quaternion.Identity : turned, spin.Rotation);
        Run(spin, 1f);
        Assert.Equal(uncompressed ? Quaternion.Identity : turned, spin.Rotation);
    }

    /// <summary>An object left spinning for an hour stays unit length and on the angle it should be at.</summary>
    [Fact]
    public void An_hour_of_frames_does_not_drift()
    {
        var spin = new SpinComponent();
        var omega = new Vector3(0.3f, -0.2f, 1.1f);
        spin.ApplyUpdate(Quaternion.Identity, omega, isPhysical: false, zeroOmegaResets: false);
        const int Frames = 60 * 3600;
        for (int i = 0; i < Frames; i++) spin.Advance(1f / 60f);

        Assert.InRange(spin.Rotation.Length(), 1f - Eps, 1f + Eps);
        double angle = omega.Length() * 3600.0 % (2.0 * Math.PI);
        var expected = Quaternion.CreateFromAxisAngle(Vector3.Normalize(omega), (float)angle);
        float dot = MathF.Abs(Quaternion.Dot(expected, spin.Rotation));
        Assert.True(dot > 0.999f, $"drifted: |dot|={dot}");
    }

    // ---- through WorldSimulation and the drawn transform ------------------------------------------

    private const ulong Region = 1ul;

    private static ObjectUpdateEvent Update(uint localId, Vector3 position, Quaternion rotation, uint parent = 0,
        Vector3 omega = default, bool isPhysical = false, bool resetsSpin = false, float timeDilation = 1f) =>
        new(Region, localId, position, rotation, Vector3.One, 1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One,
            ParentLocalId: parent, IsPhysical: isPhysical, TimeDilation: timeDilation,
            AngularVelocity: omega, ResetsSpin: resetsSpin);

    [Fact]
    public void An_object_that_never_turned_carries_no_spin_and_draws_where_it_is()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        session.RaiseObjectUpdate(Update(1, new Vector3(10, 20, 30), AboutZ(0.3f)));
        simulation.Pump();
        simulation.AdvanceSpins(1f);

        var entity = world.GetEntity(Region, 1)!;
        Assert.Null(entity.GetComponent<SpinComponent>());
        Assert.Empty(simulation.SpinDrivenEntities);
        Assert.False(TargetOmega.TryGetRenderTransform(world, entity, entity.GetComponent<TransformComponent>()!, out _, out _));
    }

    /// <summary>A turning root carries its children round its own centre, and both are on the
    /// per-frame list; the world model keeps the simulator's transforms.</summary>
    [Fact]
    public void A_turning_root_carries_its_children_round()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        session.RaiseObjectUpdate(Update(1, new Vector3(100, 100, 20), Quaternion.Identity, omega: SpinZ));
        session.RaiseObjectUpdate(Update(2, new Vector3(2, 0, 0), Quaternion.Identity, parent: 1));
        simulation.Pump();
        for (int i = 0; i < 60; i++) simulation.AdvanceSpins(1f / 60f);

        var root = world.GetEntity(Region, 1)!;
        var child = world.GetEntity(Region, 2)!;
        Assert.Contains(root, simulation.SpinDrivenEntities);
        Assert.Contains(child, simulation.SpinDrivenEntities);

        var childTransform = child.GetComponent<TransformComponent>()!;
        Assert.True(TargetOmega.TryGetRenderTransform(world, child, childTransform, out var pos, out var rot));
        AssertVector(new Vector3(100, 102, 20), pos);
        AssertRotation(AboutZ(MathF.PI / 2f), rot);
        AssertVector(new Vector3(102, 100, 20), childTransform.Position); // the world model is untouched
    }

    /// <summary>A turning child turns about its own centre, with its axis in the root's frame.</summary>
    [Fact]
    public void A_turning_child_turns_about_its_own_centre_in_the_roots_frame()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        var rootRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f); // root's Z is region -Y
        session.RaiseObjectUpdate(Update(1, new Vector3(100, 100, 20), rootRotation));
        session.RaiseObjectUpdate(Update(2, new Vector3(0, 0, 3), Quaternion.Identity, parent: 1, omega: SpinZ));
        simulation.Pump();
        for (int i = 0; i < 60; i++) simulation.AdvanceSpins(1f / 60f);

        var child = world.GetEntity(Region, 2)!;
        var childTransform = child.GetComponent<TransformComponent>()!;
        Assert.True(TargetOmega.TryGetRenderTransform(world, child, childTransform, out var pos, out var rot));
        AssertVector(childTransform.Position, pos); // spins in place
        AssertRotation(rootRotation * AboutZ(MathF.PI / 2f), rot);
    }

    [Fact]
    public void A_selected_object_stands_still_and_time_dilation_slows_the_turn()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        session.RaiseObjectUpdate(Update(1, new Vector3(10, 10, 10), Quaternion.Identity, omega: SpinZ, timeDilation: 0.5f));
        simulation.Pump();
        var entity = world.GetEntity(Region, 1)!;
        var spin = entity.GetComponent<SpinComponent>()!;

        world.SelectEntity(entity);
        simulation.AdvanceSpins(1f);
        Assert.Equal(Quaternion.Identity, spin.Rotation);

        world.DeselectEntity(entity);
        for (int i = 0; i < 60; i++) simulation.AdvanceSpins(1f / 60f);
        AssertRotation(AboutZ(MathF.PI / 4f), spin.Rotation); // half speed
    }

    /// <summary>The swing end to end: the same rotation with the omega's sign flipped keeps the
    /// turn; ResetsSpin (an uncompressed zero omega) drops it, and the object leaves the list.</summary>
    [Fact]
    public void Updates_through_the_simulation_follow_the_reset_rules()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        session.RaiseObjectUpdate(Update(1, new Vector3(10, 10, 10), Quaternion.Identity, omega: SpinZ));
        simulation.Pump();
        var entity = world.GetEntity(Region, 1)!;
        var spin = entity.GetComponent<SpinComponent>()!;
        for (int i = 0; i < 30; i++) simulation.AdvanceSpins(1f / 60f);

        session.RaiseObjectUpdate(Update(1, new Vector3(10, 10, 10), Quaternion.Identity, omega: -SpinZ));
        simulation.Pump();
        AssertRotation(AboutZ(MathF.PI / 4f), spin.Rotation);

        session.RaiseObjectUpdate(Update(1, new Vector3(10, 10, 10), Quaternion.Identity, resetsSpin: true));
        simulation.Pump();
        simulation.AdvanceSpins(1f / 60f);
        Assert.Equal(Quaternion.Identity, spin.Rotation);
        Assert.Empty(simulation.SpinDrivenEntities);
        // Nothing left turned: an ordinary object again.
        Assert.Null(entity.GetComponent<SpinComponent>());
    }

    /// <summary>Stopped by a compressed update (zero omega, no reset), the object keeps the turn and is
    /// drawn there, but is no longer moved every frame.</summary>
    [Fact]
    public void A_spinner_stopped_without_a_reset_stays_turned_and_off_the_per_frame_list()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        session.RaiseObjectUpdate(Update(1, new Vector3(10, 10, 10), Quaternion.Identity, omega: SpinZ));
        simulation.Pump();
        for (int i = 0; i < 30; i++) simulation.AdvanceSpins(1f / 60f);

        session.RaiseObjectUpdate(Update(1, new Vector3(10, 10, 10), Quaternion.Identity));
        simulation.Pump();
        simulation.AdvanceSpins(1f / 60f);

        var entity = world.GetEntity(Region, 1)!;
        Assert.Empty(simulation.SpinDrivenEntities);
        Assert.True(TargetOmega.TryGetRenderTransform(world, entity, entity.GetComponent<TransformComponent>()!, out _, out var rot));
        AssertRotation(AboutZ(MathF.PI / 4f), rot);
    }

    [Fact]
    public void A_removed_object_leaves_the_per_frame_list()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        session.RaiseObjectUpdate(Update(1, new Vector3(10, 10, 10), Quaternion.Identity, omega: SpinZ));
        simulation.Pump();
        simulation.AdvanceSpins(1f / 60f);
        Assert.Single(simulation.SpinDrivenEntities);

        world.RemoveEntity(Region, 1);
        simulation.AdvanceSpins(1f / 60f);
        Assert.Empty(simulation.SpinDrivenEntities);
    }

    /// <summary>A sitter is carried rigidly with the seat: a point on the seat's turning root a metre
    /// out goes round with it, and so does the sitter's orientation.</summary>
    [Fact]
    public void The_seat_turn_carries_a_sitter_round_the_seat()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        session.RaiseObjectUpdate(Update(1, new Vector3(50, 50, 25), Quaternion.Identity, omega: SpinZ));
        simulation.Pump();
        for (int i = 0; i < 60; i++) simulation.AdvanceSpins(1f / 60f);

        var seat = world.GetEntity(Region, 1)!;
        Assert.True(TargetOmega.TryGetSeatTurn(world, seat, seat.GetComponent<TransformComponent>()!,
            out var worldPosition, out var drawnPosition, out var turn));
        var sitter = new Vector3(51, 50, 26); // a metre out along X, a metre up
        var drawnSitter = drawnPosition + Vector3.Transform(sitter - worldPosition, turn);
        AssertVector(new Vector3(50, 51, 26), drawnSitter);
        AssertRotation(AboutZ(MathF.PI / 2f), turn);
    }
}
