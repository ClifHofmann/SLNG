using SLNG.Core.Avatars;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-ANIM-01: pins the local state → locomotion-anim decision the self avatar's
/// zero-latency animation prediction depends on.</summary>
public class SelfLocomotionTests
{
    private static LocomotionState State(
        bool sitting = false, bool flying = false, bool grounded = true,
        bool fwd = false, bool left = false, bool right = false, bool crouch = false,
        float speedH = 0f, float speedV = 0f)
        => new(sitting, flying, grounded, fwd, left, right, crouch, speedH, speedV);

    [Fact]
    public void Idle_on_the_ground_is_Stand()
        => Assert.Equal(SelfLocomotion.Stand, SelfLocomotion.Predict(State()));

    [Fact]
    public void Sitting_predicts_nothing()
        => Assert.Null(SelfLocomotion.Predict(State(sitting: true, fwd: true)));

    [Fact]
    public void Forward_key_walks_immediately_even_with_zero_reported_speed()
        => Assert.Equal(SelfLocomotion.Walk, SelfLocomotion.Predict(State(fwd: true, speedH: 0f)));

    [Fact]
    public void Fast_horizontal_speed_is_Run()
        => Assert.Equal(SelfLocomotion.Run, SelfLocomotion.Predict(State(fwd: true, speedH: 5.5f)));

    [Fact]
    public void Below_run_threshold_is_Walk()
        => Assert.Equal(SelfLocomotion.Walk, SelfLocomotion.Predict(State(fwd: true, speedH: 3.2f)));

    [Fact]
    public void Keyless_drift_above_the_move_threshold_still_walks()
        => Assert.Equal(SelfLocomotion.Walk, SelfLocomotion.Predict(State(speedH: 1.0f)));

    [Fact]
    public void Turn_left_key_with_no_forward_motion_is_TurnLeft()
        => Assert.Equal(SelfLocomotion.TurnLeft, SelfLocomotion.Predict(State(left: true)));

    [Fact]
    public void Turn_right_key_with_no_forward_motion_is_TurnRight()
        => Assert.Equal(SelfLocomotion.TurnRight, SelfLocomotion.Predict(State(right: true)));

    [Fact]
    public void Walking_while_also_holding_a_turn_key_still_walks()
        => Assert.Equal(SelfLocomotion.Walk, SelfLocomotion.Predict(State(fwd: true, left: true)));

    [Fact]
    public void Flying_still_is_Hover()
        => Assert.Equal(SelfLocomotion.Hover, SelfLocomotion.Predict(State(flying: true, grounded: false)));

    [Fact]
    public void Flying_forward_is_Fly()
        => Assert.Equal(SelfLocomotion.Fly, SelfLocomotion.Predict(State(flying: true, grounded: false, fwd: true)));

    [Fact]
    public void Flying_upward_is_HoverUp()
        => Assert.Equal(SelfLocomotion.HoverUp, SelfLocomotion.Predict(State(flying: true, grounded: false, speedV: 3f)));

    [Fact]
    public void Flying_downward_is_HoverDown()
        => Assert.Equal(SelfLocomotion.HoverDown, SelfLocomotion.Predict(State(flying: true, grounded: false, speedV: -3f)));

    [Fact]
    public void Airborne_and_not_flying_is_FallDown()
        => Assert.Equal(SelfLocomotion.FallDown, SelfLocomotion.Predict(State(grounded: false)));

    [Fact]
    public void Crouch_key_on_the_ground_is_Crouch()
        => Assert.Equal(SelfLocomotion.Crouch, SelfLocomotion.Predict(State(crouch: true)));

    [Fact]
    public void Crouch_key_while_moving_is_CrouchWalk()
        => Assert.Equal(SelfLocomotion.CrouchWalk, SelfLocomotion.Predict(State(crouch: true, fwd: true)));

    [Theory]
    [InlineData("Walk", true)]
    [InlineData("Run", true)]
    [InlineData("TurnLeft", true)]
    [InlineData("TurnRight", true)]
    [InlineData("CrouchWalk", true)]
    [InlineData("Fly", true)]
    [InlineData("HoverUp", true)]
    [InlineData("HoverDown", true)]
    [InlineData("Stand", false)]
    [InlineData("Hover", false)]
    [InlineData("Crouch", false)]
    [InlineData("FallDown", false)]
    public void IsMoving_matches_the_moving_gaits(string name, bool expected)
    {
        var id = (System.Guid)typeof(SelfLocomotion).GetField(name)!.GetValue(null)!;
        Assert.Equal(expected, SelfLocomotion.IsMoving(id));
    }

    [Fact]
    public void Every_predicted_id_is_in_the_filter_set()
    {
        // The renderer strips SelfLocomotion.All out of the sim's echo before substituting the
        // prediction -- so anything Predict can emit MUST be in All, or the two would double up.
        foreach (var s in new[]
        {
            State(), State(fwd: true), State(fwd: true, speedH: 6f), State(left: true), State(right: true),
            State(flying: true, grounded: false), State(flying: true, grounded: false, fwd: true),
            State(flying: true, grounded: false, speedV: 3f), State(flying: true, grounded: false, speedV: -3f),
            State(grounded: false), State(crouch: true), State(crouch: true, fwd: true),
        })
        {
            var id = SelfLocomotion.Predict(s);
            Assert.True(id == null || SelfLocomotion.All.Contains(id.Value), $"{id} not in All");
        }
    }
}
