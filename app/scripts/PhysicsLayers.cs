namespace SLNG.App;

/// <summary>Named bits for Godot's 32-bit physics collision layers/masks, shared across the
/// renderers and controllers that build or query <c>CollisionShape3D</c>. Centralized because a
/// raycast mask that disagrees with what a body is actually on fails silently -- there is no
/// compiler or runtime check that the two sides of a layer/mask pair agree, only a raycast that
/// quietly stops finding what it used to.
///
/// <see cref="ObjectRenderer"/> additionally defines its own <c>PhantomLayer = 1u &lt;&lt; 2</c>
/// (bit value 4) locally, for phantom prims -- not duplicated here since nothing outside that file
/// queries against it, but any FUTURE bit added here must skip 4 to avoid colliding with it.</summary>
public static class PhysicsLayers
{
    /// <summary>Non-phantom prims and meshes -- <see cref="ObjectRenderer"/>'s StaticBody default.</summary>
    public const uint Objects = 1u;

    /// <summary>Avatar hover capsules -- <see cref="AvatarRenderer"/>'s per-avatar StaticBody, so a
    /// raycast can identify which avatar it hit.</summary>
    public const uint Avatars = 1u << 1;

    /// <summary>Phantom prims -- <see cref="ObjectRenderer"/> puts them here instead of disabling
    /// their <c>CollisionShape3D</c>, so they stay clickable while staying walk-through.
    ///
    /// A ray that asks "what is the user LOOKING at" must include this bit; one that asks "what
    /// can the avatar stand on or bump into" must not. Most SL foliage is phantom, which is why
    /// leaving it out of the Alt+Click focus ray put the focus point on the ground behind the
    /// bush the user clicked (BUG-UI-09).</summary>
    public const uint Phantom = 1u << 2;

    /// <summary>Terrain's <c>HeightMapShape3D</c>. Deliberately its OWN layer, separate from
    /// <see cref="Objects"/>.
    ///
    /// TerrainRenderer marks unstreamed patches as NaN height so a raycast MISSES there rather
    /// than reporting a floor at height 0 (see its collision-build comment) -- but Godot's
    /// height-field raycast still runs a normalize() over the NaN cell en route to that miss,
    /// which prints "Vector3 cannot be normalized, the elements must be finite" to the console
    /// every time it happens. Measured: 45,862 of 45,872 such warnings in one session's log came
    /// from exactly one call site -- CursorManager's per-frame mouse-hover raycast -- which gets
    /// nothing useful from a terrain hit in the first place (terrain's StaticBody carries no
    /// PrimitiveComponent to react to, so hitting it never changes the cursor shape). Giving
    /// terrain its own bit lets that one query skip it entirely. AvatarController's ground
    /// raycast and ObjectSelectionController's ground-menu raycast keep including it explicitly,
    /// since both have a real reason to hit terrain and cannot exclude it.</summary>
    public const uint Terrain = 1u << 3;
}
