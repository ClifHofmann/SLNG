using System;
using System.Collections.Generic;
using Godot;
using SLNG.Assets;
using SLNG.Core;

namespace SLNG.App;

public partial class AvatarRenderer
{
    /// <summary>
    /// BUG-AVATAR-16: a worn rigged face whose legacy (Blinn-Phong) material names a normal and/or a specular map
    /// ends up with those maps bound, the specular scalars and each map's own placement -- as a prim face does,
    /// and as the viewer draws both (rigged faces go through LLDrawPoolMaterials too). A face whose material has
    /// no maps, a face with no material, a glTF face, a Bakes-on-Mesh face's bake and a HUD are as they were.
    /// No asset service: the textures come from <see cref="MapTexturesForSelfTest"/>.
    /// </summary>
    internal (bool Passed, string Detail) SelfTestLegacyWornMaps()
    {
        var problems = new List<string>();
        void Expect(bool ok, string what) { if (!ok) problems.Add(what); }
        static bool Bound(ShaderMaterial m, StringName flag)
        {
            var v = m.GetShaderParameter(flag);
            return v.VariantType != Variant.Type.Nil && v.AsBool();
        }
        static ImageTexture Texture() => ImageTexture.CreateFromImage(Image.CreateEmpty(4, 4, false, Image.Format.Rgba8));

        var priorLegacy = LegacyMaterialsForSelfTest;
        var priorMaps = MapTexturesForSelfTest;
        var priorPbr = PbrMaterialsForSelfTest;
        try
        {
            var normalId = Guid.NewGuid();
            var specularId = Guid.NewGuid();
            var normalTexture = Texture();
            var specularTexture = Texture();
            MapTexturesForSelfTest = new Dictionary<Guid, ImageTexture> { [normalId] = normalTexture, [specularId] = specularTexture };

            LegacyMaterialData Legacy(Guid normal, Guid specular, LegacyDiffuseAlphaMode mode) => new(Guid.NewGuid(),
                normal, new System.Numerics.Vector2(0.25f, 0.5f), new System.Numerics.Vector2(4f, 2f), 1.5f,
                specular, System.Numerics.Vector2.Zero, new System.Numerics.Vector2(8f, 1f), 0.5f,
                new System.Numerics.Vector4(1f, 0.8f, 0.2f, 1f), 200, 51, 0, mode);
            var both = Legacy(normalId, specularId, LegacyDiffuseAlphaMode.Blend);
            var onlyNormal = Legacy(normalId, Guid.Empty, LegacyDiffuseAlphaMode.None);
            var onlySpecular = Legacy(Guid.Empty, specularId, LegacyDiffuseAlphaMode.None);
            var plain = Legacy(Guid.Empty, Guid.Empty, LegacyDiffuseAlphaMode.None);
            LegacyMaterialsForSelfTest = new Dictionary<Guid, LegacyMaterialData>
            {
                [both.Id] = both, [onlyNormal.Id] = onlyNormal, [onlySpecular.Id] = onlySpecular, [plain.Id] = plain,
            };

            FaceTexture Face(Guid legacy) => new(Guid.NewGuid(), Guid.Empty, legacy, System.Numerics.Vector4.One, 1f, 1f, 0f, 0f, 0f);
            var meshId = Guid.NewGuid();
            ShaderMaterial Build(FaceTexture face, AvatarVisual? wearer = null, PrimShaderFamily.Surface surface = PrimShaderFamily.Surface.Avatar) =>
                BuildFaceMaterialAsync(face, wearer, meshId, 0, surface).GetAwaiter().GetResult();

            // Normal and specular map: both bound, with the material's own numbers.
            var m = Build(Face(both.Id));
            Expect(Bound(m, PrimShaderFamily.HasNormalTexture) && m.GetShaderParameter(PrimShaderFamily.NormalTexture).Obj == normalTexture,
                "the normal map is bound");
            Expect(Bound(m, PrimShaderFamily.HasSpecularTexture) && m.GetShaderParameter(PrimShaderFamily.SpecularTexture).Obj == specularTexture,
                "the specular map is bound");
            Expect(Math.Abs((float)m.GetShaderParameter(PrimShaderFamily.SpecularGlossiness) - 200f / 255f) < 1e-5f
                   && Math.Abs((float)m.GetShaderParameter(PrimShaderFamily.SpecularEnvironment) - 51f / 255f) < 1e-5f,
                "specular exponent and environment intensity are the material's, over 255");
            var tint = (Godot.Vector3)m.GetShaderParameter(PrimShaderFamily.SpecularTint);
            Expect(Math.Abs(tint.Y - 0.8f) < 1e-5f && Math.Abs(tint.Z - 0.2f) < 1e-5f, $"specular colour is the material's (got {tint})");
            Expect(Bound(m, PrimShaderFamily.HasNormalUv)
                   && (Godot.Vector2)m.GetShaderParameter(PrimShaderFamily.NormalUvScale) == new Godot.Vector2(4f, 2f)
                   && (Godot.Vector2)m.GetShaderParameter(PrimShaderFamily.SpecularUvScale) == new Godot.Vector2(8f, 1f),
                "each map is placed by its own repeat");
            Expect(Math.Abs((float)m.GetShaderParameter(PrimShaderFamily.NormalUvRotation) - 1.5f) < 1e-5f
                   && Math.Abs((float)m.GetShaderParameter(PrimShaderFamily.SpecularUvRotation) - 0.5f) < 1e-5f,
                "each map is rotated by its own angle");
            // The alpha mode is still the material's: BLEND stays blended while it gains maps.
            Expect(FactsOf(m).Kind == PrimShaderFamily.Kind.Blend, $"BLEND stays Blend (got {FactsOf(m).Kind})");

            // BUG-AVATAR-17: a face with a specular map is lit by the viewer's own highlight (the twin of its base
            // variant); one without stays on the base variant.
            var blendBase = PrimShaderFamily.Select(PrimShaderFamily.Kind.Blend, PrimShaderFamily.Surface.Avatar);
            Expect(ReferenceEquals(m.Shader, PrimShaderFamily.ViewerSpecTwin(blendBase)) && !ReferenceEquals(m.Shader, blendBase),
                "a BLEND face with a specular map is on the avatar viewer-specular twin");
            Expect(ReferenceEquals(PrimShaderFamily.BaseVariant(m.Shader), blendBase),
                "the twin answers as its base variant (sorting, depth pass, merge probe)");

            // One map only.
            var n = Build(Face(onlyNormal.Id));
            Expect(Bound(n, PrimShaderFamily.HasNormalTexture) && !Bound(n, PrimShaderFamily.HasSpecularTexture),
                "a normal-only material binds just the normal map");
            Expect(ReferenceEquals(n.Shader, PrimShaderFamily.Select(PrimShaderFamily.Kind.Opaque, PrimShaderFamily.Surface.Avatar)),
                "a normal-only face keeps the base avatar variant (the viewer's highlight needs a specular map)");
            var s = Build(Face(onlySpecular.Id));
            Expect(!Bound(s, PrimShaderFamily.HasNormalTexture) && Bound(s, PrimShaderFamily.HasSpecularTexture),
                "a specular-only material binds just the specular map");
            Expect(ReferenceEquals(s.Shader, PrimShaderFamily.ViewerSpecTwin(PrimShaderFamily.Select(PrimShaderFamily.Kind.Opaque, PrimShaderFamily.Surface.Avatar))),
                "a specular-only face is on the opaque avatar viewer-specular twin");

            // Nothing to add: unchanged.
            var p = Build(Face(plain.Id));
            Expect(!Bound(p, PrimShaderFamily.HasNormalTexture) && !Bound(p, PrimShaderFamily.HasSpecularTexture)
                   && p.GetShaderParameter(PrimShaderFamily.SpecularGlossiness).VariantType == Variant.Type.Nil,
                "a legacy material without maps is left exactly as before");
            var none = Build(Face(Guid.Empty));
            Expect(!Bound(none, PrimShaderFamily.HasNormalTexture) && !Bound(none, PrimShaderFamily.HasSpecularTexture),
                "a face without a material has no maps");

            // Bakes-on-Mesh: the bake is still the texture, and the material's maps come with it.
            var wearer = new AvatarVisual { AgentId = Guid.NewGuid(), EntityId = Guid.NewGuid() };
            var bake = Guid.NewGuid();
            wearer.LoadedTextures[10] = bake;
            var bomFace = new FaceTexture(new Guid("24daea5f-0539-cfcf-047f-fbc40b2786ba"), Guid.Empty, onlySpecular.Id,
                System.Numerics.Vector4.One, 1f, 1f, 0f, 0f, 0f);
            var bom = Build(bomFace, wearer);
            Expect(FactsOf(bom).BakeChannel == 10 && FactsOf(bom).Texture == bake, "a BoM face still samples its bake");
            Expect(Bound(bom, PrimShaderFamily.HasSpecularTexture), "a BoM face gets its material's specular map");

            // A glTF face: its material replaces the legacy one outright (llvovolume.cpp:6005-6029).
            var pbrId = Guid.NewGuid();
            PbrMaterialsForSelfTest = new Dictionary<Guid, PbrMaterialData>
            {
                [pbrId] = new(Guid.NewGuid(), Guid.Empty, Guid.Empty, Guid.Empty, System.Numerics.Vector4.One, 0f, 1f,
                    System.Numerics.Vector3.Zero, PbrAlphaMode.Opaque, 0.5f),
            };
            var pbr = Build(Face(both.Id) with { RenderMaterialId = pbrId });
            Expect(!Bound(pbr, PrimShaderFamily.HasSpecularTexture) && !Bound(pbr, PrimShaderFamily.HasNormalTexture),
                "a glTF face ignores the legacy material's maps");

            // A HUD is unshaded: nothing to bind.
            var hud = Build(Face(both.Id), surface: PrimShaderFamily.Surface.Hud);
            Expect(!Bound(hud, PrimShaderFamily.HasSpecularTexture) && !Bound(hud, PrimShaderFamily.HasNormalTexture),
                "a HUD face binds no legacy maps");

            // The outfit merge must not fold two faces that differ only in their material into one surface.
            var kBoth = new OutfitMaterialKey(Face(both.Id), FactsOf(m).Texture, FactsOf(m).Kind, FactsOf(m).Threshold);
            var kPlain = new OutfitMaterialKey(Face(plain.Id), FactsOf(p).Texture, FactsOf(p).Kind, FactsOf(p).Threshold);
            Expect(!kBoth.Equals(kPlain), "faces with different legacy materials keep different merge keys");
        }
        finally
        {
            LegacyMaterialsForSelfTest = priorLegacy;
            MapTexturesForSelfTest = priorMaps;
            PbrMaterialsForSelfTest = priorPbr;
        }

        return problems.Count == 0
            ? (true, "a legacy material's normal and specular maps are bound with their own placement and specular scalars; none/plain/glTF/HUD unchanged; BoM keeps its bake")
            : (false, string.Join("; ", problems));
    }
}
