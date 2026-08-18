using Godot;
using SLNG.Core.Components;
using SLNG.Core.ECS;

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
    public static float DrawDistance = 96f;

    /// <summary>
    /// How many milliseconds per frame the main thread may spend on deferred scene work (building
    /// object visuals, pushing sharpened textures to the GPU). Everything over budget waits for
    /// the next frame -- see <see cref="MainThreadWorkQueue"/> for why an unbounded flush is what
    /// caused the stutter this exists to fix.
    ///
    /// 3 ms is deliberately well under a 16.7 ms frame: the queue can only measure a unit of work
    /// AFTER running it, so the budget has to leave room for one over-long item to land on top of
    /// it without blowing the frame. Raising it makes objects appear sooner and stutter more.
    /// </summary>
    public static double MainThreadWorkBudgetMs = 3.0;

    // Floating origin: the global metre coordinates of the region we render relative to.
    // OSGrid regions sit at global coordinates in the millions; rendering at those raw
    // coordinates blows float32 precision (objects jitter / Z-fight / look shattered). We
    // subtract this origin so everything renders near 0. Set once the local region is known.
    public static double OriginX;
    public static double OriginY;

    /// <summary>Sets the floating origin to the given region's global SW corner.</summary>
    public static void SetRegionOrigin(ulong regionHandle)
    {
        OriginX = (uint)(regionHandle >> 32);
        OriginY = (uint)(regionHandle & 0xFFFFFFFF);
    }

    /// <summary>Converts an SL region-local position to Godot world space, relative to the
    /// floating origin. SL is Z-up; Godot is Y-up: SL(X,Y,Z) → Godot(X, Z, −Y).</summary>
    public static Vector3 ToGodot(ulong regionHandle, System.Numerics.Vector3 slLocal)
    {
        double gx = (uint)(regionHandle >> 32) + (double)slLocal.X - OriginX;
        double gy = (uint)(regionHandle & 0xFFFFFFFF) + (double)slLocal.Y - OriginY;
        return new Vector3((float)gx, slLocal.Z, (float)-gy);
    }

    /// <summary>Inverse of <see cref="ToGodot"/>: converts a Godot world-space position (e.g. a
    /// raycast hit point) back to SL region-local coordinates for the given region.</summary>
    public static System.Numerics.Vector3 FromGodot(ulong regionHandle, Vector3 godotPos)
    {
        double regionX = (uint)(regionHandle >> 32);
        double regionY = (uint)(regionHandle & 0xFFFFFFFF);
        float slX = (float)(godotPos.X - regionX + OriginX);
        float slY = (float)(-godotPos.Z - regionY + OriginY);
        return new System.Numerics.Vector3(slX, slY, godotPos.Y);
    }

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

            pos = ToGodot(e.RegionHandle, t.Position);
            return true;
        }
        return false;
    }
}
