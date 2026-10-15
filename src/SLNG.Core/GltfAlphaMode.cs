namespace SLNG.Core;

/// <summary>glTF alpha mode in the WIRE numbering (llgltfmaterial.h: OPAQUE 0, BLEND 1, MASK 2). NOT the numbering of
/// SLNG.Assets.PbrAlphaMode (Opaque, Mask, Blend): never cast between them, map explicitly.</summary>
public enum GltfAlphaMode
{
    Opaque = 0,
    Blend = 1,
    Mask = 2
}
