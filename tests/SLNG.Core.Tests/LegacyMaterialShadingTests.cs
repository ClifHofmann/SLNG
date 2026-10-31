using System;
using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-AVATAR-16: the numbers a legacy material hands the shader -- the same for a prim face and
/// a worn rigged face, because the viewer draws both through LLDrawPoolMaterials (lldrawpoolmaterials.cpp
/// :150-260: specular_color, environment_intensity and the maps are bound per draw-info, rigged or not).</summary>
public class LegacyMaterialShadingTests
{
    private static LegacyMaterialData Material(
        Guid? normal = null, Guid? specular = null, Vector4? specColor = null, byte exponent = 51, byte env = 0,
        Vector2? normalRepeat = null, Vector2? normalOffset = null, Vector2? specRepeat = null, Vector2? specOffset = null,
        float normalRotation = 0f, float specRotation = 0f) =>
        new(Guid.NewGuid(), normal ?? Guid.Empty, normalOffset ?? Vector2.Zero, normalRepeat ?? Vector2.One, normalRotation,
            specular ?? Guid.Empty, specOffset ?? Vector2.Zero, specRepeat ?? Vector2.One, specRotation,
            specColor ?? Vector4.One, exponent, env, 0, LegacyDiffuseAlphaMode.Blend);

    [Fact]
    public void Scalars_are_the_wire_bytes_over_255()
    {
        // llvovolume.cpp:5568-5573: colour and exponent come as bytes * (1/255), environment likewise.
        var s = LegacyMaterialShading.From(Material(specColor: new Vector4(1f, 0.5f, 0.25f, 1f), exponent: 255, env: 51));
        Assert.Equal(new Vector3(1f, 0.5f, 0.25f), s.SpecularTint);
        Assert.Equal(1f, s.Glossiness, 6);
        Assert.Equal(0.2f, s.Environment, 6);
    }

    [Fact]
    public void The_viewer_defaults_come_through_unchanged()
    {
        // llmaterial.h:55-57: SpecExp 0.2*255 = 51, EnvIntensity 0.
        var s = LegacyMaterialShading.From(Material());
        Assert.Equal(0.2f, s.Glossiness, 6);
        Assert.Equal(0f, s.Environment, 6);
    }

    [Fact]
    public void A_map_with_no_placement_is_centred_on_the_face()
    {
        var s = LegacyMaterialShading.From(Material(normal: Guid.NewGuid(), specular: Guid.NewGuid()));
        Assert.Equal(Vector2.One, s.Normal.Scale);
        Assert.Equal(Vector2.Zero, s.Normal.Offset);
        Assert.Equal(Vector2.One, s.Specular.Scale);
        Assert.Equal(Vector2.Zero, s.Specular.Offset);
    }

    [Fact]
    public void Placement_is_centred_and_the_V_offset_is_negated_for_the_flipped_meshes()
    {
        // The same fold the diffuse placement gets (AvatarRenderer.BuildFaceMaterialAsync, ObjectRenderer):
        // u' = (u-0.5)*repeat + 0.5 + off, and the V axis is flipped, so the V offset changes sign.
        var s = LegacyMaterialShading.From(Material(
            normal: Guid.NewGuid(), normalRepeat: new Vector2(4f, 2f), normalOffset: new Vector2(0.25f, 0.5f), normalRotation: 1.5f));
        Assert.Equal(new Vector2(4f, 2f), s.Normal.Scale);
        Assert.Equal(0.5f - 0.5f * 4f + 0.25f, s.Normal.Offset.X, 6);
        Assert.Equal(0.5f - 0.5f * 2f - 0.5f, s.Normal.Offset.Y, 6);
        Assert.Equal(1.5f, s.Normal.Rotation);
    }

