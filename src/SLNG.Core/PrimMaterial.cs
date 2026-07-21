namespace SLNG.Core;

/// <summary>Classic SL prim material (affects collision sound/friction) -- distinct from
/// PrimitiveComponent.RenderMaterialId, which is the PBR glTF material. Values match
/// LibreMetaverse's OpenMetaverse.Material byte exactly so GridSession can cast directly at the
/// SLNG.Net boundary without a lookup table.</summary>
public enum PrimMaterial : byte
{
    Stone = 0,
    Metal = 1,
    Glass = 2,
    Wood = 3,
    Flesh = 4,
    Plastic = 5,
    Rubber = 6,
}
