using System;

namespace SLNG.Core.Camera;

/// <summary>
/// How close the camera may get to what it looks at, and the near plane that keeps that point visible
/// (FEAT-UI-73). Engine-free so it can be tested; the camera controller only supplies the kind of focus.
///
/// <para>The numbers are the Linden viewer's, from <c>indra/newview/llagentcamera.cpp</c> of the vendored
/// clone: a focused person 0.5 m (<c>AVATAR_MIN_ZOOM</c>, :90), an object 0.02 m (<c>OBJECT_MIN_ZOOM</c>,
/// :91), the ground 0.15 m (<c>LAND_MIN_ZOOM</c>, :88). The wheel path (<c>handleScrollWheel</c> :2080) applies
/// them in <c>cameraOrbitIn</c> (:1017-1032) and then <c>cameraZoomIn</c> (:950-967). While following one's own
/// avatar the viewer instead stops at a quarter of the resting offset (<c>MIN_ZOOM_FRACTION</c>, :59, 0.77 m at
/// the default 3.09 m) and switches to mouselook (:997-1001); SLNG has no mouselook (FEAT-RENDER-23), so that
/// view keeps the avatar floor.</para>
///
/// <para>What is not ported: for a person the viewer also pushes the minimum out to the edge of the
/// avatar's shrunk bounding box when the camera has gone round to the far side of the focus point
/// (<c>calcCameraMinDistance</c>, :575-722), and for a non-mesh prim it backs the camera off and narrows
/// the field of view instead of going closer (<c>calcCameraFOVZoomFactor</c>, :1728-1753). SLNG goes
/// straight to the 0.02 m and lowers the near plane to match (<see cref="NearPlane"/>), which shows the
/// same picture without the back-off.</para>
/// </summary>
public static class CameraZoomLimits
{
    /// <summary>Nearest to a focused point on the ground: <c>LAND_MIN_ZOOM</c>, llagentcamera.cpp:88.</summary>
    public const float LandMinZoom = 0.15f;

    /// <summary>Nearest to a focused person (and the floor while following one's own avatar):
    /// <c>AVATAR_MIN_ZOOM</c>, llagentcamera.cpp:90.</summary>
    public const float AvatarMinZoom = 0.5f;

    /// <summary>Nearest to a focused object: <c>OBJECT_MIN_ZOOM</c>, llagentcamera.cpp:91.</summary>
    public const float ObjectMinZoom = 0.02f;

    /// <summary>The near plane while the camera is not closer than <c>DefaultNearPlane / NearToFocusRatio</c>
    /// to its focus: Godot's own default, which SLNG has always run with.</summary>
    public const float DefaultNearPlane = 0.05f;

    /// <summary>How much nearer than the focus point the near plane sits when the camera is very close, so the
    /// point and the surface around it stay in front of the plane: half the distance.</summary>
    public const float NearToFocusRatio = 0.5f;

    /// <summary>Smallest near plane ever used: half of the smallest minimum distance.</summary>
    public const float MinNearPlane = ObjectMinZoom * NearToFocusRatio;

    /// <summary>The floor while the camera constraints are switched off (the viewer's <c>DisableCameraConstraints</c>): not
    /// zero, because the zoom slider is logarithmic, but a millimetre, which puts the camera on its focus point.</summary>
    public const float UnconstrainedMinZoom = 0.001f;

    /// <summary>How far an Alt+Click on a person is moved INTO the body along the click ray. The pick shapes
    /// (<c>AvatarRenderer.PickLimbs</c>) are deliberately wider than the skin so a click always lands, which put the
    /// focus point about 7 cm in front of the surface the person pointed at; the reference viewer's collision volumes
    /// hug the body, so there the point is on it.</summary>
    public const float AvatarFocusInset = 0.07f;

    /// <summary>The nearest the camera may get to its focus point, in metres.</summary>
    public static float MinDistance(CameraFocusKind kind) => kind switch
    {
        CameraFocusKind.Land => LandMinZoom,
        CameraFocusKind.Object => ObjectMinZoom,
        _ => AvatarMinZoom,
    };

    /// <summary><see cref="MinDistance(CameraFocusKind)"/>, or the near-zero floor when the person has switched the camera
    /// constraints off -- Firestorm's "Disable camera constraints", which lets the wheel take the camera all the way to the
    /// focus point (on a person, into the body).</summary>
    public static float MinDistance(CameraFocusKind kind, bool constraintsDisabled)
        => constraintsDisabled ? UnconstrainedMinZoom : MinDistance(kind);

    /// <summary>
    /// The near plane for a camera <paramref name="focusDistance"/> metres from its focus point: the default
    /// 0.05 m, or half the distance when that is smaller, never below <see cref="MinNearPlane"/>. At the
    /// closest allowed distance (0.02 m) it is 0.01 m, so the focus point is never clipped. The viewer's own
    /// near plane is a fixed 0.1 m (llagentcamera.cpp:207-208, MIN_NEAR_PLANE llcamera.h:47), which is why it
    /// can only get that close by backing the camera off.
    /// </summary>
    public static float NearPlane(float focusDistance)
    {
        if (!(focusDistance > 0f)) return MinNearPlane; // zero, negative or NaN: as close as it gets
        return Math.Clamp(focusDistance * NearToFocusRatio, MinNearPlane, DefaultNearPlane);
    }
}
