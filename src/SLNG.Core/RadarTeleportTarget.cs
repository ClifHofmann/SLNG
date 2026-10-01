using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// Picks the full region-local position for a teleport aimed at a point on the radar, which only
/// knows x and y (FEAT-UI-39). Firestorm uses the camera's Z for this and never looks at the
/// ground; the heightmap is already in memory here, so aim just above the ground instead -- a
/// click on a hill and a click in a valley both land on the surface.
/// </summary>
public static class RadarTeleportTarget
{
    /// <summary>Metres above the ground to ask for, so the avatar is not placed inside it.</summary>
    public const float ClearanceMetres = 1f;

    /// <summary>Ground at the point plus <see cref="ClearanceMetres"/>; else <paramref name="fallbackZ"/>
    /// (the avatar's own height) when that patch of terrain has not arrived; else 0, which leaves
    /// the placement to the simulator.</summary>
    public static Vector3 Resolve(RegionTerrain? terrain, Vector2 local, float? fallbackZ)
    {
        float z;
        if (terrain != null && terrain.TryGetKnownHeight((int)local.X, (int)local.Y, out var ground))
            z = ground + ClearanceMetres;
        else
            z = fallbackZ ?? 0f;
        return new Vector3(local.X, local.Y, z);
    }
}
