using Godot;
using SLNG.Assets;
using SLNG.Core;
using System;
using SysVector2 = System.Numerics.Vector2;
using SysVector3 = System.Numerics.Vector3;
using SysVector4 = System.Numerics.Vector4;

namespace SLNG.App;

public partial class SelfTest
{
    private static Check CheckPbrOverrideApply(SceneTree tree)
    {
        const string Name = "pbr override apply";
        try
        {
            var pbr = new PbrMaterialData(
                BaseColorTextureId: Guid.NewGuid(),
                NormalTextureId: Guid.Empty,
                MetallicRoughnessTextureId: Guid.Empty,
                EmissiveTextureId: Guid.Empty,
                BaseColorFactor: new SysVector4(1f, 1f, 1f, 1f),
                MetallicFactor: 0.5f,
                RoughnessFactor: 0.5f,
                EmissiveFactor: new SysVector3(0f, 0f, 0f),
                AlphaMode: PbrAlphaMode.Opaque,
                AlphaCutoff: 0.5f,
                DoubleSided: false
            );

            const float Tol = 1e-4f;

            // 1. No override
            var matNoOverride = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
            ObjectRenderer.ApplyPbrFactors(matNoOverride, pbr, null);

            // BUG-RENDER-49 follow-up: colour factors are handed over sRGB-encoded (source_color uniforms) and
            // read here the way the shader sees them, decoded back to the linear factor.
            Godot.Color noOvAlbedo = ((Godot.Color)matNoOverride.GetShaderParameter(PrimShaderFamily.AlbedoColor)).SrgbToLinear();
            float noOvMetallic = (float)matNoOverride.GetShaderParameter(PrimShaderFamily.MetallicFactor);
            float noOvRoughness = (float)matNoOverride.GetShaderParameter(PrimShaderFamily.RoughnessFactor);
            bool noOvEmission = (bool)matNoOverride.GetShaderParameter(PrimShaderFamily.EmissionEnabled);
            // BUG-RENDER-49: a PBR face is placed by its material's KHR transform, never the TextureEntry
            // repeats -- with no override that is the identity, switched ON (pbr_uv_b.y = 1).
            var noOvUvA = (Godot.Vector4)matNoOverride.GetShaderParameter(PrimShaderFamily.PbrUvA);
            var noOvUvB = (Godot.Vector4)matNoOverride.GetShaderParameter(PrimShaderFamily.PbrUvB);
            bool noOvIdentityKhr = noOvUvA == new Godot.Vector4(1f, 1f, 0f, 0f) && noOvUvB.X == 0f && noOvUvB.Y > 0.5f;

            if (MathF.Abs(noOvAlbedo.R - 1f) > Tol || MathF.Abs(noOvMetallic - 0.5f) > Tol || MathF.Abs(noOvRoughness - 0.5f) > Tol || noOvEmission || !noOvIdentityKhr || matNoOverride.Shader != PrimShaderFamily.Opaque)
                return new Check(Name, false, $"no override test failed (pbr_uv_a={noOvUvA}, pbr_uv_b={noOvUvB})");

            // 2. Factors: colour (0.8,0.1,0.1,1), roughness 0.35, metallic 0, emissive (0.2,0,0)
            var ovFactors = new GltfMaterialOverride
            {
                BaseColor = new SysVector4(0.8f, 0.1f, 0.1f, 1f),
                Roughness = 0.35f,
                Metallic = 0f,
                EmissiveColor = new SysVector3(0.2f, 0f, 0f)
            };
            var effFactors = PbrOverrideMerge.Apply(pbr, ovFactors);
            var matFactors = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
            ObjectRenderer.ApplyPbrFactors(matFactors, effFactors, ovFactors);

            Godot.Color fAlbedo = ((Godot.Color)matFactors.GetShaderParameter(PrimShaderFamily.AlbedoColor)).SrgbToLinear();
            float fRoughness = (float)matFactors.GetShaderParameter(PrimShaderFamily.RoughnessFactor);
            float fMetallic = (float)matFactors.GetShaderParameter(PrimShaderFamily.MetallicFactor);
            bool fEmission = (bool)matFactors.GetShaderParameter(PrimShaderFamily.EmissionEnabled);
            Godot.Color fEmissionColor = ((Godot.Color)matFactors.GetShaderParameter(PrimShaderFamily.EmissionColor)).SrgbToLinear();

            if (MathF.Abs(fAlbedo.R - 0.8f) > Tol || MathF.Abs(fAlbedo.G - 0.1f) > Tol || MathF.Abs(fAlbedo.B - 0.1f) > Tol ||
                MathF.Abs(fRoughness - 0.35f) > Tol || MathF.Abs(fMetallic - 0f) > Tol || !fEmission ||
                MathF.Abs(fEmissionColor.R - 0.2f) > Tol)
                return new Check(Name, false, "factors test failed");

            // 3. Alpha MASK, cutoff 0.4: shader is Scissor and alpha_scissor_threshold == 0.4
            var ovMask = new GltfMaterialOverride
            {
                AlphaMode = GltfAlphaMode.Mask,
                AlphaCutoff = 0.4f
            };
            var effMask = PbrOverrideMerge.Apply(pbr, ovMask);
            var matMask = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
            ObjectRenderer.ApplyPbrFactors(matMask, effMask, ovMask);

            float scissorThreshold = (float)matMask.GetShaderParameter(PrimShaderFamily.AlphaScissorThreshold);
            if (matMask.Shader != PrimShaderFamily.Scissor || MathF.Abs(scissorThreshold - 0.4f) > Tol)
                return new Check(Name, false, $"mask test failed: shader={matMask.Shader} path={matMask.Shader?.ResourcePath} id={matMask.Shader?.GetInstanceId()} scissor={PrimShaderFamily.Scissor} spath={PrimShaderFamily.Scissor.ResourcePath} sid={PrimShaderFamily.Scissor.GetInstanceId()} opaque={PrimShaderFamily.Opaque} ref={ReferenceEquals(matMask.Shader, PrimShaderFamily.Scissor)} cutoff={scissorThreshold}");

            // 4. BLEND: shader is Blend. Double-sided: the shader is double-sided twin
            var ovBlend2Sided = new GltfMaterialOverride
            {
                AlphaMode = GltfAlphaMode.Blend,
                DoubleSided = true
            };
            var effBlend2Sided = PbrOverrideMerge.Apply(pbr, ovBlend2Sided);
            var matBlend2Sided = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
            ObjectRenderer.ApplyPbrFactors(matBlend2Sided, effBlend2Sided, ovBlend2Sided);

            var expectedTwin = PrimShaderFamily.Select(PrimShaderFamily.Kind.Blend, PrimShaderFamily.Surface.WorldPrim, doubleSided: true);
            if (matBlend2Sided.Shader != expectedTwin)
                return new Check(Name, false, $"blend 2-sided test failed: shader={matBlend2Sided.Shader}");

            // 5. Transform: scale (2,3), offset (0.25,-0.5), rotation 0.5
            var ovTransform = new GltfMaterialOverride
            {
                BaseColorTransform = new GltfTextureTransform(
                    new SysVector2(0.25f, -0.5f),
                    new SysVector2(2f, 3f),
                    0.5f)
            };
            var effTransform = PbrOverrideMerge.Apply(pbr, ovTransform);
            var matTransform = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
            ObjectRenderer.ApplyPbrFactors(matTransform, effTransform, ovTransform);

            Godot.Vector4 uvA = (Godot.Vector4)matTransform.GetShaderParameter(PrimShaderFamily.PbrUvA);
            Godot.Vector4 uvB = (Godot.Vector4)matTransform.GetShaderParameter(PrimShaderFamily.PbrUvB);
            if (MathF.Abs(uvA.X - 2f) > Tol || MathF.Abs(uvA.Y - 3f) > Tol || MathF.Abs(uvA.Z - 0.25f) > Tol || MathF.Abs(uvA.W - (-0.5f)) > Tol ||
                MathF.Abs(uvB.X - 0.5f) > Tol || MathF.Abs(uvB.Y - 1f) > Tol)
                return new Check(Name, false, $"transform test failed: uvA={uvA}, uvB={uvB}");

            // 6. Equal in, equal out: MaterialFingerprint
            var matEq1 = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
            var matEq2 = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
            ObjectRenderer.ApplyPbrFactors(matEq1, effTransform, ovTransform);
            ObjectRenderer.ApplyPbrFactors(matEq2, effTransform, ovTransform);

            string fp1 = MaterialFingerprint.Of(matEq1);
            string fp2 = MaterialFingerprint.Of(matEq2);
            if (fp1 != fp2)
                return new Check(Name, false, "materials with equal overrides produced different fingerprints");

            var ovDiff = ovTransform with { Roughness = 0.123f };
            var effDiff = PbrOverrideMerge.Apply(pbr, ovDiff);
            var matDiff = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
            ObjectRenderer.ApplyPbrFactors(matDiff, effDiff, ovDiff);
            string fpDiff = MaterialFingerprint.Of(matDiff);
            if (fp1 == fpDiff)
                return new Check(Name, false, "materials with different roughness produced identical fingerprints");

            // 7. BUG-RENDER-49 follow-up: a linear factor reaches the shader's math as the same linear value. The
            // uniform is source_color (decoded from sRGB), so what is stored must be the sRGB encoding of it, not
            // the factor itself -- that was the double linearisation. Alpha is never converted. A glTF face also
            // gets the dielectric specular flag.
            var tinted = pbr with
            {
                BaseColorFactor = new SysVector4(1f, 0.6f, 0.7f, 0.5f),
                EmissiveFactor = new SysVector3(0.25f, 0.5f, 0f),
            };
            var matTinted = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
            ObjectRenderer.ApplyPbrFactors(matTinted, tinted, null);
            var stored = (Godot.Color)matTinted.GetShaderParameter(PrimShaderFamily.AlbedoColor);
            var seen = stored.SrgbToLinear();
            var seenEmission = ((Godot.Color)matTinted.GetShaderParameter(PrimShaderFamily.EmissionColor)).SrgbToLinear();
            if (MathF.Abs(seen.R - 1f) > Tol || MathF.Abs(seen.G - 0.6f) > Tol || MathF.Abs(seen.B - 0.7f) > Tol
                || MathF.Abs(stored.A - 0.5f) > Tol || MathF.Abs(stored.G - 0.6f) < 0.1f
                || MathF.Abs(seenEmission.R - 0.25f) > Tol || MathF.Abs(seenEmission.G - 0.5f) > Tol)
                return new Check(Name, false, $"linear factor test failed: stored {stored}, the shader sees {seen}, emission {seenEmission}");
            if (!(bool)matTinted.GetShaderParameter(PrimShaderFamily.PbrMaterial))
                return new Check(Name, false, "a glTF face is not flagged for the dielectric specular");
            var legacy = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
            if (legacy.GetShaderParameter(PrimShaderFamily.PbrMaterial).VariantType == Variant.Type.Bool
                && (bool)legacy.GetShaderParameter(PrimShaderFamily.PbrMaterial))
                return new Check(Name, false, "a legacy face is flagged as glTF");

            return new Check(Name, true, "factors (linear into the shader, alpha untouched), alpha, 2-sided, transform, dielectric flag and fingerprints match spec");
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
