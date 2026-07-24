namespace SLNG.Core;

/// <summary>
/// Engine- and protocol-neutral level of detail for procedurally generated prim geometry
/// (box/cylinder/sphere/torus cuts, not uploaded LLMesh assets, which carry their own baked
/// LOD blocks). <see cref="SLNG.Assets.AssetService.GetPrimMeshAsync"/> maps this onto
/// LibreMetaverse's own <c>LibreMetaverse.Rendering.DetailLevel</c> internally -- callers outside
/// <c>SLNG.Assets</c> (i.e. the renderer in <c>app/</c>) must never reference that LibreMetaverse
/// type directly, per the "no LibreMetaverse type crosses this boundary" rule in AGENTS.md.
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
