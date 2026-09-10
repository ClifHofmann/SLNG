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
    /// If false, objects with a bounding radius smaller than 0.5m will not cast shadows.
    /// This drastically reduces draw calls on dense regions with lots of small details (grass, rocks).
    /// </summary>
    public static bool SmallObjectShadows = false;

    /// <summary>
    /// BUG-RENDER-16: how to render an undeclared-alpha world-prim face whose texture
    /// <c>analyzeAlphaData</c> classifies as NOT maskable (high-frequency / gradient alpha — thin
    /// grass, wispy foliage). Clean cutouts (<c>maskable == true</c>: fences, sharp leaf cards)
    /// are unaffected and stay on Scissor regardless.
    ///
    /// <list type="bullet">
    /// <item><see cref="FoliageAlpha.Scissor"/> — BUG-RENDER-11's shipped behaviour: hard binary
    ///   cutout at 0.33, depth-writing opaque queue, no sort so nothing pops, but the soft blade
    ///   tips are chopped and thin low-alpha detail vanishes.</item>
    /// <item><see cref="FoliageAlpha.Blend"/> — reference-viewer parity
    ///   (<c>LLFace::canRenderAsMask()</c> false → <c>PASS_ALPHA</c>): soft feathered edge back,
    ///   but Godot's per-object AABB-centre transparent sort is coarse, so overlapping foliage
    ///   swaps draw order as the camera orbits (the flicker BUG-RENDER-11 chose Scissor to avoid).</item>
    /// <item><see cref="FoliageAlpha.Hash"/> — BUG-RENDER-09's stochastic cutout
    ///   (<see cref="PrimShaderFamily.Hash"/>): depth written like opaque so nothing sorts and
    ///   nothing pops, and the gradient resolves as a dither rather than a hard step. The price is
    ///   dither noise that can shimmer under motion / read as over-sharp speckle without TAA
    ///   (SLNG has none).</item>
    /// <item><see cref="FoliageAlpha.Prepass"/> — <see cref="PrimShaderFamily.BlendPrepass"/>:
    ///   soft blended colour like Blend, plus an alpha depth-prepass so overlapping foliage
    ///   self-occludes by depth and the coarse per-object sort stops flickering it. Costs a second
    ///   draw of the geometry. (Godot's prepass alpha threshold is strict, so mid-alpha overlap
    ///   can still sort — observed to still flicker on dense grass.)</item>
    /// <item><see cref="FoliageAlpha.Edge"/> — <see cref="PrimShaderFamily.ScissorEdge"/>:
    ///   Scissor's flicker-free opaque/depth-write pass, but with <c>ALPHA_ANTIALIASING_EDGE</c>.
    ///   Still limited to MSAA's 4 coverage levels, so the edge stays fairly hard.</item>
    /// <item><see cref="FoliageAlpha.BlendDepth"/> — <see cref="PrimShaderFamily.BlendDepth"/>:
    ///   blended colour + <c>depth_draw_always</c>. Every fragment writes depth so overlapping
    ///   foliage self-occludes and the sort stops mattering — no flicker — while the outer
    ///   silhouette still blends softly against the background. Blade-on-blade overlap reads as
    ///   opaque rather than blended.</item>
    /// </list>
    ///
    /// Set via <c>--foliage-alpha=scissor|blend|hash|prepass|edge|blenddepth</c> (or the alias
    /// <c>--foliage-blend</c>) on the command line. The default is
    /// <see cref="HighFrequencyFoliageAlpha"/> below; the other five stay wired only as the
    /// in-world A/B harness the real fix (see that field's comment) will need.
    /// </summary>
    public enum FoliageAlpha { Scissor, Blend, Hash, Prepass, Edge, BlendDepth }

    // BUG-RENDER-16: default to Blend. Round after round of in-world A/B on dense pink grass
    // settled it: the depth-writing / opaque-queue modes (scissor, edge, hash) never flicker but
    // their edge is always hard, blocky or grainy -- 4x-MSAA coverage has 4 levels and the hash
    // dither reads as speckle even with FXAA and a finer scale (TAA tried too, did not resolve
    // it). Blend is the only mode whose texture the user accepted as matching Firestorm. Its
    // flicker under camera motion is Godot's coarse per-SURFACE transparent sort and the real fix
    // is to merge a foliage mesh's same-material faces into one sortable surface (the BUG-RENDER-12
    // approach); until that lands this ships the correct look with the known flicker rather than a
    // permanently degraded edge. --foliage-alpha= overrides for an A/B.
    public static FoliageAlpha HighFrequencyFoliageAlpha = FoliageAlpha.Blend;

    /// <summary>BUG-RENDER-16: <c>ALPHA_HASH_SCALE</c> for the Hash foliage path. Higher = finer
    /// dither cells (less blocky, reads smoother once FXAA blurs it); 1.0 is Godot's own default.
    /// Override with <c>--foliage-hash-scale=N</c>.</summary>
    public static float FoliageHashScale = 2.0f;

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

    /// <summary>
    /// Ceiling for the adaptive budget below. A deep backlog is the user staring at a half-built
    /// scene, and at that point a few dropped frames buy back minutes of waiting -- but the cap
    /// keeps even the worst case inside a 16.7 ms frame with room for one over-long item.
    /// </summary>
    public static double MainThreadWorkBudgetMaxMs = 9.0;

    /// <summary>Queue depth at which the budget starts rising above
    /// <see cref="MainThreadWorkBudgetMs"/>, and the depth at which it reaches
    /// <see cref="MainThreadWorkBudgetMaxMs"/>.</summary>
    public static int MainThreadWorkBacklogSoft = 200;
    public static int MainThreadWorkBacklogHard = 2000;

    /// <summary>
    /// BUG-NET-11: the budget is a steady-state figure, and it was being applied to a transient
    /// that is not steady state at all. Arriving in a dense region queues thousands of visual
    /// builds and texture uploads at once; at a flat 3 ms/frame that backlog drains at roughly the
    /// frame rate, so a scene with ~8800 texture requests (measured live, 2026-09-03) takes
    /// minutes to finish appearing however fast the decode is. Spending up to
    /// <see cref="MainThreadWorkBudgetMaxMs"/> while the backlog is deep converts that latency
    /// into a brief, bounded frame-rate cost and then returns to 3 ms the moment the queue drains
    /// -- an idle or steady scene never sees a different budget from before.
    /// </summary>
    public static double MainThreadWorkBudgetFor(int queueDepth)
    {
        if (queueDepth <= MainThreadWorkBacklogSoft) return MainThreadWorkBudgetMs;
        if (queueDepth >= MainThreadWorkBacklogHard) return MainThreadWorkBudgetMaxMs;

        double t = (double)(queueDepth - MainThreadWorkBacklogSoft)
                 / (MainThreadWorkBacklogHard - MainThreadWorkBacklogSoft);
        return MainThreadWorkBudgetMs + t * (MainThreadWorkBudgetMaxMs - MainThreadWorkBudgetMs);
    }

    /// <summary>
    /// Radius within which an object's collision shape must exist IMMEDIATELY rather than being
    /// built in the background.
    ///
    /// Deferring the shape is what makes a busy region load without stuttering, but it has one
    /// unacceptable consequence: the avatar's ground check is a downward raycast, so an object whose
    /// shape has not landed yet is not merely unclickable, it is not there to stand on -- you walk
    /// onto a prim and drop through it. Anything you could be standing on is by definition close, so
    /// a small radius buys back correctness for a tiny fraction of the objects.
    /// </summary>
    public static float CollisionUrgentDistance = 24f;

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
