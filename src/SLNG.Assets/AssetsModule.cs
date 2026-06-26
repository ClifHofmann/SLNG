namespace SLNG.Assets;

/// <summary>
/// Placeholder marker for the asset-pipeline layer. Owns off-thread decode of
/// JPEG2000, LLMesh and glTF materials plus the RAM/disk/GPU cache. Real
/// implementation begins in task M2-1 (mesh) and M2-2 (JPEG2000 decode).
/// </summary>
public static class AssetsModule
{
    /// <summary>Layer identifier, used in diagnostics until real types land.</summary>
    public const string Layer = "assets";
}
