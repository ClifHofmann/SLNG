namespace SLNG.Core.Camera;

/// <summary>
/// Where on a camera joystick (the orbit trackball, the pan pad) a press landed
/// (<c>EJoystickQuadrant</c>, lljoystickbutton.h; FEAT-UI-74).
/// </summary>
public enum JoystickQuadrant
{
    /// <summary>The centre dot: a reset, no movement.</summary>
    Origin,
    Up,
    Left,
    Down,
    Right,
}
