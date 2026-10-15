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
            ObjectRenderer.ApplyPbrFactors(matNoOverride, pbr, null, Godot.Colors.White);

            Godot.Color noOvAlbedo = (Godot.Color)matNoOverride.GetShaderParameter(PrimShaderFamily.AlbedoColor);
            float noOvMetallic = (float)matNoOverride.GetShaderParameter(PrimShaderFamily.MetallicFactor);
            float noOvRoughness = (float)matNoOverride.GetShaderParameter(PrimShaderFamily.RoughnessFactor);
            bool noOvEmission = (bool)matNoOverride.GetShaderParameter(PrimShaderFamily.EmissionEnabled);
            var noOvUvB = matNoOverride.GetShaderParameter(PrimShaderFamily.PbrUvB);
            bool noOvUvBUnset = noOvUvB.VariantType == Variant.Type.Nil || ((Godot.Vector4)noOvUvB).Y <= 0.5f;

            if (MathF.Abs(noOvAlbedo.R - 1f) > Tol || MathF.Abs(noOvMetallic - 0.5f) > Tol || MathF.Abs(noOvRoughness - 0.5f) > Tol || noOvEmission || !noOvUvBUnset || matNoOverride.Shader != PrimShaderFamily.Opaque)
                return new Check(Name, false, "no override test failed");

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
            ObjectRenderer.ApplyPbrFactors(matFactors, effFactors, ovFactors, Godot.Colors.White);

            Godot.Color fAlbedo = (Godot.Color)matFactors.GetShaderParameter(PrimShaderFamily.AlbedoColor);
            float fRoughness = (float)matFactors.GetShaderParameter(PrimShaderFamily.RoughnessFactor);
            float fMetallic = (float)matFactors.GetShaderParameter(PrimShaderFamily.MetallicFactor);
            bool fEmission = (bool)matFactors.GetShaderParameter(PrimShaderFamily.EmissionEnabled);
            Godot.Color fEmissionColor = (Godot.Color)matFactors.GetShaderParameter(PrimShaderFamily.EmissionColor);

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
            ObjectRenderer.ApplyPbrFactors(matMask, effMask, ovMask, Godot.Colors.White);

            float scissorThreshold = (float)matMask.GetShaderParameter(PrimShaderFamily.AlphaScissorThreshold);
            if (matMask.Shader != PrimShaderFamily.Scissor || MathF.Abs(scissorThreshold - 0.4f) > Tol)
                return new Check(Name, false, $"mask test failed: shader={matMask.Shader}, cutoff={scissorThreshold}");

            // 4. BLEND: shader is Blend. Double-sided: the shader is double-sided twin
            var ovBlend2Sided = new GltfMaterialOverride
            {
                AlphaMode = GltfAlphaMode.Blend,
                DoubleSided = true
            };
            var effBlend2Sided = PbrOverrideMerge.Apply(pbr, ovBlend2Sided);
            var matBlend2Sided = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
            ObjectRenderer.ApplyPbrFactors(matBlend2Sided, effBlend2Sided, ovBlend2Sided, Godot.Colors.White);

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
            ObjectRenderer.ApplyPbrFactors(matTransform, effTransform, ovTransform, Godot.Colors.White);

            Godot.Vector4 uvA = (Godot.Vector4)matTransform.GetShaderParameter(PrimShaderFamily.PbrUvA);
            Godot.Vector4 uvB = (Godot.Vector4)matTransform.GetShaderParameter(PrimShaderFamily.PbrUvB);
            if (MathF.Abs(uvA.X - 2f) > Tol || MathF.Abs(uvA.Y - 3f) > Tol || MathF.Abs(uvA.Z - 0.25f) > Tol || MathF.Abs(uvA.W - (-0.5f)) > Tol ||
                MathF.Abs(uvB.X - 0.5f) > Tol || MathF.Abs(uvB.Y - 1f) > Tol)
                return new Check(Name, false, $"transform test failed: uvA={uvA}, uvB={uvB}");

            // 6. Equal in, equal out: MaterialFingerprint
            var matEq1 = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
            var matEq2 = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
            ObjectRenderer.ApplyPbrFactors(matEq1, effTransform, ovTransform, Godot.Colors.White);
            ObjectRenderer.ApplyPbrFactors(matEq2, effTransform, ovTransform, Godot.Colors.White);

            string fp1 = MaterialFingerprint.Of(matEq1);
            string fp2 = MaterialFingerprint.Of(matEq2);
            if (fp1 != fp2)
                return new Check(Name, false, "materials with equal overrides produced different fingerprints");

            var ovDiff = ovTransform with { Roughness = 0.123f };
            var effDiff = PbrOverrideMerge.Apply(pbr, ovDiff);
            var matDiff = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
            ObjectRenderer.ApplyPbrFactors(matDiff, effDiff, ovDiff, Godot.Colors.White);
            string fpDiff = MaterialFingerprint.Of(matDiff);
            if (fp1 == fpDiff)
                return new Check(Name, false, "materials with different roughness produced identical fingerprints");

            return new Check(Name, true, "factors, alpha, 2-sided, transform, and fingerprints match spec");
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
