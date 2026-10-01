using System;
using Godot;

namespace SLNG.App;

/// <summary>
/// A camera focus point that rides on a subject (FEAT-UI-40): an avatar or an object the camera was
/// aimed at, which may walk, drive or be animated. The rule is the reference viewer's
/// (<c>LLAgentCamera::calcFocusPositionTargetGlobal</c>): the focus is the subject's position NOW plus
/// the offset the focus point had from it when it was set. The offset is a plain world-space vector --
/// it is not turned with the subject, so a rotating prim does not swing the camera around.
///
/// Engine-side on purpose: the subject is a scene node or a world entity, and where it is changes
/// every frame, so this only asks "where is it now?" and never remembers where it was.
/// </summary>
internal sealed class FocusFollow
{
    private readonly Func<Vector3?> _subjectPosition;
    private readonly Vector3 _offset;

    /// <param name="subjectPosition">Where the subject is now (Godot space), or null once it is gone.</param>
    /// <param name="focusPoint">The point the camera is aimed at now; its offset from the subject is kept.</param>
    private FocusFollow(Func<Vector3?> subjectPosition, Vector3 offset)
    {
        _subjectPosition = subjectPosition;
        _offset = offset;
    }

    /// <summary>The focus point at this moment, or null once the subject is gone -- the caller then
    /// leaves the camera where the point last was, as the viewer does for a focus object that died.</summary>
    public Vector3? Current => _subjectPosition() is { } subject ? subject + _offset : null;

    /// <summary>Follows whatever <paramref name="subjectPosition"/> reports. Null when the subject has no
    /// position to start from, since there is then no offset to keep.</summary>
    public static FocusFollow? Of(Func<Vector3?> subjectPosition, Vector3 focusPoint)
    {
        if (subjectPosition() is not { } subject) return null;
        return new FocusFollow(subjectPosition, focusPoint - subject);
    }

    /// <summary>Follows a physics body (the collider an Alt+Click ray hit): an object's own body, an
    /// avatar's, or one worn on an avatar, all of which are carried by the node they hang from. The body
    /// being freed or leaving the tree -- the object was deleted, the avatar left -- ends it.</summary>
    public static FocusFollow? OfBody(Node3D body, Vector3 focusPoint)
    {
        Vector3? Position() =>
            GodotObject.IsInstanceValid(body) && body.IsInsideTree() ? body.GlobalPosition : null;
        return Of(Position, focusPoint);
    }
}
