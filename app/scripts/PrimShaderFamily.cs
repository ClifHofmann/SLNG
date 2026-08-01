using System;
using System.Threading;
using Godot;

namespace SLNG.App;

/// <summary>
/// The three compiled variants of SLNG's world-surface shader (FEAT-RENDER-01, ADR 0002) plus
/// the uniform names their callers set.
///
/// There are three shaders rather than one with a "transparency" uniform because
/// <c>render_mode</c> is COMPILE-TIME in Godot: whether a face writes ALPHA, and whether it
/// alpha-scissors, decides which pass the engine puts it in and cannot be branched at runtime.
/// StandardMaterial3D solves the same problem the same way — it silently compiles a variant per
/// feature combination. Choosing a variant here therefore means assigning
/// <see cref="ShaderMaterial.Shader"/>, which is exactly what the old code expressed as
/// <c>material.Transparency = ...</c>.
///
/// The uniform names are cached as <see cref="StringName"/>s: these are set per face on
/// potentially thousands of prims, and a raw string would allocate and re-hash a StringName on
/// every single call.
///
/// TEXTURE UNIFORMS COME IN PAIRS. Setting <c>albedo_texture</c> without also setting
/// <c>has_albedo_texture</c> is not an error — the shader keeps sampling its default (white/
/// black) texture and the face renders as if the texture never arrived. There is no warning for
/// this, so always set the flag alongside the sampler.
/// </summary>
public static class PrimShaderFamily
{
    private const string OpaquePath = "res://materials/prim/prim_opaque.gdshader";
    private const string ScissorPath = "res://materials/prim/prim_scissor.gdshader";
    private const string BlendPath = "res://materials/prim/prim_blend.gdshader";

    // Materials are built on worker threads (see ObjectRenderer.BuildFaceMaterialAsync), so the
    // first touch of any of these could race. ExecutionAndPublication guarantees exactly one
    // load. Preload() additionally pulls them in from the main thread during Initialize so the
    // load — and the shader compile it triggers — doesn't land inside the first frame's material
    // build.
    private static readonly Lazy<Shader> _opaque = MakeLazy(OpaquePath);
    private static readonly Lazy<Shader> _scissor = MakeLazy(ScissorPath);
    private static readonly Lazy<Shader> _blend = MakeLazy(BlendPath);

    private static Lazy<Shader> MakeLazy(string path) =>
        new(() => GD.Load<Shader>(path), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Fully opaque, back-face culled. Never writes ALPHA, so it stays in the opaque
    /// pass. Replaces <c>TransparencyEnum.Disabled</c>.</summary>
    public static Shader Opaque => _opaque.Value;

    /// <summary>Binary alpha cutout (foliage, fences). Writes ALPHA_SCISSOR_THRESHOLD, which
    /// keeps it in the opaque pass. Replaces <c>TransparencyEnum.AlphaScissor</c>; pair it with
    /// <see cref="AlphaScissorThreshold"/>.</summary>
    public static Shader Scissor => _scissor.Value;

    /// <summary>True alpha blending (glass, soft edges, translucent tints). Replaces
    /// <c>TransparencyEnum.Alpha</c>.</summary>
    public static Shader Blend => _blend.Value;

    /// <summary>Loads all three from the main thread. Call once during renderer setup.</summary>
    public static void Preload()
    {
        _ = Opaque;
        _ = Scissor;
        _ = Blend;
    }

    // --- Uniform names (see app/materials/prim/prim_common.gdshaderinc) ---------------------

    public static readonly StringName AlbedoColor = "albedo_color";
    public static readonly StringName AlbedoTexture = "albedo_texture";
    public static readonly StringName HasAlbedoTexture = "has_albedo_texture";

    public static readonly StringName MetallicFactor = "metallic_factor";
    public static readonly StringName RoughnessFactor = "roughness_factor";

    public static readonly StringName EmissionColor = "emission_color";
    public static readonly StringName EmissionEnabled = "emission_enabled";
    public static readonly StringName EmissionTexture = "emission_texture";
    public static readonly StringName HasEmissionTexture = "has_emission_texture";

    public static readonly StringName NormalTexture = "normal_texture";
    public static readonly StringName HasNormalTexture = "has_normal_texture";
    public static readonly StringName NormalScale = "normal_scale";

    public static readonly StringName OrmTexture = "orm_texture";
    public static readonly StringName HasOrmTexture = "has_orm_texture";

    public static readonly StringName UvScale = "uv_scale";
    public static readonly StringName UvOffset = "uv_offset";
    public static readonly StringName UvRotation = "uv_rotation";

    /// <summary>Only meaningful on <see cref="Scissor"/>.</summary>
    public static readonly StringName AlphaScissorThreshold = "alpha_scissor_threshold";
}
