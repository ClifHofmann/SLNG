using System;

namespace SLNG.Core;

/// <summary>
/// BUG-RENDER-50: what a legacy (Blinn-Phong) material's diffuse alpha mode does to a face, for faces
/// that carry no glTF material. Ported from <c>LLPipeline::getPoolTypeFromTE</c> (pipeline.cpp:1781-1820)
/// and the material branch of <c>LLVolumeGeometryManager::registerFace</c> (llvovolume.cpp:6766-6790),
/// which apply to rigged faces exactly as to prims:
///
/// <list type="bullet">
/// <item>BLEND (1) goes to the alpha pool.</item>
/// <item>NONE (0) and EMISSIVE (3) never go to the alpha pool on account of the texture -- "alpha mode set
/// to none, never go to alpha pool" -- only a translucent face colour sends them there.</item>
/// <item>MASK (2) is drawn in the opaque material pass with the material's cutoff (out of 255), again
/// unless the face colour is translucent, which makes the face BLEND. The shader discards below the cutoff
/// minus half an 8-bit step, "comparing floats cast from 8-bit values, produces acne right at the 8-bit
/// transition points" (class3/deferred/materialF.glsl:253-261): a texel exactly AT the cutoff is kept.</item>
/// <item>DEFAULT is not a mode a material is sent with; the texture decides, as before.</item>
/// </list>
/// </summary>
public static class LegacyMaterialAlpha
{
    /// <summary>The route for a face with a legacy material of <paramref name="mode"/> and
    /// <paramref name="alphaMaskCutoff"/>, whose TextureEntry colour is translucent when
    /// <paramref name="tintTranslucent"/>. The cutoff (0..1) is meaningful for
    /// <see cref="LegacyAlphaRoute.Mask"/> only.</summary>
    public static (LegacyAlphaRoute Route, float Cutoff) Route(LegacyDiffuseAlphaMode mode, byte alphaMaskCutoff, bool tintTranslucent) =>
        mode switch
        {
            LegacyDiffuseAlphaMode.Blend => (LegacyAlphaRoute.Blend, 0f),
            LegacyDiffuseAlphaMode.None or LegacyDiffuseAlphaMode.Emissive =>
                (tintTranslucent ? LegacyAlphaRoute.Blend : LegacyAlphaRoute.Opaque, 0f),
            LegacyDiffuseAlphaMode.Mask =>
                tintTranslucent ? (LegacyAlphaRoute.Blend, 0f) : (LegacyAlphaRoute.Mask, MaskThreshold(alphaMaskCutoff)),
            _ => (LegacyAlphaRoute.Pixels, 0f),
        };

    /// <summary>Half an 8-bit step, the viewer's bias below the cutoff (materialF.glsl:257).</summary>
    public const float HalfStep = 1f / 512f;

    /// <summary>The alpha below which a MASK face is discarded: the cutoff out of 255, less
    /// <see cref="HalfStep"/>, never negative (cutoff 0 discards nothing).</summary>
    public static float MaskThreshold(byte alphaMaskCutoff) => Math.Max(0f, alphaMaskCutoff / 255f - HalfStep);
}
