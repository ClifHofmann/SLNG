using System.Numerics;
using SLNG.Core.Camera;
using Xunit;

namespace SLNG.Core.Tests.Camera;

public class CameraJoystickTests
{
    [Fact]
    public void Constants_are_the_viewers()
    {
        // lljoystickbutton.cpp:55-58 and :437-438.
        Assert.Equal(0.25f, CameraJoystick.NudgeTime);
        Assert.Equal(0.05f, CameraJoystick.NudgeRate);
        Assert.Equal(16f, CameraJoystick.SlopNear);
        Assert.Equal(32f, CameraJoystick.SlopFar);
        Assert.Equal(7f, CameraJoystick.CenterDotRadius);
    }

    // ---- quadrant of the press (x right, y UP, from the centre) ----

    [Theory]
    [InlineData(0f, 40f, JoystickQuadrant.Up)]
    [InlineData(0f, -40f, JoystickQuadrant.Down)]
    [InlineData(-40f, 0f, JoystickQuadrant.Left)]
    [InlineData(40f, 0f, JoystickQuadrant.Right)]
    public void A_press_on_each_side_picks_that_side(float x, float yUp, JoystickQuadrant expected)
        => Assert.Equal(expected, CameraJoystick.Quadrant(x, yUp));

    [Theory]
    [InlineData(10f, 30f, JoystickQuadrant.Up)]
    [InlineData(-10f, 30f, JoystickQuadrant.Up)]
    [InlineData(30f, 10f, JoystickQuadrant.Right)]
    [InlineData(30f, -10f, JoystickQuadrant.Right)]
    [InlineData(-30f, 10f, JoystickQuadrant.Left)]
    [InlineData(-30f, -10f, JoystickQuadrant.Left)]
    [InlineData(10f, -30f, JoystickQuadrant.Down)]
    [InlineData(-10f, -30f, JoystickQuadrant.Down)]
    public void The_wedges_are_split_on_the_diagonals(float x, float yUp, JoystickQuadrant expected)
        => Assert.Equal(expected, CameraJoystick.Quadrant(x, yUp));

