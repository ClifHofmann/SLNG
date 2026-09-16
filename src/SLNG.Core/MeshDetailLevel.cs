namespace SLNG.Core;

/// <summary>
/// Engine- and protocol-neutral level of detail, for BOTH kinds of SL geometry.
/// <see cref="SLNG.Assets.AssetService.GetPrimMeshAsync"/> and
/// <see cref="SLNG.Assets.AssetService.GetMeshAsync"/> map this onto LibreMetaverse's own
/// <c>LibreMetaverse.Rendering.DetailLevel</c> internally -- callers outside <c>SLNG.Assets</c>
/// (i.e. the renderer in <c>app/</c>) must never reference that LibreMetaverse type directly, per
/// the "no LibreMetaverse type crosses this boundary" rule in AGENTS.md.
///
/// <para>For a PROCEDURAL prim the level is a tessellation parameter -- the mesher generates the
/// geometry on the spot at that density. For an UPLOADED LLMesh asset it selects between the four
/// geometry blocks the creator baked into the asset (<c>lowest_lod</c> / <c>low_lod</c> /
/// <c>medium_lod</c> / <c>high_lod</c>, in this enum's order); nothing is generated and nothing is
/// simplified at runtime. Beware the off-by-one in the names: <see cref="High"/> is the asset's
/// <c>medium_lod</c>. See FEAT-PERF-07.</para>
///
/// Matters most for CURVED profiles (sphere/torus/ring -- anything using ProfileCurve.Circle,
/// HalfCircle, or EqualTriangle): the side count these curves are tessellated with scales
/// directly with this level (6/12/24 sides for Low/Medium/High+). A large or heavily-scaled
/// curved element rendered at Medium (12 sides) is visibly faceted -- flat, angular, "origami" --
/// where a real SL viewer's own distance/size-based LOD would show a much smoother curve.
/// Flat-profile shapes (box/prism) are largely unaffected, since their sides aren't curve-based.
/// </summary>
public enum MeshDetailLevel
{
    Low,
    Medium,
    High,
    Highest
}
