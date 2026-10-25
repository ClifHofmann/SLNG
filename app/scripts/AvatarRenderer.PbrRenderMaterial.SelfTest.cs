using System;
using System.Collections.Generic;
using Godot;
using SLNG.Assets;
using SLNG.Core;

namespace SLNG.App;

public partial class AvatarRenderer
{
    /// <summary>
    /// BUG-RENDER-49: a worn rigged mesh whose face names a glTF render material. Its legacy
    /// TextureEntry is the transparent texture at alpha 0 -- what a PBR garment can show a legacy viewer
    /// -- and still: the geometry is kept, the material samples the PBR base colour with its factors and
    /// MASK cutoff, the outfit merge's facts carry that texture, and two faces that differ only in their
    /// material get different merge keys. A BLEND material at base alpha 0 hides the face, as the viewer
    /// does; a face whose material is not to be had is exactly its legacy self -- geometry and material --
    /// so a TE alpha 0 hides it (the v0.27.83 hair regression). No asset service: the
    /// materials come from <see cref="PbrMaterialsForSelfTest"/>.
    /// </summary>
    internal (bool Passed, string Detail) SelfTestPbrRenderMaterial()
    {
        var problems = new List<string>();
        void Expect(bool ok, string what) { if (!ok) problems.Add(what); }
        static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-4f;

        var prior = PbrMaterialsForSelfTest;
        try
        {
            var maskId = Guid.NewGuid();
            var opaqueId = Guid.NewGuid();
            var hiddenId = Guid.NewGuid();
            var maskTexture = Guid.NewGuid();
            var opaqueTexture = Guid.NewGuid();
            PbrMaterialData Material(Guid baseColor, System.Numerics.Vector4 factor, PbrAlphaMode mode, float cutoff) =>
                new(baseColor, Guid.Empty, Guid.Empty, Guid.Empty, factor, 0f, 1f, System.Numerics.Vector3.Zero, mode, cutoff);
            PbrMaterialsForSelfTest = new Dictionary<Guid, PbrMaterialData>
            {
                [maskId] = Material(maskTexture, new System.Numerics.Vector4(0.8f, 0.6f, 0.4f, 1f), PbrAlphaMode.Mask, 0.35f),
                [opaqueId] = Material(opaqueTexture, System.Numerics.Vector4.One, PbrAlphaMode.Opaque, 0.5f),
                [hiddenId] = Material(maskTexture, new System.Numerics.Vector4(1f, 1f, 1f, 0f), PbrAlphaMode.Blend, 0.5f),
            };

            var pbrFace = new FaceTexture(FaceTexture.TransparentTextureId, maskId, Guid.Empty,
                new System.Numerics.Vector4(1f, 1f, 1f, 0f), 4f, 4f, 0f, 0f, 0f, Fullbright: true);
            var opaqueFace = pbrFace with { RenderMaterialId = opaqueId };

            // The worn rigged mesh keeps both faces' triangles: their materials are loaded and draw,
            // whatever the transparent TE says. A face whose material is NOT to be had (the v0.27.83
            // regression: every fetch failed) is its legacy self -- TE alpha 0, no triangles -- and so
            // is one whose loaded material is BLEND at base alpha 0.
            var unavailable = pbrFace with { RenderMaterialId = Guid.NewGuid() };
            var switchedOff = pbrFace with { RenderMaterialId = hiddenId };
            var mesh = TestMesh(new[] { "mPelvis" }, new[] { System.Numerics.Matrix4x4.Identity }, faces: 4, x: 0f);
            var prepared = PrepareRiggedMesh(mesh, new[] { 0 }, new[] { pbrFace, opaqueFace, unavailable, switchedOff }, default,
                drawsNothing: FaceDrawsNothing);
            Expect(string.Join(',', prepared.Geometry.FaceIndices()) == "0,1",
                $"geometry for the two drawing PBR faces only (got faces {string.Join(',', prepared.Geometry.FaceIndices())})");

            var meshId = Guid.NewGuid();
            var material = BuildFaceMaterialAsync(pbrFace, null, meshId, 0).GetAwaiter().GetResult();
            var facts = FactsOf(material);
            Expect(!facts.Hidden, "the PBR face is drawn");
            Expect(material.Shader == PrimShaderFamily.Select(PrimShaderFamily.Kind.Scissor, PrimShaderFamily.Surface.Avatar),
                "MASK selects the avatar scissor variant");
            Expect(Near((float)material.GetShaderParameter(PrimShaderFamily.AlphaScissorThreshold), 0.35f), "the material's alpha cutoff");
            // The shader's view of the factor: the uniform is source_color, decoded from sRGB (BUG-RENDER-49 follow-up).
            var albedo = ((Color)material.GetShaderParameter(PrimShaderFamily.AlbedoColor)).SrgbToLinear();
            Expect(Near(albedo.R, 0.8f) && Near(albedo.G, 0.6f) && Near(albedo.B, 0.4f) && Near(albedo.A, 1f),
                $"albedo is the base colour factor, not the TE colour (got {albedo})");
            Expect(!(bool)material.GetShaderParameter(PrimShaderFamily.Fullbright), "TE fullbright is ignored");
            Expect(((Godot.Vector4)material.GetShaderParameter(PrimShaderFamily.PbrUvB)).Y > 0.5f, "placed by the KHR transform");
            Expect(facts.Texture == maskTexture && facts.Kind == PrimShaderFamily.Kind.Scissor,
                $"the merge facts carry the base colour texture (got {facts.Texture}, {facts.Kind})");

            var opaque = BuildFaceMaterialAsync(opaqueFace, null, meshId, 1).GetAwaiter().GetResult();
            var opaqueFacts = FactsOf(opaque);
            Expect(opaqueFacts.Texture == opaqueTexture && opaqueFacts.Kind == PrimShaderFamily.Kind.Opaque,
                $"the second material's own texture and kind (got {opaqueFacts.Texture}, {opaqueFacts.Kind})");
            var key = new OutfitMaterialKey(pbrFace, facts.Texture, facts.Kind, facts.Threshold);
            var opaqueKey = new OutfitMaterialKey(opaqueFace, opaqueFacts.Texture, opaqueFacts.Kind, opaqueFacts.Threshold);
            Expect(!key.Equals(opaqueKey), "two PBR materials never merge into one surface");

            var hidden = BuildFaceMaterialAsync(pbrFace with { RenderMaterialId = hiddenId }, null, meshId, 0).GetAwaiter().GetResult();
            Expect(FactsOf(hidden).Hidden, "a BLEND material at base alpha 0 is not drawn (llvovolume.cpp:6017-6022)");

            var missing = BuildFaceMaterialAsync(unavailable, null, meshId, 2).GetAwaiter().GetResult();
            Expect(FactsOf(missing).Hidden, "PBR face, material unavailable, TE alpha 0: hidden, as before BUG-RENDER-49");

            var unavailableVisible = unavailable with { TextureId = Guid.NewGuid(), Color = System.Numerics.Vector4.One };
            var legacyFallback = BuildFaceMaterialAsync(unavailableVisible, null, meshId, 2).GetAwaiter().GetResult();
            Expect(!FactsOf(legacyFallback).Hidden && FactsOf(legacyFallback).Texture == unavailableVisible.TextureId,
                "PBR face, material unavailable, TE visible: its legacy texture");
            Expect(IsPbrBuiltWithout(unavailable.RenderMaterialId), "a face decided without its material is remembered");

            // The material turns up later: the worn item naming it is found and built again.
            var priorWorld = _world;
            var world = new SLNG.Core.ECS.World();
            _world = world;
            Guid wornId = Guid.Empty;
            try
            {
                var wornEntity = world.GetOrCreateEntity(1UL, 4242);
                var wornPrim = new SLNG.Core.Components.PrimitiveComponent(System.Numerics.Vector3.One, profileCurve: 0)
                {
                    Faces = new[] { opaqueFace, unavailable },
                };
                wornEntity.SetComponent(wornPrim);
                wornId = wornEntity.Id;
                _attachmentToAvatar[wornId] = Guid.NewGuid();

                Expect(WornItemsNaming(unavailable.RenderMaterialId).Contains(wornId), "the worn item naming the late material is found");
                Expect(WornItemsNaming(Guid.NewGuid()).Count == 0, "... and no other");
                Expect(RebuildForPbrMaterial(unavailable.RenderMaterialId) == 1, "... and asked to build again");
                OnPbrMaterialLoaded(unavailable.RenderMaterialId);
                Expect(!IsPbrBuiltWithout(unavailable.RenderMaterialId), "the arrival is consumed once");
            }
            finally
            {
                _attachmentToAvatar.Remove(wornId);
                _world = priorWorld;
            }
        }
        finally
        {
            PbrMaterialsForSelfTest = prior;
        }

        return problems.Count == 0
            ? (true, "geometry by the loaded material, by the TE when unavailable; base colour, factors, MASK cutoff, KHR placement; merge facts and keys per material; BLEND alpha 0 and unavailable+TE alpha 0 hidden; a late material rebuilds the item")
            : (false, string.Join("; ", problems));
    }
}
