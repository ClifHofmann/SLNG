using System;
using Godot;
using SLNG.Core.Camera;
using CoreJoystick = SLNG.Core.Camera.CameraJoystick;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-74: the press-and-hold pad of the Camera Controls window - a round trackball for orbiting or a square
/// pad for panning, the viewer's <c>LLJoystickCameraRotate</c> / <c>LLJoystickCameraTrack</c>. The maths is
/// <see cref="CoreJoystick"/>; this is the drawing and the mouse.
///
/// <para>A press picks a quadrant and the pad keeps following the mouse until the button comes up, wherever the
/// pointer goes (Godot keeps sending a pressed control its motion and release; <see cref="_Process"/> also polls the
/// button, so a release it never saw - a hidden window, lost focus - cannot leave the camera turning). The window
/// reads <see cref="Rate"/> every frame and moves the camera by it times the seconds the frame took, so the speed
/// never depends on the frame rate. A press on the centre dot raises <see cref="ResetRequested"/> and moves nothing
/// by itself.</para>
///
/// <para>All distances are in the control's own pixels and scale with its size against the 100 px reference
/// (<see cref="CoreJoystick.ReferenceSize"/>), so a pad drawn larger keeps the same feel.</para>
/// </summary>
public partial class CameraJoystick : Control
{
    private const float CornerRadius = 10f; // of the square pad, at the reference size
    private const float EdgeInset = 1.5f; // highlights stay this far inside the border
    private const int WedgeSegments = 12;

    private static readonly Color BodyColor = new(0.15f, 0.15f, 0.16f, 0.9f);
    private static readonly Color BorderColor = new(1f, 1f, 1f, 0.1f);
    private static readonly Color ChevronColor = new(0.85f, 0.85f, 0.85f, 0.8f);
    private static readonly Color DotColor = new(0.62f, 0.67f, 0.74f, 0.9f);
    private static readonly Color HoverFill = new(1f, 1f, 1f, 0.12f);
    private static readonly Color HeldFill = new(UiTheme.Accent, 0.5f);

    // The square pad's body. The round one is drawn as a circle instead: a StyleBoxFlat with a radius that large
    // and a translucent fill shows its corner seams as hairlines through the middle.
    private readonly StyleBoxFlat _body = new()
    {
        BgColor = BodyColor,
        BorderColor = BorderColor,
        BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
    };

    private bool _held;
    private ulong _heldSinceMsec;
    private Vector2 _firstMouse;
    private JoystickQuadrant _pressQuadrant = JoystickQuadrant.Origin;
    private (int X, int Y) _axes; // -1/0/+1; X right is +, Y UP is +
    private JoystickQuadrant? _hover;

    /// <summary>True for the round trackball, false for the square pad.</summary>
    public bool Round { get; set; }

    /// <summary>The centre dot was pressed: put back what this pad moves (the orbit, or the pan).</summary>
    public event Action? ResetRequested;

    /// <summary>
    /// Where to push now, for the window to read each frame: X is -1..1 with right positive, Y is -1..1 with
    /// <b>up</b> positive, each already times the ramp that starts a press at 5 % and reaches full over a quarter
    /// second. Zero while nothing is held. The window turns it into radians or metres per second.
    /// </summary>
    public Vector2 Rate
    {
        get
        {
            if (!_held || _axes == (0, 0)) return Vector2.Zero;
            float ramp = CoreJoystick.Ramp((Time.GetTicksMsec() - _heldSinceMsec) / 1000f);
            return new Vector2(_axes.X, _axes.Y) * ramp;
        }
    }

    public CameraJoystick()
    {
        CustomMinimumSize = new Vector2(CoreJoystick.ReferenceSize, CoreJoystick.ReferenceSize);
        MouseDefaultCursorShape = CursorShape.PointingHand;
    }

    public override void _Ready()
    {
        SetProcess(false); // only while a press is being followed
        MouseExited += OnMouseExited;
    }

