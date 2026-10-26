namespace SLNG.Core.Camera;

/// <summary>
/// What the third-person camera is currently centred on. It decides how close the camera may get
/// (<see cref="CameraZoomLimits.MinDistance"/>), because the Linden viewer keeps one minimum per kind of
/// subject (FEAT-UI-73).
/// </summary>
public enum CameraFocusKind
{
    /// <summary>The default view: following one's own avatar, no Alt+Click focus point.</summary>
    OwnAvatar,

    /// <summary>An Alt+Click (or roster) focus on a person, or on an item worn by one - the viewer replaces a
    /// worn item by its wearer (llagentcamera.cpp:2607-2614).</summary>
    Avatar,

    /// <summary>An Alt+Click focus on a prim or mesh in the world.</summary>
    Object,

    /// <summary>An Alt+Click focus on the ground.</summary>
    Land,
}
