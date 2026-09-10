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
    /// FEAT-PERF-06: render groups of identical, repeated, static prims (same shared mesh, same
    /// single material, same shadow flag) through one <c>MultiMeshInstance3D</c> instead of one
    /// <c>MeshInstance3D</c> each. Measured trigger: a fully-loaded OpenSim villa region submitting
    /// ~12–14k draw calls because nothing batches. A prim is evicted back to its own node the
    /// instant it is selected, edited, animated or leaves draw distance. Turn off with
    /// <c>--no-instancing</c> for an A/B.
    /// </summary>
    public static bool EnableInstancing = true;

    /// <summary>
    /// FEAT-PERF-06: an object farther than this (metres, to whichever of avatar/camera is nearer)
    /// stops casting a directional shadow. The shadow DEPTH pass draws the scene once per CSM
    /// split, so a distant object is drawn 2 extra times for a shadow the atmosphere haze hides
    /// anyway. Measured on the villa scene: shadows off entirely is ~15 FPS, and a pure draw-call
    /// cut (instancing) moved nothing — the shadow pass is the cost. Default is well inside the
    /// 96 m draw distance; near shadows, which are the ones you actually read, are untouched.
    /// Instanced groups decide this once at join time from the shared mesh size instead.
    /// </summary>
    public static float ShadowCasterDistance = 56f;

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
    /// <item><see cref="FoliageAlpha.BlendCore"/> — Blend's soft colour pass, plus a depth-only
    ///   companion pass (<see cref="PrimShaderFamily.DepthCore"/>, chained as the material's
    ///   <c>next_pass</c>) that runs BEFORE every transparent draw and writes depth for fragments
    ///   with alpha ≥ <see cref="FoliageCoreAlpha"/>. The reference viewer's own alpha depth pass
    ///   (lldrawpoolalpha.cpp:212-227, threshold 0.33). Unlike <c>Prepass</c> the threshold is
    ///   ours, not Godot's fixed 0.99; unlike <c>BlendDepth</c> a transparent texel writes no
    ///   depth, so it punches no holes. The fringe still blends softly; the cores occlude by real
    ///   depth in every draw order, which removes the parallax "zappeln" while walking.</item>
    /// </list>
    ///
    /// Set via <c>--foliage-alpha=scissor|blend|hash|prepass|edge|blenddepth|blendcore</c> (or
    /// the alias <c>--foliage-blend</c>) on the command line. The default is
    /// <see cref="HighFrequencyFoliageAlpha"/> below; the other modes stay wired only as the
    /// in-world A/B harness.
    /// </summary>
    public enum FoliageAlpha { Scissor, Blend, Hash, Prepass, Edge, BlendDepth, BlendCore }

    // BUG-RENDER-16: default to Blend. Round after round of in-world A/B on dense pink grass
    // settled it: the depth-writing / opaque-queue modes (scissor, edge, hash) never flicker but
    // their edge is always hard, blocky or grainy -- 4x-MSAA coverage has 4 levels and the hash
    // dither reads as speckle even with FXAA and a finer scale (TAA tried too, did not resolve
    // it). Blend is the only mode whose texture the user accepted as matching Firestorm. Its
    // flicker under camera motion is Godot's coarse per-SURFACE transparent sort and the real fix
    // is to merge a foliage mesh's same-material faces into one sortable surface (the BUG-RENDER-12
    // approach); until that lands this ships the correct look with the known flicker rather than a
    // permanently degraded edge. --foliage-alpha= overrides for an A/B.
    //
    // v0.22.24 tried BlendCore as the default (the --alpha-sort-freeze test had shown that a frozen
    // order still flickers when walking, i.e. the WRONG order made visible by parallax, which only
    // per-pixel depth removes) and it calmed the Millenium grass in-world -- but it turned a pine's
    // canopy into a pale haze, reproduced offline with the real needle texture
    // (scratch/probes/probe_motion.gd): at the mip level a canopy is actually viewed at, a thin
    // needle texel's alpha is ~0.5, so it writes depth and hides every card behind it while itself
    // covering only half the pixel. Blend accumulates those layers into a dense dark canopy; the
    // depth pass leaves ONE half-transparent layer over the sky. No threshold serves both textures
    // (0.9 still reads lighter than Blend on the pine, and leaves the plumes to the sort), so
    // BlendCore is opt-in per session (--foliage-alpha=blendcore) and Blend stays the default.
    public static FoliageAlpha HighFrequencyFoliageAlpha = FoliageAlpha.Blend;

    /// <summary>BUG-RENDER-16: alpha at or above which a <see cref="FoliageAlpha.BlendCore"/> face
    /// writes depth in its companion pass. The reference viewer's 0.33 (lldrawpoolalpha.cpp:217).
    /// Lower = more of the blade occludes (fewer mis-ordered pairs, but a mid-alpha core hides
    /// what is behind it and shows the background through itself instead); higher = softer but
    /// more of the fringe is left to the sort. Override with <c>--foliage-core-alpha=N</c>.</summary>
    public static float FoliageCoreAlpha = 0.33f;

    /// <summary>BUG-RENDER-16 (v0.22.26): give every sorted-transparent surface of a multi-surface
    /// object its own <c>MeshInstance3D</c>, so Godot sorts it by its OWN bounds instead of tying
    /// it with its siblings at the parent's depth.
    ///
    /// <para>Godot assigns ONE sort depth per instance (<c>render_forward_clustered.cpp:961-966</c>)
    /// and orders the tie with an unstable introsort, so two blended surfaces of one object draw in
    /// an order that depends on the surrounding render list -- which changes as the camera moves.
    /// The Millenium grass is exactly that: each of 23 linkset parts carries TWO blended faces
    /// (different textures, no material) beside its alpha-masked ones, measured 2026-09-10 via the
    /// click diagnostic. Freezing, hysteresis and planar depth are all per INSTANCE and cannot
    /// touch a tie inside one; the reference viewer never has the tie because it sorts per face
    /// (<c>LLFace::CompareDistanceGreater</c> in <c>genDrawInfo</c>). This is that granularity.</para>
    ///
    /// <para>The shared ArrayMesh is untouched: the parent wears <see cref="PrimShaderFamily.Hidden"/>
    /// on the moved surface and the child gets a one-surface copy, cached per (mesh, surface) so a
    /// field of identical plants still shares. Off with <c>--alpha-split=off</c>.</para></summary>
    public static bool SplitSortedSurfaces = true;

    /// <summary>BUG-RENDER-16: a FALSIFICATION TEST, not a fix. Freezes each transparent object's
    /// sort depth at the value it had the first time it was seen, so the transparent draw order
    /// becomes completely camera-independent and can never change again.
    ///
    /// <para>Four fixes have been aimed at the alpha sort — the surface merge (how many sortable
    /// pieces), the hysteresis (when re-sorting is allowed) and planar depth (what is sorted by) —
    /// and the flicker survived all of them. Each was grounded in the reference viewer's source,
    /// so the thing left to question is the premise they share: that the flicker is a SORT ORDER
    /// problem at all.</para>
    ///
    /// <para>This mode settles that. With the order frozen there is nothing left for the sorter to
    /// change, at any camera position or angle. If the grass still flickers, the cause is not the
    /// transparent draw order and every fix in that direction is wasted. The picture will layer
    /// oddly as you walk past — that is expected and is not what is being judged; the only
    /// question is whether it still SHIMMERS.</para>
    ///
    /// Set with <c>--alpha-sort-freeze</c>.
    /// </summary>
    public static bool AlphaSortFreezeDebug;

    /// <summary>BUG-RENDER-16: sort transparent objects by PLANAR depth along the view axis, the
    /// way the reference viewer does, instead of Godot's radial distance from the camera.
    ///
    /// <para>Godot: <c>inst->depth = cam_transform.origin.distance_to(center)</c>
    /// (<c>render_forward_clustered.cpp:961-966</c>) — a RADIAL distance.</para>
    ///
    /// <para>Viewer: alpha groups are ordered by <c>CompareDepthGreater</c> on
    /// <c>mDepth</c> (<c>pipeline.cpp:3732</c>, <c>llspatialpartition.h:236</c>), and
    /// <c>mDepth</c> is built only for groups that contain <c>PASS_ALPHA</c>
    /// (<c>llspatialpartition.cpp:684-692</c>):</para>
    /// <code>
    /// LLVector3 at = camera.getAtAxis();
    /// t = at; t.mul(0.25f); t.mul(group->mObjectBounds[1]);  // toward the front of the box
    /// v.sub(t);
    /// group->mDepth = v.dot3(ata);                           // PROJECTION on the view axis
    /// </code>
    /// <para>The non-alpha branch right below it keeps <c>eye.getLength3()</c> — radial. So the
    /// viewer switches to planar depth deliberately, and only for alpha.</para>
    ///
    /// <para><b>Why this is the one that matches the symptom.</b> Reported in-world: the grass is
    /// steady when standing still AND when only turning the camera, and flickers when the avatar
    /// MOVES. Radial distance is invariant under rotation, which is exactly why turning is calm —
    /// but walking forward changes it by completely different amounts for an object ahead and an
    /// object off to the side, so the order churns. Planar depth decreases by the SAME amount for
    /// every object when you walk along the view axis, so the order is preserved outright. Three
    /// earlier rounds aimed at how many sortable pieces there were and at when re-sorting is
    /// allowed; neither touched what is being sorted BY.</para>
    ///
    /// Set with <c>--alpha-sort-planar</c>.
    /// </summary>
    public static bool AlphaSortPlanarDepth;

    /// <summary>BUG-RENDER-16: the reference viewer's alpha-sort HYSTERESIS, ported per object.
    /// <c>0</c> disables it (default); the viewer's own value is <c>0.64</c>.
    ///
    /// <para>Measured on SL <i>Millenium</i> at <c>v0.22.19</c>: <b>1310</b> objects carry surfaces
    /// in Godot's sorted transparent queue, and <b>937</b> of them have exactly ONE such surface.
    /// The same-material surface merge cannot touch those — Godot gives every surface of one
    /// instance the same depth, so a single-surface object has no internal tie left to remove.
    /// What reorders them is the sort BETWEEN objects, re-run from scratch every single frame.</para>
    ///
    /// <para>The viewer re-sorts alpha only once the viewing direction has moved far enough
    /// (<c>llspatialpartition.cpp:657-676</c>). Note what is compared: <c>eye</c> is
    /// <c>normalize3fast()</c>'d first, so <c>0.64</c> is a CHORD on the unit sphere — about
    /// <b>37°</b> of direction change — not a distance and not 0.64 radians. Between those
    /// re-sorts the order is frozen, which is the whole reason Firestorm is stable on content that
    /// shimmers here: AABB-centre distance is a poor proxy for the true per-pixel order of
    /// interpenetrating grass cards, so re-deciding it every frame swaps between orderings that
    /// are each wrong in different pixels.</para>
    ///
    /// <para>The port sets <c>GeometryInstance3D.SortingOffset</c> so an object sorts at the depth
    /// it had when its direction was last frozen; Godot then reproduces the frozen order without
    /// any change to its sorter. The viewer freezes per spatial-octree group, SLNG per object,
    /// which is finer-grained and needs no spatial structure SLNG does not have.</para>
    ///
    /// <para>The trade this buys, and it is a real one: instead of continuous shimmer the order
    /// snaps once per ~37° of orbit. That is exactly what the reference viewer does.</para>
    ///
    /// Set with <c>--alpha-sort-hysteresis</c> (uses 0.64) or <c>--alpha-sort-hysteresis=N</c>.
    /// </summary>
    public static float AlphaSortHysteresis;

    /// <summary>BUG-RENDER-16: the reference viewer's own threshold, used when
    /// <c>--alpha-sort-hysteresis</c> is passed without a value (llspatialpartition.cpp:667).</summary>
    public const float ViewerAlphaSortHysteresis = 0.64f;

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