    /// <summary>The pad's own size against the reference one; every pixel constant is multiplied by it.</summary>
    private float SizeFactor => Mathf.Max(Mathf.Min(Size.X, Size.Y), 1f) / CoreJoystick.ReferenceSize;

    /// <summary>A round pad takes presses only inside the circle, so its square corners stay click-through.</summary>
    public override bool _HasPoint(Vector2 point)
    {
        if (!Round) return new Rect2(Vector2.Zero, Size).HasPoint(point);
        float radius = Mathf.Min(Size.X, Size.Y) / 2f;
        return point.DistanceSquaredTo(Size / 2f) <= radius * radius;
    }

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                if (button.Pressed) Press(button.Position);
                else if (_held) Release(button.Position);
                AcceptEvent();
                break;
            case InputEventMouseMotion motion:
                if (_held) Follow(motion.Position);
                else SetHover(QuadrantAt(motion.Position));
                break;
        }
    }

    public override void _Process(double delta)
    {
        // Safety net only: a release the control never saw (a window closed under the pointer, focus lost) must
        // not leave the camera turning. The position comes from the motion events in _GuiInput.
        if (_held && Input.IsMouseButtonPressed(MouseButton.Left) && IsVisibleInTree()) return;
        if (_held) Release(null);
        SetProcess(false);
    }

    private JoystickQuadrant QuadrantAt(Vector2 local)
    {
        var fromCentre = local - Size / 2f;
        return CoreJoystick.Quadrant(fromCentre.X, -fromCentre.Y, SizeFactor); // the maths is y up, Godot's is y down
    }

    private void Press(Vector2 local)
    {
        _pressQuadrant = QuadrantAt(local);
        _firstMouse = local;
        _heldSinceMsec = Time.GetTicksMsec();
        _held = true;
        _hover = null;
        SetProcess(true);
        QueueRedraw(); // the centre dot lights up even though no axis moved
        if (_pressQuadrant == JoystickQuadrant.Origin) ResetRequested?.Invoke();
        Follow(local);
    }

    private void Follow(Vector2 local)
    {
        var drag = local - _firstMouse;
        var axes = CoreJoystick.Axes(new System.Numerics.Vector2(drag.X, -drag.Y), _pressQuadrant, SizeFactor);
        if (axes == _axes) return;
        _axes = axes;
        QueueRedraw();
    }

    /// <summary>Ends the hold. <paramref name="local"/> is where the button came up, or null when it was not seen.</summary>
    private void Release(Vector2? local)
    {
        _held = false;
        _axes = (0, 0);
        _hover = local is { } at && _HasPoint(at) ? QuadrantAt(at) : null; // released outside: nothing is under the pointer
        QueueRedraw();
    }

    private void SetHover(JoystickQuadrant? quadrant)
    {
        if (_hover == quadrant) return;
        _hover = quadrant;
        QueueRedraw();
    }

    private void OnMouseExited()
    {
        if (!_held) SetHover(null);
    }

    public override void _Draw()
    {
        var size = Size;
        float s = SizeFactor;
        var centre = size / 2f;
        float half = Mathf.Min(size.X, size.Y) / 2f;

        if (Round)
        {
            DrawCircle(centre, half, BodyColor, true, -1f, true);
            DrawArc(centre, half - 0.5f, 0f, Mathf.Tau, 64, BorderColor, 1f, true);
        }
        else
        {
            _body.SetCornerRadiusAll(Mathf.RoundToInt(CornerRadius * s));
            DrawStyleBox(_body, new Rect2(Vector2.Zero, size));
        }

        // The wedges that are pushed (held) or that the pointer is over (hover), under the arrows.
        bool up = false, down = false, left = false, right = false;
        Color fill = HoverFill;
        if (_held)
        {
            fill = HeldFill;
            up = _axes.Y > 0; down = _axes.Y < 0; right = _axes.X > 0; left = _axes.X < 0;
        }
        else if (_hover is { } hovered)
        {
            up = hovered == JoystickQuadrant.Up; down = hovered == JoystickQuadrant.Down;
            left = hovered == JoystickQuadrant.Left; right = hovered == JoystickQuadrant.Right;
        }

        float reach = half - EdgeInset;
        float corner = CornerRadius * s - EdgeInset;
        if (up) DrawWedge(centre, -Mathf.Pi / 2f, reach, corner, fill);
        if (right) DrawWedge(centre, 0f, reach, corner, fill);
        if (down) DrawWedge(centre, Mathf.Pi / 2f, reach, corner, fill);
        if (left) DrawWedge(centre, Mathf.Pi, reach, corner, fill);

        // Four chevrons near the edge, brighter while their side is pushed.
        float chevronAt = half - 16f * s;
        DrawChevron(centre, -Mathf.Pi / 2f, chevronAt, s, up);
        DrawChevron(centre, 0f, chevronAt, s, right);
        DrawChevron(centre, Mathf.Pi / 2f, chevronAt, s, down);
        DrawChevron(centre, Mathf.Pi, chevronAt, s, left);

        // The centre dot, which resets: a disc with a ring so it reads as a button on the dark body.
        bool dotPressed = _held && _pressQuadrant == JoystickQuadrant.Origin;
        bool dotHover = !_held && _hover == JoystickQuadrant.Origin;
        float dot = CoreJoystick.CenterDotRadius * s;
        DrawCircle(centre, dot, dotPressed ? UiTheme.Accent : dotHover ? new Color(1f, 1f, 1f, 0.55f) : new Color(DotColor, 0.45f), true, -1f, true);
        DrawCircle(centre, dot, dotPressed ? Colors.White : dotHover ? Colors.White : DotColor, false, Mathf.Max(1.5f * s, 1f), true);
    }

    /// <summary>A 90 degree wedge from the centre towards <paramref name="angle"/> (screen radians, y down), cut
    /// off at the outline of the pad so it follows the circle or the rounded corners.</summary>
    private void DrawWedge(Vector2 centre, float angle, float reach, float corner, Color color)
    {
        var points = new Vector2[WedgeSegments + 2];
        points[0] = centre;
        for (int i = 0; i <= WedgeSegments; i++)
        {
            float a = angle - Mathf.Pi / 4f + i * (Mathf.Pi / 2f) / WedgeSegments;
            float r = Round ? reach : SquareRadius(a, reach, corner);
            points[i + 1] = centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
        }

        DrawColoredPolygon(points, color);
    }

    /// <summary>Distance from the centre to the outline of a rounded square of half-width
    /// <paramref name="half"/> and corner radius <paramref name="corner"/>, along <paramref name="angle"/>.</summary>
    private static float SquareRadius(float angle, float half, float corner)
    {
        float ax = Mathf.Abs(Mathf.Cos(angle)), ay = Mathf.Abs(Mathf.Sin(angle));
        float toSide = half / Mathf.Max(ax, ay); // where the ray meets the plain square
        float inner = half - corner;
        if (corner <= 0f || ax * toSide <= inner || ay * toSide <= inner) return toSide; // on a flat side
        // Inside a corner: intersect the ray with that corner's circle, centred at (inner, inner).
        float along = (ax + ay) * inner;
        return along + Mathf.Sqrt(Mathf.Max(along * along - 2f * inner * inner + corner * corner, 0f));
    }

    private void DrawChevron(Vector2 centre, float angle, float distance, float s, bool lit)
    {
        // A chevron pointing up, rotated to face <angle> (up is -pi/2, so the turn is angle + pi/2).
        float turn = angle + Mathf.Pi / 2f;
        var at = centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
        var points = new[]
        {
            at + new Vector2(-6f * s, 3f * s).Rotated(turn),
            at + new Vector2(0f, -3f * s).Rotated(turn),
            at + new Vector2(6f * s, 3f * s).Rotated(turn),
        };
        DrawPolyline(points, lit ? Colors.White : ChevronColor, Mathf.Max(2f * s, 1.5f), true);
    }
}
