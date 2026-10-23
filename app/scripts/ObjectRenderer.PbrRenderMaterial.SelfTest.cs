using System;
using System.Collections.Generic;
using Godot;
using SLNG.Assets;
using SLNG.Core;

namespace SLNG.App;

public partial class ObjectRenderer
{
    /// <summary>
    /// BUG-RENDER-49: a world-prim face that names a glTF render material builds a PBR material -- the
    /// material's base colour texture, colour, alpha mode and KHR placement -- and none of the legacy
    /// TextureEntry's texture, colour, alpha, fullbright or Shiny (llvovolume.cpp:6649-6650,
    /// llface.cpp:1395-1400). The same face without its material is the legacy face, unchanged. No asset
    /// service: the material comes from <see cref="PbrMaterialsForSelfTest"/>.
    /// </summary>
    internal (bool Passed, string Detail) SelfTestPbrRenderMaterial()
    {
        var problems = new List<string>();
        void Expect(bool ok, string what) { if (!ok) problems.Add(what); }
        static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-4f;

        var prior = PbrMaterialsForSelfTest;
        try
        {
            var materialId = Guid.NewGuid();
            var baseColorTexture = Guid.NewGuid();
            var legacyTexture = Guid.NewGuid();
            PbrMaterialsForSelfTest = new Dictionary<Guid, PbrMaterialData>
            {
                [materialId] = new PbrMaterialData(
                    baseColorTexture, Guid.Empty, Guid.Empty, Guid.Empty,
                    new System.Numerics.Vector4(0.8f, 0.6f, 0.4f, 1f), 0.3f, 0.7f, System.Numerics.Vector3.Zero,
                    PbrAlphaMode.Mask, 0.35f, DoubleSided: false),
            };

            // Everything the legacy face says is deliberately something the PBR face must NOT show: a
            // translucent grey tint, 4x repeats, fullbright, Shiny high.
            var face = new FaceTexture(legacyTexture, materialId, Guid.Empty,
                new System.Numerics.Vector4(0.2f, 0.2f, 0.2f, 0.5f), 4f, 4f, 0.1f, 0.2f, 0f,
                Fullbright: true, Shiny: 3);

            var (pbrMaterial, pbrUsed) = DoBuildFaceMaterialAsync(face, System.Numerics.Vector3.One, 1000f, 1f).GetAwaiter().GetResult();
            Expect(pbrUsed.Contains(baseColorTexture), "the PBR face samples its material's base colour");
            Expect(!pbrUsed.Contains(legacyTexture), "the PBR face does not sample its legacy texture");
            // The shader's view of the factor: the uniform is source_color, decoded from sRGB (BUG-RENDER-49 follow-up).
            var albedo = ((Color)pbrMaterial.GetShaderParameter(PrimShaderFamily.AlbedoColor)).SrgbToLinear();
            Expect(Near(albedo.R, 0.8f) && Near(albedo.G, 0.6f) && Near(albedo.B, 0.4f) && Near(albedo.A, 1f),
                $"albedo is the base colour factor, not tinted by the TextureEntry (got {albedo})");
            Expect(pbrMaterial.Shader == PrimShaderFamily.Scissor, "MASK selects the scissor variant (the TE alpha 0.5 does not select blend)");
            Expect(Near((float)pbrMaterial.GetShaderParameter(PrimShaderFamily.AlphaScissorThreshold), 0.35f), "the material's alpha cutoff");
            Expect(Near((float)pbrMaterial.GetShaderParameter(PrimShaderFamily.MetallicFactor), 0.3f), "the material's metallic factor");
            Expect(!(bool)pbrMaterial.GetShaderParameter(PrimShaderFamily.Fullbright), "TE fullbright is ignored on a PBR face");
            Expect(Near((float)pbrMaterial.GetShaderParameter(PrimShaderFamily.LegacyShininess), 0f), "TE Shiny is ignored on a PBR face");
            var uvA = (Godot.Vector4)pbrMaterial.GetShaderParameter(PrimShaderFamily.PbrUvA);
            var uvB = (Godot.Vector4)pbrMaterial.GetShaderParameter(PrimShaderFamily.PbrUvB);
            Expect(uvA == new Godot.Vector4(1f, 1f, 0f, 0f) && uvB.Y > 0.5f,
                $"placed by the identity KHR transform, not the 4x TE repeats (pbr_uv_a={uvA}, pbr_uv_b={uvB})");

            // Same face, its material not available: the legacy face exactly as before.
            var legacyFace = face with { RenderMaterialId = Guid.NewGuid() };
            var (legacyMaterial, legacyUsed) = DoBuildFaceMaterialAsync(legacyFace, System.Numerics.Vector3.One, 1000f, 1f).GetAwaiter().GetResult();
            Expect(legacyUsed.Contains(legacyTexture), "without its material the face falls back to its legacy texture");
            var legacyAlbedo = (Color)legacyMaterial.GetShaderParameter(PrimShaderFamily.AlbedoColor);
            Expect(Near(legacyAlbedo.R, 0.2f) && Near(legacyAlbedo.A, 0.5f), $"... and its legacy colour (got {legacyAlbedo})");
            Expect(legacyMaterial.Shader == PrimShaderFamily.Blend, "... and its translucent tint selects blend");
            Expect((bool)legacyMaterial.GetShaderParameter(PrimShaderFamily.Fullbright), "... and its fullbright");

            // FEAT-PERF-20: two faces that differ only in their render material never share a material.
            Expect(!PrimMaterialKey.Create(face, System.Numerics.Vector3.One).Equals(PrimMaterialKey.Create(legacyFace, System.Numerics.Vector3.One)),
                "different render materials give different shared-material keys");

            // A material that arrives AFTER its face was built (and shared) without it: the stale shared
            // material is retired, the face holding it can still release it, and the next build of the
            // same face is the PBR one.
            bool priorShare = RenderConfig.ShareMaterials;
            RenderConfig.ShareMaterials = true;
            MeshInstance3D? holderMesh = null;
            try
            {
                var lateId = Guid.NewGuid();
                var lateFace = face with { RenderMaterialId = lateId };
                var scale = System.Numerics.Vector3.One;
                var (fallback, fallbackUsed) = BuildFaceMaterialAsync(lateFace, scale, 1000f, 1f).GetAwaiter().GetResult();
                var key = PrimMaterialKey.Create(lateFace, scale);
                Expect(IsPbrBuiltWithout(lateId), "a face built without its material is remembered");
                Expect(fallbackUsed.Contains(legacyTexture), "... and was built as its legacy self");

                holderMesh = new MeshInstance3D();
                var holder = new VisualState { EntityId = Guid.NewGuid(), MeshInstance = holderMesh, SurfaceMaterialKeys = new[] { key }, SurfaceDetached = new byte[1] };
                _visuals[holder.EntityId] = holder;
                PbrMaterialsForSelfTest[lateId] = PbrMaterialsForSelfTest[materialId];

                Expect(RetireSharedPbrMaterials(lateId) == 1 && !_materialCache.TryGet(key, out _), "the stale shared material is retired");
                var heldKey = holder.SurfaceMaterialKeys[0];
                Expect(!heldKey.Equals(key) && _materialCache.TryGet(heldKey, out var retiredEntry) && ReferenceEquals(retiredEntry.Material, fallback),
                    "the face holding it keeps a key its release still finds");

                var (fresh, freshUsed) = BuildFaceMaterialAsync(lateFace, scale, 1000f, 1f).GetAwaiter().GetResult();
                Expect(!ReferenceEquals(fresh, fallback) && freshUsed.Contains(baseColorTexture), "the next build of the same face is the PBR material");
                Expect(_materialCache.Release(heldKey), "releasing the retired key frees the stale material");
                _materialCache.Release(key);
                _visuals.Remove(holder.EntityId);
            }
            finally
            {
                RenderConfig.ShareMaterials = priorShare;
                holderMesh?.Free();
            }
        }
        finally
        {
            PbrMaterialsForSelfTest = prior;
        }

        return problems.Count == 0
            ? (true, "base colour texture, factors, MASK cutoff, KHR placement; TE colour/alpha/fullbright/Shiny ignored; legacy fallback unchanged; a late material retires the stale shared one")
            : (false, string.Join("; ", problems));
    }
}
