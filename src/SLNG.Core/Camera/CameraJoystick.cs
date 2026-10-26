using System;
using System.Numerics;

namespace SLNG.Core.Camera;

/// <summary>
/// The press-and-hold maths of the camera joysticks (the orbit trackball and the pan pad), kept free of the
/// engine so it can be tested (FEAT-UI-74). It is <c>LLJoystickCameraRotate</c> / <c>LLJoystickCameraTrack</c> of
/// the Linden viewer (<c>indra/newview/lljoystickbutton.cpp</c>, :55-58 constants, :432-498 press, :517-567 held):
///
/// <para>A press anywhere on the control picks a quadrant, split on the diagonals, and starts with an offset of
/// <see cref="InitialOffset"/> towards it, so the press alone already counts as "that way". While held, the mouse
/// travel since the press is added to that offset; an axis is active once the result passes the slop. Dragging
/// while held therefore reverses an axis or adds a second one, which is how a press becomes a diagonal. The rate
/// starts at <see cref="NudgeRate"/> and climbs to full over <see cref="NudgeTime"/>, so a tap nudges and a hold
/// moves.</para>
///
/// <para>Everything here is in the viewer's orientation, <b>y up</b>, in pixels at a control of the reference size
/// (<see cref="ReferenceSize"/>, 80 px); <c>scale</c> stretches the pixel constants for a control drawn larger or
/// smaller. A caller whose own y points down negates it before and after.</para>
/// </summary>
public static class CameraJoystick
{
    /// <summary>Seconds the rate takes to climb from <see cref="NudgeRate"/> to full (<c>NUDGE_TIME</c>, :55).</summary>
    public const float NudgeTime = 0.25f;

    /// <summary>The rate at the instant of the press, as a fraction of full (<c>ORBIT_NUDGE_RATE</c>, :56).</summary>
    public const float NudgeRate = 0.05f;

    /// <summary>An axis is active once the effective offset is farther than this from the middle
    /// (<c>mVertSlopNear</c>, :437).</summary>
    public const float SlopNear = 16f;

    /// <summary>The far edge of the slop zone (<c>mVertSlopFar</c>, :438). A press starts halfway between near and far.</summary>
    public const float SlopFar = 32f;

    /// <summary>Radius of the centre dot, within which a press resets instead of moving (<c>CENTER_DOT_RADIUS</c>, :58).</summary>
    public const float CenterDotRadius = 7f;

    /// <summary>The control size, in pixels, that the constants above are written for: the viewer's own joysticks are
    /// about 78-80 px, and its slop (16/32) and centre dot (7) are tuned to that. The Camera window opens its pads at
    /// this size, so they feel as the viewer's do.</summary>
    public const float ReferenceSize = 80f;

    /// <summary>
    /// The quadrant of a press at (<paramref name="x"/>, <paramref name="yUp"/>) from the centre of the control:
    /// <see cref="JoystickQuadrant.Origin"/> inside the centre dot, otherwise the wedge between the diagonals that
    /// the point is in. The comparisons are the viewer's, so a point exactly on a diagonal falls the same way
    /// there: <c>dy &gt; dx &amp;&amp; dy &gt; -dx</c> is up, <c>dy &gt; dx</c> alone is left, <c>dy &lt;= dx &amp;&amp;
    /// dy &lt;= -dx</c> is down, and the rest is right.
    /// </summary>
    public static JoystickQuadrant Quadrant(float x, float yUp, float scale = 1f)
    {
        float dot = CenterDotRadius * scale;
        if (x * x + yUp * yUp <= dot * dot) return JoystickQuadrant.Origin;
        if (yUp > x && yUp > -x) return JoystickQuadrant.Up;
        if (yUp > x) return JoystickQuadrant.Left;
        if (yUp <= x && yUp <= -x) return JoystickQuadrant.Down;
        return JoystickQuadrant.Right;
    }

    /// <summary>The offset a press starts with, towards its quadrant: halfway between the near and far slop
    /// (24 px at the reference size), zero for the centre.</summary>
    public static Vector2 InitialOffset(JoystickQuadrant quadrant, float scale = 1f)
    {
        float d = (SlopNear + SlopFar) / 2f * scale;
        return quadrant switch
        {
            JoystickQuadrant.Up => new Vector2(0f, d),
            JoystickQuadrant.Down => new Vector2(0f, -d),
            JoystickQuadrant.Left => new Vector2(-d, 0f),
            JoystickQuadrant.Right => new Vector2(d, 0f),
            _ => Vector2.Zero,
        };
    }

    /// <summary>
    /// Which way each axis is pushed while the button is held: -1, 0 or +1 for X (right is +) and for Y (up is +).
    /// <paramref name="drag"/> is the mouse position now minus the position at the press, y up; the press's own
    /// <see cref="InitialOffset"/> is added here. An axis is active when its effective value is strictly beyond
    /// <see cref="SlopNear"/> (so exactly 16 px is still idle, as in the viewer's <c>dx &gt; slop</c>).
    /// </summary>
    public static (int X, int Y) Axes(Vector2 drag, JoystickQuadrant quadrant, float scale = 1f)
    {
        var effective = drag + InitialOffset(quadrant, scale);
        float slop = SlopNear * scale;
        return (Beyond(effective.X, slop), Beyond(effective.Y, slop));
    }

    private static int Beyond(float value, float slop) => value > slop ? 1 : value < -slop ? -1 : 0;

    /// <summary>The fraction of full speed after the button has been held <paramref name="heldSeconds"/>:
    /// 0.05 at the press, rising linearly to 1 at 0.25 s and staying there (<c>getOrbitRate</c>, :554-567).</summary>
    public static float Ramp(float heldSeconds)
    {
        float t = heldSeconds > 0f ? heldSeconds : 0f; // negative and NaN count as "just pressed"
        return t < NudgeTime ? NudgeRate + t * ((1f - NudgeRate) / NudgeTime) : 1f;
    }
}
