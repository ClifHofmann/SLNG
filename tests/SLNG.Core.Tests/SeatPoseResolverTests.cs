using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-ANIM-03: a furniture pose outranks a worn AO HUD while seated.
///
/// <para>The rule has to be narrow, because everything it gets wrong it gets wrong by silently
/// removing an animation — and a missing animation reads as a frozen or T-posed avatar, which is
/// worse than the AO fight it replaces. These tests pin both halves: that it fires when a seat is
/// posing you, and that it keeps its hands off every other situation.</para>
/// </summary>
public class SeatPoseResolverTests
{
    private static readonly Guid Seat = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AoHud = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Collar = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly Guid SeatPose = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AoSit = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid BuiltInSit = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid CollarIdle = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");

    // The reported case: sitting on posing furniture while wearing an AO.
    [Fact]
    public void A_posing_seat_drops_the_ao_and_keeps_its_own_pose()
    {
        var ids = new List<Guid> { AoSit, SeatPose };
        var sources = new List<AnimationSignal>
        {
            new(AoSit, AoHud),
            new(SeatPose, Seat),
        };

        var kept = SeatPoseResolver.Resolve(ids, sources, Seat);

        Assert.Equal(new[] { SeatPose }, kept);
    }

    // An agent-sourced animation is not an object fighting for the bones -- a built-in SIT, a
    // gesture, an llSetAnimationOverride result. The blender's priorities handle those.
    [Fact]
    public void An_agent_sourced_animation_survives()
    {
        var ids = new List<Guid> { BuiltInSit, AoSit, SeatPose };
        var sources = new List<AnimationSignal>
        {
            new(BuiltInSit, Guid.Empty),
            new(AoSit, AoHud),
            new(SeatPose, Seat),
        };

        var kept = SeatPoseResolver.Resolve(ids, sources, Seat);

        Assert.Equal(new[] { BuiltInSit, SeatPose }, kept);
    }

    // v1 drops every non-seat object source, not just the AO. Documented as such -- while a seat is
    // posing you, a worn collar's idle is the same problem wearing a different hat.
    [Fact]
    public void Another_worn_animator_is_dropped_too()
    {
        var ids = new List<Guid> { CollarIdle, SeatPose };
        var sources = new List<AnimationSignal>
        {
            new(CollarIdle, Collar),
            new(SeatPose, Seat),
        };

        Assert.Equal(new[] { SeatPose }, SeatPoseResolver.Resolve(ids, sources, Seat));
    }

    // A plain chair that seats you without posing you. Whatever the AO plays is the only pose there
    // is, so taking it away would leave the avatar with nothing.
    [Fact]
    public void A_seat_that_does_not_pose_you_changes_nothing()
    {
        var ids = new List<Guid> { AoSit };
        var sources = new List<AnimationSignal> { new(AoSit, AoHud) };

        Assert.Same(ids, SeatPoseResolver.Resolve(ids, sources, Seat));
    }

    // Ground sit (llSitOnGround): seated, but on nothing. The AO's ground sit must still play.
    [Fact]
    public void A_ground_sit_changes_nothing()
    {
        var ids = new List<Guid> { AoSit };
        var sources = new List<AnimationSignal> { new(AoSit, AoHud) };

        Assert.Same(ids, SeatPoseResolver.Resolve(ids, sources, Guid.Empty));
    }

    // Standing, walking -- FEAT-ANIM-01's territory, which must stay untouched.
    [Fact]
    public void Not_seated_changes_nothing()
    {
        var ids = new List<Guid> { AoSit, CollarIdle };
        var sources = new List<AnimationSignal> { new(AoSit, AoHud), new(CollarIdle, Collar) };

        Assert.Same(ids, SeatPoseResolver.Resolve(ids, sources, Guid.Empty));
    }

    // A producer that carries no sources (an older test double, or a grid that sends none) must get
    // the previous behaviour rather than an empty avatar.
    [Fact]
    public void Missing_sources_disable_the_rule_entirely()
    {
        var ids = new List<Guid> { AoSit, SeatPose };

        Assert.Same(ids, SeatPoseResolver.Resolve(ids, null, Seat));
        Assert.Same(ids, SeatPoseResolver.Resolve(ids, new List<AnimationSignal>(), Seat));
    }

    // An id the simulator listed but did not classify must not be swept up. Dropping what cannot be
    // identified would make the rule subtract animations it was never meant to touch.
    [Fact]
    public void An_unclassified_animation_is_kept()
    {
        var mystery = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000f");
        var ids = new List<Guid> { mystery, AoSit, SeatPose };
        var sources = new List<AnimationSignal>
        {
            new(AoSit, AoHud),
            new(SeatPose, Seat),
        };

        Assert.Equal(new[] { mystery, SeatPose }, SeatPoseResolver.Resolve(ids, sources, Seat));
    }

    // Guard, not a path: the seat's own animation always survives, so this cannot normally happen.
    // If it ever did, an empty set would be a T-pose — worse than the fight being fixed.
    [Fact]
    public void The_rule_never_returns_an_empty_set()
    {
        var ids = new List<Guid> { AoSit };
        var sources = new List<AnimationSignal>
        {
            new(AoSit, AoHud),
            // The seat sources something that is not in the playing list -- contrived, but it is
            // what makes seatIsPosing true with nothing of the seat's left to keep.
            new(SeatPose, Seat),
        };

        Assert.Same(ids, SeatPoseResolver.Resolve(ids, sources, Seat));
    }

    // Simulator order is the blender's tie-break at equal priority, so the filter must not reorder.
    [Fact]
    public void Surviving_animations_keep_the_simulators_order()
    {
        var first = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1");
        var second = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a2");
        var ids = new List<Guid> { first, AoSit, second };
        var sources = new List<AnimationSignal>
        {
            new(first, Seat),
            new(AoSit, AoHud),
            new(second, Seat),
        };

        Assert.Equal(new[] { first, second }, SeatPoseResolver.Resolve(ids, sources, Seat));
    }
}
