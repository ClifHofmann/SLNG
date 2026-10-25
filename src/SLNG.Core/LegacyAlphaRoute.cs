namespace SLNG.Core;

/// <summary>BUG-RENDER-50: which pass a face goes to once its legacy material's diffuse alpha mode has
/// been read (<see cref="LegacyMaterialAlpha.Route"/>).</summary>
public enum LegacyAlphaRoute
{
    /// <summary>The material says nothing (<see cref="LegacyDiffuseAlphaMode.Default"/>): the texture's
    /// own alpha decides, as for a face without a material.</summary>
    Pixels,

    /// <summary>Drawn opaque; the texture's alpha is ignored.</summary>
    Opaque,

    /// <summary>Alpha-tested at the material's cutoff.</summary>
    Mask,

    /// <summary>Alpha-blended.</summary>
    Blend,
}