    [Theory]
    // Exactly on a diagonal the viewer's comparisons decide (lljoystickbutton.cpp:468-495): top-right goes right,
    // top-left goes left, both bottom diagonals go down.
    [InlineData(30f, 30f, JoystickQuadrant.Right)]
    [InlineData(-30f, 30f, JoystickQuadrant.Left)]
    [InlineData(-30f, -30f, JoystickQuadrant.Down)]
    [InlineData(30f, -30f, JoystickQuadrant.Down)]
    public void A_press_exactly_on_a_diagonal_falls_the_way_the_viewer_does(float x, float yUp, JoystickQuadrant expected)
        => Assert.Equal(expected, CameraJoystick.Quadrant(x, yUp));

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(5f, 4f)] // 6.4 px from the centre
    [InlineData(-7f, 0f)] // exactly the radius is still inside (the viewer's <=)
    [InlineData(0f, 7f)]
    public void A_press_inside_the_centre_dot_is_the_origin(float x, float yUp)
        => Assert.Equal(JoystickQuadrant.Origin, CameraJoystick.Quadrant(x, yUp));

    [Fact]
    public void A_press_just_outside_the_centre_dot_is_a_side()
    {
        Assert.Equal(JoystickQuadrant.Right, CameraJoystick.Quadrant(7.1f, 0f));
        Assert.Equal(JoystickQuadrant.Up, CameraJoystick.Quadrant(0f, 7.1f));
        Assert.Equal(JoystickQuadrant.Right, CameraJoystick.Quadrant(5f, 5f)); // 7.07 px, on the diagonal
    }

    [Fact]
    public void The_centre_dot_grows_with_the_control()
    {
        Assert.Equal(JoystickQuadrant.Origin, CameraJoystick.Quadrant(13f, 0f, scale: 2f));
        Assert.Equal(JoystickQuadrant.Right, CameraJoystick.Quadrant(15f, 0f, scale: 2f));
    }

    // ---- the offset a press starts with ----

    [Fact]
    public void A_press_starts_24_px_towards_its_side_and_the_centre_starts_at_zero()
    {
        Assert.Equal(new Vector2(0f, 24f), CameraJoystick.InitialOffset(JoystickQuadrant.Up));
        Assert.Equal(new Vector2(0f, -24f), CameraJoystick.InitialOffset(JoystickQuadrant.Down));
        Assert.Equal(new Vector2(-24f, 0f), CameraJoystick.InitialOffset(JoystickQuadrant.Left));
        Assert.Equal(new Vector2(24f, 0f), CameraJoystick.InitialOffset(JoystickQuadrant.Right));
        Assert.Equal(Vector2.Zero, CameraJoystick.InitialOffset(JoystickQuadrant.Origin));
    }

    [Fact]
    public void The_start_offset_scales_with_the_control()
        => Assert.Equal(new Vector2(0f, 48f), CameraJoystick.InitialOffset(JoystickQuadrant.Up, scale: 2f));

    // ---- axes while held ----

    [Theory]
    [InlineData(JoystickQuadrant.Up, 0, 1)]
    [InlineData(JoystickQuadrant.Down, 0, -1)]
    [InlineData(JoystickQuadrant.Left, -1, 0)]
    [InlineData(JoystickQuadrant.Right, 1, 0)]
    [InlineData(JoystickQuadrant.Origin, 0, 0)]
    public void A_press_without_moving_pushes_towards_its_side(JoystickQuadrant quadrant, int x, int y)
        => Assert.Equal((x, y), CameraJoystick.Axes(Vector2.Zero, quadrant));

    [Fact]
    public void Dragging_sideways_while_held_adds_a_second_axis_for_a_diagonal()
    {
        // Press up, then drag right: 17 px of travel is past the 16 px slop on X.
        Assert.Equal((1, 1), CameraJoystick.Axes(new Vector2(17f, 0f), JoystickQuadrant.Up));
        Assert.Equal((-1, 1), CameraJoystick.Axes(new Vector2(-17f, 0f), JoystickQuadrant.Up));
        Assert.Equal((1, -1), CameraJoystick.Axes(new Vector2(17f, 0f), JoystickQuadrant.Down));
    }

    [Fact]
    public void Dragging_back_past_the_start_offset_reverses_an_axis()
    {
        // Press right (24 px of start offset): 8 px back is idle, 41 px back is a full reversal.
        Assert.Equal((1, 0), CameraJoystick.Axes(new Vector2(-7f, 0f), JoystickQuadrant.Right));
        Assert.Equal((0, 0), CameraJoystick.Axes(new Vector2(-9f, 0f), JoystickQuadrant.Right));
        Assert.Equal((0, 0), CameraJoystick.Axes(new Vector2(-39f, 0f), JoystickQuadrant.Right));
        Assert.Equal((-1, 0), CameraJoystick.Axes(new Vector2(-41f, 0f), JoystickQuadrant.Right));
        // Press up, drag down: same on Y.
        Assert.Equal((0, -1), CameraJoystick.Axes(new Vector2(0f, -41f), JoystickQuadrant.Up));
    }

    [Fact]
    public void An_axis_needs_strictly_more_than_the_slop()
    {
        // Press up: effective Y = 24 + drag. Exactly 16 is still idle, a hair over is active.
        Assert.Equal((0, 0), CameraJoystick.Axes(new Vector2(0f, -8f), JoystickQuadrant.Up));
        Assert.Equal((0, 1), CameraJoystick.Axes(new Vector2(0f, -7.99f), JoystickQuadrant.Up));
        // And on the negative side, from the centre.
        Assert.Equal((0, 0), CameraJoystick.Axes(new Vector2(-16f, 0f), JoystickQuadrant.Origin));
        Assert.Equal((-1, 0), CameraJoystick.Axes(new Vector2(-16.01f, 0f), JoystickQuadrant.Origin));
        Assert.Equal((0, 0), CameraJoystick.Axes(new Vector2(16f, 16f), JoystickQuadrant.Origin));
        Assert.Equal((1, 1), CameraJoystick.Axes(new Vector2(16.01f, 16.01f), JoystickQuadrant.Origin));
    }

    [Fact]
    public void The_slop_scales_with_the_control()
    {
        // At scale 2 the slop is 32 px and the start offset 48 px: dragging back 16 px leaves exactly 32, idle.
        Assert.Equal((0, 1), CameraJoystick.Axes(new Vector2(0f, -15f), JoystickQuadrant.Up, scale: 2f));
        Assert.Equal((0, 0), CameraJoystick.Axes(new Vector2(0f, -16f), JoystickQuadrant.Up, scale: 2f));
        // The same drag at scale 1 would have reversed the axis long since.
        Assert.Equal((0, -1), CameraJoystick.Axes(new Vector2(0f, -41f), JoystickQuadrant.Up));
    }

    // ---- the ramp ----

    [Fact]
    public void A_tap_nudges_at_five_percent()
        => Assert.Equal(0.05f, CameraJoystick.Ramp(0f), 5);

    [Fact]
    public void The_rate_climbs_linearly_over_a_quarter_second()
    {
        Assert.Equal(0.525f, CameraJoystick.Ramp(0.125f), 5); // 0.05 + 0.125 * 3.8
        Assert.Equal(0.3f, CameraJoystick.Ramp(0.0658f), 3);
        Assert.True(CameraJoystick.Ramp(0.1f) < CameraJoystick.Ramp(0.2f));
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.3f)]
    [InlineData(5f)]
    public void After_a_quarter_second_the_rate_is_full(float held)
        => Assert.Equal(1f, CameraJoystick.Ramp(held));

    [Fact]
    public void Just_below_a_quarter_second_the_rate_is_nearly_full()
        => Assert.InRange(CameraJoystick.Ramp(0.2499f), 0.999f, 1f);

    [Fact]
    public void A_negative_or_undefined_hold_time_reads_as_a_fresh_press()
    {
        Assert.Equal(0.05f, CameraJoystick.Ramp(-1f), 5);
        Assert.Equal(0.05f, CameraJoystick.Ramp(float.NaN), 5);
    }
}
