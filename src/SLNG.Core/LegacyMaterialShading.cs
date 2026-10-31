using System.Numerics;

namespace SLNG.Core;

/// <summary>Where a legacy material's map sits on a face: its own repeat/offset/rotation, folded the way the
/// renderer's diffuse placement is folded so the shader stays a multiply-add.</summary>
/// <param name="Scale">The repeat.</param>
/// <param name="Offset">Centring (<c>0.5 - 0.5 * repeat</c>) plus the material's offset, with the V offset
/// negated: the meshes are built V-flipped and the texture rows are top-origin, so the viewer's
/// <c>t' = (t-0.5)*rep + 0.5 + off</c> becomes <c>v' = (v-0.5)*rep + 0.5 - off</c> (llface.cpp:734-756).</param>
/// <param name="Rotation">Radians, passed through untouched (the shader rotates).</param>
public readonly record struct LegacyMapPlacement(Vector2 Scale, Vector2 Offset, float Rotation)
{
    public static LegacyMapPlacement Fold(Vector2 repeat, Vector2 offset, float rotation) =>
        new(repeat, new Vector2(0.5f - 0.5f * repeat.X + offset.X, 0.5f - 0.5f * repeat.Y - offset.Y), rotation);
}

/// <summary>
/// What a legacy material hands the shader besides its texture ids: the specular colour, glossiness and
/// environment intensity (bytes on the wire, normalised here as <c>llvovolume.cpp:5568-5573</c> does) and each
/// map's placement. The same numbers serve a prim face and a worn rigged face -- the viewer draws both through
/// <c>LLDrawPoolMaterials</c>, rigged through the shader's rigged variant (lldrawpoolmaterials.cpp:57-90).
///
/// <para>Whether the specular values are USED is the shader's gate, not this record's: they only apply to a
/// face whose material names a specular map (llvovolume.cpp:5565), the rest keep the Shiny level.</para>
/// </summary>
public readonly record struct LegacyMaterialShading(
    Vector3 SpecularTint, float Glossiness, float Environment, LegacyMapPlacement Normal, LegacyMapPlacement Specular, bool HasMaps)
{
    public static LegacyMaterialShading From(LegacyMaterialData m) => new(
        new Vector3(m.SpecularColor.X, m.SpecularColor.Y, m.SpecularColor.Z),
        m.SpecularExponent / 255f,
        m.EnvironmentIntensity / 255f,
        LegacyMapPlacement.Fold(m.NormalRepeat, m.NormalOffset, m.NormalRotation),
        LegacyMapPlacement.Fold(m.SpecularRepeat, m.SpecularOffset, m.SpecularRotation),
        m.HasMaps);
}
