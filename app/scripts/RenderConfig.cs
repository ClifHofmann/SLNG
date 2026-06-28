using Godot;
using SLNG.Core.ECS;
using SLNG.Core.Components;

namespace SLNG.App;

/// <summary>
/// Shared, runtime-tunable rendering limits. Kept tiny and engine-side (app layer only).
/// </summary>
public static class RenderConfig
{
    /// <summary>
    /// Draw distance in metres. Objects and avatars farther than this from the local agent
    /// are hidden (and far avatars skip animation). Busy grids (e.g. OSGrid) stream far more
    /// content than fits in a frame budget, so capping what we render keeps the client
    /// responsive. SL viewers default to ~64–128 m.
    /// </summary>
    public static float DrawDistance = 128f;

    /// <summary>
    /// Returns the local agent's position converted to Godot world space, or false if the
    /// agent (or its transform) isn't in the world yet.
    /// </summary>
    public static bool TryGetLocalAgentGodotPos(World world, out Vector3 pos)
    {
        pos = Vector3.Zero;
        foreach (var e in world.GetAllEntities())
        {
            var avatar = e.GetComponent<AvatarComponent>();
            if (avatar == null || !avatar.IsLocalAgent) continue;

            var t = e.GetComponent<TransformComponent>();
            if (t == null) return false;

            uint regionX = (uint)(e.RegionHandle >> 32);
            uint regionY = (uint)(e.RegionHandle & 0xFFFFFFFF);
            pos = new Vector3(regionX + t.Position.X, t.Position.Z, -(regionY + t.Position.Y));
            return true;
        }
        return false;
    }
}
