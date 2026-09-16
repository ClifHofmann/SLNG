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
        float speedH = 0f, float speedV = 0f, bool running = false)
        => new(sitting, flying, grounded, fwd, left, right, crouch, speedH, speedV, running);

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
    public void Running_flag_predicts_Run_immediately_even_with_zero_reported_speed()
    {
        Assert.Equal(SelfLocomotion.Run, SelfLocomotion.Predict(State(fwd: true, running: true), isMale: true));
        Assert.Equal(SelfLocomotion.FemaleRun, SelfLocomotion.Predict(State(fwd: true, running: true), isMale: false));
    }

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

    [Fact]
    public void Female_avatar_predicts_female_walk_and_female_run()
    {
        Assert.Equal(SelfLocomotion.FemaleWalk, SelfLocomotion.Predict(State(fwd: true), isMale: false));
        Assert.Equal(SelfLocomotion.FemaleRun, SelfLocomotion.Predict(State(fwd: true, speedH: 5.5f), isMale: false));
    }

    [Fact]
    public void Male_avatar_predicts_neutral_walk_and_run()
    {
        Assert.Equal(SelfLocomotion.Walk, SelfLocomotion.Predict(State(fwd: true), isMale: true));
        Assert.Equal(SelfLocomotion.Run, SelfLocomotion.Predict(State(fwd: true, speedH: 5.5f), isMale: true));
    }

    [Fact]
    public void RemapForSex_maps_correctly_both_directions()
    {
        // Female
        Assert.Equal(SelfLocomotion.FemaleWalk, SelfLocomotion.RemapForSex(SelfLocomotion.Walk, isMale: false));
        Assert.Equal(SelfLocomotion.FemaleWalkNew, SelfLocomotion.RemapForSex(SelfLocomotion.WalkNew, isMale: false));
        Assert.Equal(SelfLocomotion.FemaleRun, SelfLocomotion.RemapForSex(SelfLocomotion.Run, isMale: false));
        Assert.Equal(SelfLocomotion.FemaleRun, SelfLocomotion.RemapForSex(SelfLocomotion.RunNew, isMale: false));
        Assert.Equal(SelfLocomotion.SitFemale, SelfLocomotion.RemapForSex(SelfLocomotion.Sit, isMale: false));

        // Male
        Assert.Equal(SelfLocomotion.Walk, SelfLocomotion.RemapForSex(SelfLocomotion.FemaleWalk, isMale: true));
        Assert.Equal(SelfLocomotion.WalkNew, SelfLocomotion.RemapForSex(SelfLocomotion.FemaleWalkNew, isMale: true));
        Assert.Equal(SelfLocomotion.Run, SelfLocomotion.RemapForSex(SelfLocomotion.FemaleRun, isMale: true));
        Assert.Equal(SelfLocomotion.Sit, SelfLocomotion.RemapForSex(SelfLocomotion.SitFemale, isMale: true));

        // Unrelated/custom
        var custom = System.Guid.NewGuid();
        Assert.Equal(custom, SelfLocomotion.RemapForSex(custom, isMale: false));
        Assert.Equal(custom, SelfLocomotion.RemapForSex(custom, isMale: true));
    }

    [Fact]
    public void Neutral_fallback_resolves_female_to_neutral()
    {
        Assert.Equal(SelfLocomotion.Walk, SelfLocomotion.GetNeutralFallback(SelfLocomotion.FemaleWalk));
        Assert.Equal(SelfLocomotion.Walk, SelfLocomotion.GetNeutralFallback(SelfLocomotion.FemaleWalkNew));
        Assert.Equal(SelfLocomotion.Run, SelfLocomotion.GetNeutralFallback(SelfLocomotion.FemaleRun));
        Assert.Equal(SelfLocomotion.Sit, SelfLocomotion.GetNeutralFallback(SelfLocomotion.SitFemale));
        Assert.Equal(SelfLocomotion.Walk, SelfLocomotion.GetNeutralFallback(SelfLocomotion.Walk));
    }

    [Theory]
    [InlineData("Walk", true)]
    [InlineData("Run", true)]
    [InlineData("FemaleWalk", true)]
    [InlineData("FemaleWalkNew", true)]
    [InlineData("FemaleRun", true)]
    [InlineData("WalkNew", true)]
    [InlineData("RunNew", true)]
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
            var idMale = SelfLocomotion.Predict(s, isMale: true);
            Assert.True(idMale == null || SelfLocomotion.All.Contains(idMale.Value), $"{idMale} not in All");

            var idFemale = SelfLocomotion.Predict(s, isMale: false);
            Assert.True(idFemale == null || SelfLocomotion.All.Contains(idFemale.Value), $"{idFemale} not in All");
        }
    }
}
