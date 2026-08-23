using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>How a legacy material says its DIFFUSE texture's alpha channel should be treated
/// (LLMaterial::eDiffuseAlphaMode, llmaterial.h).
///
/// <para>Worth having even before it is wired up: SLNG currently guesses this per face from the
/// decoded pixels (<c>Image.DetectAlpha()</c>), and a material states it outright.</para></summary>
public enum LegacyDiffuseAlphaMode : byte
{
    None = 0,
    Blend = 1,
    Mask = 2,
    Emissive = 3,
    Default = 4,
}

/// <summary>
/// SL's legacy "Blinn-Phong" material — the 2013 materials system — as an engine- and
/// protocol-neutral value.
///
/// <para>Distinct from the glTF PBR material (<see cref="PbrMaterialData"/>): a face carries an
/// id for each system independently, and this is the older one. It adds a normal map and a
/// specular map on top of the face's diffuse texture, each with its OWN placement — a material's
/// maps are not required to line up with the diffuse texture's repeat/offset/rotation, so they
/// cannot be folded into the face's UV transform.</para>
///
/// <para>Fetched from the region's <c>RenderMaterials</c> capability rather than as an asset;
/// see the conversion in SLNG.Net, which is where LibreMetaverse's <c>LegacyMaterial</c> stops.
/// Every float here is already decoded from the wire's integer-scaled-by-10000 form
/// (llmaterial.cpp:61).</para>
/// </summary>
/// <param name="Id">The material id, as referenced by a face's legacy material id.</param>
/// <param name="NormalMap">Normal map texture, or empty for none.</param>
/// <param name="NormalOffset">Normal map UV offset.</param>
/// <param name="NormalRepeat">Normal map UV repeat.</param>
/// <param name="NormalRotation">Normal map UV rotation, radians.</param>
/// <param name="SpecularMap">Specular map texture, or empty for none.</param>
/// <param name="SpecularOffset">Specular map UV offset.</param>
/// <param name="SpecularRepeat">Specular map UV repeat.</param>
/// <param name="SpecularRotation">Specular map UV rotation, radians.</param>
/// <param name="SpecularColor">Specular tint (RGBA, 0-1).</param>
/// <param name="SpecularExponent">SL "glossiness", 0-255. The viewer's default is 0.2*255 = 51
/// (DEFAULT_SPECULAR_LIGHT_EXPONENT, llmaterial.h).</param>
/// <param name="EnvironmentIntensity">SL "environment", 0-255; the viewer defaults it to 0.</param>
/// <param name="AlphaMaskCutoff">Cutoff for <see cref="LegacyDiffuseAlphaMode.Mask"/>, 0-255.</param>
/// <param name="DiffuseAlphaMode">How to treat the diffuse texture's alpha.</param>
public readonly record struct LegacyMaterialData(
    Guid Id,
    Guid NormalMap,
    Vector2 NormalOffset,
    Vector2 NormalRepeat,
    float NormalRotation,
    Guid SpecularMap,
    Vector2 SpecularOffset,
    Vector2 SpecularRepeat,
    float SpecularRotation,
    Vector4 SpecularColor,
    byte SpecularExponent,
    byte EnvironmentIntensity,
    byte AlphaMaskCutoff,
    LegacyDiffuseAlphaMode DiffuseAlphaMode)
{
    /// <summary>True when this material actually adds something to render. A material id that
    /// resolves to neither map still carries alpha-mode and specular-colour information, but
    /// nothing that needs a texture fetch.</summary>
    public bool HasMaps => NormalMap != Guid.Empty || SpecularMap != Guid.Empty;
}
