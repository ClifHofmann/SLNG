namespace SLNG.Core;

/// <summary>The handful of PCode values SLNG's own rendering logic branches on, kept as named
/// bytes so <c>app/</c> -- which must never reference <c>LibreMetaverse.PCode</c> directly, per
/// AGENTS.md's layering rule that no LibreMetaverse type crosses <c>SLNG.Net</c>/<c>SLNG.Assets</c>'s
/// public boundary -- has a neutral name instead of a magic number. Values verified by reflection
/// against the pinned LibreMetaverse 3.1.3 assembly's own <c>PCode</c> enum, not assumed from the
/// wire spec or the vendored source.</summary>
public static class PrimPCode
{
    /// <summary>Legacy tree object. LMV's <c>PCode.Tree</c> = 255.</summary>
    public const byte Tree = 255;

    /// <summary>Modern (post-2010) tree object -- what a real region actually sends for a rezzed
    /// tree; <see cref="Tree"/> is a legacy value kept for completeness. LMV's
    /// <c>PCode.NewTree</c> = 111.</summary>
    public const byte NewTree = 111;

    /// <summary>Grass patch object. LMV's <c>PCode.Grass</c> = 95.</summary>
    public const byte Grass = 95;

    /// <summary>True for any of the three procedural-foliage pcodes above. <see cref="SLNG.Assets.PrimMeshService"/>
    /// gives all three the same crossed-planes placeholder geometry (real branch/blade geometry is
    /// generated procedurally client-side from a species table the real viewer bundles, not from
    /// the object's ProfileCurve/PathCurve the way a normal prim's shape is), so anything that
    /// treats one specially should treat all three the same way.</summary>
    public static bool IsFoliage(byte pcode) => pcode is Tree or NewTree or Grass;
}