    [Fact]
    public void The_specular_map_is_placed_by_its_own_numbers_not_the_normal_maps()
    {
        var s = LegacyMaterialShading.From(Material(
            normal: Guid.NewGuid(), specular: Guid.NewGuid(),
            normalRepeat: new Vector2(2f, 2f), specRepeat: new Vector2(8f, 1f), specOffset: new Vector2(0.1f, 0f), specRotation: 0.5f));
        Assert.Equal(new Vector2(2f, 2f), s.Normal.Scale);
        Assert.Equal(new Vector2(8f, 1f), s.Specular.Scale);
        Assert.Equal(0.5f - 0.5f * 8f + 0.1f, s.Specular.Offset.X, 6);
        Assert.Equal(0.5f, s.Specular.Rotation);
    }

    [Fact]
    public void Maps_to_fetch_lists_only_the_ones_the_material_names()
    {
        Assert.False(LegacyMaterialShading.From(Material()).HasMaps);
        Assert.True(LegacyMaterialShading.From(Material(normal: Guid.NewGuid())).HasMaps);
        Assert.True(LegacyMaterialShading.From(Material(specular: Guid.NewGuid())).HasMaps);
    }
}

/// <summary>BUG-AVATAR-17: why a worn face with a specular map must not take Godot's stock GGX lobe. Gloss 222 is
/// the "Dress Waifu" material (a98ce5ed, SpecExp 222): the viewer's lobe against the one Godot draws when the shader
/// feeds <c>slng_glossiness_to_roughness</c> into ROUGHNESS (Godot squares it into the GGX alpha).</summary>
public class LegacySpecularLobeTests
{
    private const float Gloss = 222f / 255f;

    // GGX D(theta) with alpha = roughness^2, the way Godot's forward renderer takes ROUGHNESS.
    private static float GodotD(float theta, float roughness)
    {
        float alpha = roughness * roughness;
        float c = MathF.Cos(theta);
        float d = c * c * (alpha * alpha - 1f) + 1f;
        return alpha * alpha / (MathF.PI * d * d);
    }

    private static float HalfWidthDegrees(Func<float, float> lobe)
    {
        float peak = lobe(0f);
        float t = 0f;
        while (lobe(t) > 0.5f * peak && t < 1f) t += 0.00005f;
        return t * 180f / MathF.PI;
    }

    [Fact]
    public void Godots_stock_lobe_is_far_narrower_and_stronger_than_the_viewers_at_gloss_222()
    {
        float roughness = LegacyShadeMirror.GlossinessToRoughness(Gloss);
        float viewerWidth = HalfWidthDegrees(t => LegacyShadeMirror.ViewerSpecularLut(MathF.Cos(t), Gloss));
        float godotWidth = HalfWidthDegrees(t => GodotD(t, roughness));
        float viewerPeak = LegacyShadeMirror.ViewerSpecularLut(1f, Gloss);
        float godotPeak = GodotD(0f, roughness);

        // The viewer's lobe is ~4 degrees wide (n = 279); Godot's is ~0.26 degrees: a pinpoint, not a sheen.
        Assert.InRange(viewerWidth, 3.5f, 4.5f);
        Assert.InRange(godotWidth, 0.2f, 0.35f);
        Assert.True(viewerWidth / godotWidth > 10f, $"widths {viewerWidth} vs {godotWidth} deg");
        Assert.True(godotPeak / viewerPeak > 300f, $"peaks {godotPeak} vs {viewerPeak}");
    }

    [Fact]
    public void The_normal_maps_alpha_widens_the_viewers_lobe_further()
    {
        // materialF.glsl:227: glossiness *= normal map alpha. Half the gloss is ~2x the width: the creases of a
        // satin spread the highlight instead of pinpointing it.
        float full = HalfWidthDegrees(t => LegacyShadeMirror.ViewerSpecularLut(MathF.Cos(t), Gloss));
        float half = HalfWidthDegrees(t => LegacyShadeMirror.ViewerSpecularLut(MathF.Cos(t), Gloss * 0.5f));
        Assert.True(half > 1.9f * full, $"{half} vs {full}");
    }
}
