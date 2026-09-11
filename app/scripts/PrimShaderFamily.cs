using System;
using System.Threading;
using Godot;

namespace SLNG.App;

/// <summary>
/// The compiled variants of SLNG's world-surface shader (FEAT-RENDER-01, ADR 0002) plus the
/// uniform names their callers set.
///
/// There are separate shaders rather than one with uniforms because <c>render_mode</c> is
/// COMPILE-TIME in Godot. Three things this family varies are all render_modes, so all three are
/// variant axes and none can be a parameter:
///
///   * whether a face writes ALPHA and whether it alpha-scissors (<see cref="Kind"/>) — it
///     decides which pass the engine puts the face in;
///   * whether it back-face culls;
///   * whether it is shaded at all.
///
/// The last two are not exposed as booleans. They are folded into <see cref="Surface"/>, named
/// after the three real call sites, so the variant set stays closed at 3 × 3 = 9 instead of
/// growing with every render_mode someone needs next. StandardMaterial3D solves the same problem
/// the same way — it silently compiles a variant per feature combination.
///
/// Choosing a variant therefore means assigning <see cref="ShaderMaterial.Shader"/>, which is
/// exactly what the old code expressed as <c>material.Transparency = ...</c>. Use
/// <see cref="Select"/>; the bare <see cref="Opaque"/>/<see cref="Scissor"/>/<see cref="Blend"/>
/// properties are the <see cref="Surface.WorldPrim"/> shorthand and mean the same thing.
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
    // BUG-RENDER-09: hashed alpha -- prim_scissor's depth behaviour with prim_blend's smooth
    // gradient. See prim_hash.gdshader for why the avatar needed a fourth Kind.
    private const string HashPath = "res://materials/prim/prim_hash.gdshader";
    // BUG-RENDER-16: blend for colour + an alpha depth-prepass, for dense high-frequency foliage.
    // See prim_blend_prepass.gdshader. WorldPrim only -- it is opt-in via --foliage-alpha=prepass.
    private const string BlendPrepassPath = "res://materials/prim/prim_blend_prepass.gdshader";
    // BUG-RENDER-16: prim_scissor + ALPHA_ANTIALIASING_EDGE (alpha-edge-blend). Opaque pass,
    // depth write, no sort -> no flicker, but a soft (non-dithered) silhouette. --foliage-alpha=edge.
    private const string ScissorEdgePath = "res://materials/prim/prim_scissor_edge.gdshader";
    // BUG-RENDER-16: blend for colour + depth_draw_always so overlapping foliage self-occludes by
    // depth and the coarse sort stops mattering. --foliage-alpha=blenddepth.
    private const string BlendDepthPath = "res://materials/prim/prim_blend_depth.gdshader";
    // BUG-RENDER-16: depth-only companion pass chained onto prim_blend as its next_pass -- writes
    // depth for fragments at or above core_alpha_threshold, nothing to colour. The reference
    // viewer's own alpha depth pass (lldrawpoolalpha.cpp:212-227). --foliage-alpha=blendcore.
    private const string DepthCorePath = "res://materials/prim/prim_depth_core.gdshader";
    // BUG-RENDER-16: draws nothing. Worn by a shared-mesh surface whose geometry now lives on its
    // own MeshInstance3D (ObjectRenderer.SplitSortedSurfaces).
    private const string HiddenPath = "res://materials/prim/prim_hidden.gdshader";

    // BUG-RENDER-06: the real viewer back-face culls WorldPrim faces by default, EXCEPT a face
    // whose GLTF material explicitly declares mDoubleSided (lldrawpool.cpp:839, :856) --
    // ObjectRenderer.cs already documented this exception but had no variant to route such a face
    // to, so every alpha-blended double-sided leaf card still culled like an ordinary one-sided
    // face, popping in and out of view as the camera crossed its facing (reported as the tree
    // canopy "flickering" while moving/zooming). Only WorldPrim needs these: Avatar and Hud are
    // already cull_disabled unconditionally (see Surface's own doc comment), so there is nothing
    // for a double-sided flag to change there.
    private const string OpaqueDoubleSidedPath = "res://materials/prim/prim_opaque_doublesided.gdshader";
    private const string ScissorDoubleSidedPath = "res://materials/prim/prim_scissor_doublesided.gdshader";
    private const string BlendDoubleSidedPath = "res://materials/prim/prim_blend_doublesided.gdshader";

    private const string OpaqueAvatarPath = "res://materials/prim/prim_opaque_avatar.gdshader";
    private const string ScissorAvatarPath = "res://materials/prim/prim_scissor_avatar.gdshader";
    private const string BlendAvatarPath = "res://materials/prim/prim_blend_avatar.gdshader";
    private const string HashAvatarPath = "res://materials/prim/prim_hash_avatar.gdshader";

    private const string OpaqueHudPath = "res://materials/prim/prim_opaque_hud.gdshader";
    private const string ScissorHudPath = "res://materials/prim/prim_scissor_hud.gdshader";
    private const string BlendHudPath = "res://materials/prim/prim_blend_hud.gdshader";

    // Materials are built on worker threads (see ObjectRenderer.BuildFaceMaterialAsync), so the
    // first touch of any of these could race. ExecutionAndPublication guarantees exactly one
    // load. Preload() additionally pulls them in from the main thread during Initialize so the
    // load — and the shader compile it triggers — doesn't land inside the first frame's material
    // build.
    private static readonly Lazy<Shader> _opaque = MakeLazy(OpaquePath);
    private static readonly Lazy<Shader> _scissor = MakeLazy(ScissorPath);
    private static readonly Lazy<Shader> _blend = MakeLazy(BlendPath);
    private static readonly Lazy<Shader> _hash = MakeLazy(HashPath);
    private static readonly Lazy<Shader> _blendPrepass = MakeLazy(BlendPrepassPath);
    private static readonly Lazy<Shader> _scissorEdge = MakeLazy(ScissorEdgePath);
    private static readonly Lazy<Shader> _blendDepth = MakeLazy(BlendDepthPath);
    private static readonly Lazy<Shader> _depthCore = MakeLazy(DepthCorePath);
    private static readonly Lazy<Shader> _hidden = MakeLazy(HiddenPath);

    private static readonly Lazy<Shader> _opaqueDoubleSided = MakeLazy(OpaqueDoubleSidedPath);
    private static readonly Lazy<Shader> _scissorDoubleSided = MakeLazy(ScissorDoubleSidedPath);
    private static readonly Lazy<Shader> _blendDoubleSided = MakeLazy(BlendDoubleSidedPath);

    private static readonly Lazy<Shader> _opaqueAvatar = MakeLazy(OpaqueAvatarPath);
    private static readonly Lazy<Shader> _scissorAvatar = MakeLazy(ScissorAvatarPath);
    private static readonly Lazy<Shader> _blendAvatar = MakeLazy(BlendAvatarPath);
    private static readonly Lazy<Shader> _hashAvatar = MakeLazy(HashAvatarPath);

    private static readonly Lazy<Shader> _opaqueHud = MakeLazy(OpaqueHudPath);
    private static readonly Lazy<Shader> _scissorHud = MakeLazy(ScissorHudPath);
    private static readonly Lazy<Shader> _blendHud = MakeLazy(BlendHudPath);

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

    /// <summary>Stochastic (hashed) alpha cutout. Writes depth exactly like <see cref="Scissor"/>,
    /// so it cannot sort wrong, but resolves a soft alpha gradient as a dither instead of a hard
    /// step. Pair it with <see cref="AlphaHashScale"/>.</summary>
    public static Shader Hash => _hash.Value;

    /// <summary>BUG-RENDER-16: true alpha blending for colour, plus <c>depth_prepass_alpha</c> so
    /// overlapping high-frequency foliage self-occludes by depth and Godot's coarse per-object
    /// transparent sort stops flickering it. Draws the geometry twice; WorldPrim only, opt-in via
    /// <c>--foliage-alpha=prepass</c>.</summary>
    public static Shader BlendPrepass => _blendPrepass.Value;

    /// <summary>BUG-RENDER-16: <see cref="Scissor"/> plus <c>ALPHA_ANTIALIASING_EDGE</c> — the
    /// silhouette resolves as a smooth coverage ramp instead of a staircase, with no dither
    /// noise. Still the opaque, depth-writing, no-sort pass, so it does not flicker. Pair it with
    /// <see cref="AlphaScissorThreshold"/> and <see cref="AlphaEdge"/>.</summary>
    public static Shader ScissorEdge => _scissorEdge.Value;

    /// <summary>BUG-RENDER-16: true alpha blending for colour, plus <c>depth_draw_always</c> so
    /// every fragment writes depth and overlapping foliage self-occludes instead of being sorted.
    /// No flicker; blade-on-blade overlap goes effectively opaque but the outer silhouette still
    /// blends. WorldPrim only, opt-in via <c>--foliage-alpha=blenddepth</c>.</summary>
    public static Shader BlendDepth => _blendDepth.Value;

    /// <summary>BUG-RENDER-16: the alpha DEPTH pass. Not a face shader in its own right but the
    /// <c>next_pass</c> of a <see cref="Blend"/> material: it writes depth for every fragment whose
    /// alpha reaches <see cref="CoreAlphaThreshold"/> and leaves the colour buffer untouched, and
    /// it runs at <see cref="DepthCoreRenderPriority"/> so every core is in the depth buffer before
    /// any blend pass is drawn. A soft fringe behind another blade's core is then rejected in EVERY
    /// draw order, which is what makes the coarse per-instance sort stop mattering. See the shader
    /// header for the viewer citations. WorldPrim only; attached by ObjectRenderer.</summary>
    public static Shader DepthCore => _depthCore.Value;

    /// <summary>BUG-RENDER-16: render priority of a <see cref="DepthCore"/> pass. Godot's alpha
    /// comparator orders by priority BEFORE depth (render_forward_clustered.h:722-726), so this
    /// bucket -- below water's -1 and the default 0 -- is drawn in full before any ordinary
    /// transparent surface, exactly like the viewer's depth pass over the whole alpha pool.</summary>
    public const int DepthCoreRenderPriority = -2;

    /// <summary>BUG-RENDER-16: a surface that draws nothing (every vertex collapsed, every fragment
    /// discarded). Godot has no per-surface visibility and world meshes are SHARED by geometry, so
    /// this is how a parent instance stops drawing a surface that has been moved onto its own
    /// child instance for per-surface transparent sorting. Opaque queue, no shadow.</summary>
    public static Shader Hidden => _hidden.Value;

    /// <summary>The transparency treatment of a face, i.e. which compile-time variant it needs.
    /// Named after the <c>StandardMaterial3D.TransparencyEnum</c> values it replaces so the
    /// migration reads one-to-one.</summary>
    public enum Kind
    {
        /// <summary>Opaque pass, never writes ALPHA.</summary>
        Opaque,
        /// <summary>Binary cutout; stays in the opaque pass via ALPHA_SCISSOR_THRESHOLD.</summary>
        Scissor,
        /// <summary>True alpha blending.</summary>
        Blend,
        /// <summary>Stochastic cutout; stays in the opaque pass via ALPHA_HASH_SCALE and keeps
        /// depth write, unlike <see cref="Blend"/>. BUG-RENDER-09.</summary>
        Hash,
    }

    /// <summary>
    /// Which surface a face belongs to. This is the SECOND compile-time axis, and it is deliberately
    /// named after the three real call sites rather than exposed as raw <c>render_mode</c> flags
    /// (cull, shading): Godot forces both to be compile-time, and enumerating the combinations that
    /// actually exist keeps the variant set closed instead of growing a shader per boolean pair.
    /// </summary>
    public enum Surface
    {
        /// <summary>World prims. <c>cull_back</c>, shaded -- the viewer's global default
        /// (llrender.cpp:863). Double-sided prims are what made solid objects look like glassy
        /// shells, so nothing else may be routed here by accident.</summary>
        WorldPrim,

        /// <summary>Avatar faces and worn mesh attachments. <c>cull_disabled</c>, shaded -- these
        /// ran on <c>CullMode.Disabled</c> under StandardMaterial3D and Phase 3 is required to be
        /// visually identical, so the cull mode came across unchanged rather than being
        /// "corrected" during the migration.</summary>
        Avatar,

        /// <summary>HUD attachments. <c>cull_disabled</c> AND <c>unshaded</c> -- the HUD
        /// SubViewport has its own World3D with no lights in it, so a shaded material renders
        /// black. Being unshaded also keeps HUDs out of the Phase 5 atmospherics seam by
        /// construction: fogging an overlay by its distance from the camera is meaningless.</summary>
        Hud,
    }

    /// <summary>Picks the compiled variant for a transparency treatment on a given surface.
    /// <paramref name="doubleSided"/> (BUG-RENDER-06) routes a <see cref="Surface.WorldPrim"/>
    /// face to its cull_disabled twin -- pass it ONLY from a face's own
    /// <c>PbrMaterialData.DoubleSided</c>, never as a default. It is a no-op for
    /// <see cref="Surface.Avatar"/>/<see cref="Surface.Hud"/>, which are already cull_disabled
    /// unconditionally (see <see cref="Surface"/>'s own doc comment) -- nothing for it to
    /// change there.</summary>
    public static Shader Select(Kind kind, Surface surface, bool doubleSided = false) => surface switch
    {
        Surface.Avatar => kind switch
        {
            Kind.Scissor => _scissorAvatar.Value,
            Kind.Blend => _blendAvatar.Value,
            Kind.Hash => _hashAvatar.Value,
            _ => _opaqueAvatar.Value,
        },
        // No _hud/_doublesided hash twin: Kind.Hash exists for BUG-RENDER-09's avatar bake faces
        // and nothing else routes to it yet. A HUD is unshaded overlay art where a dither would
        // just look like noise, so it falls back to Scissor -- the behaviour it had before this
        // Kind existed -- rather than silently picking up a variant nobody has looked at.
        Surface.Hud => kind switch
        {
            Kind.Scissor or Kind.Hash => _scissorHud.Value,
            Kind.Blend => _blendHud.Value,
            _ => _opaqueHud.Value,
        },
        _ when doubleSided => kind switch
        {
            Kind.Scissor or Kind.Hash => _scissorDoubleSided.Value,
            Kind.Blend => _blendDoubleSided.Value,
            _ => _opaqueDoubleSided.Value,
        },
        _ => kind switch
        {
            Kind.Scissor => _scissor.Value,
            Kind.Blend => _blend.Value,
            Kind.Hash => _hash.Value,
            _ => _opaque.Value,
        },
    };

    /// <summary>Loads every variant from the main thread. Call once during renderer setup.</summary>
    public static void Preload()
    {
        _ = Opaque;
        _ = Scissor;
        _ = Blend;
        _ = Hash;
        _ = _blendPrepass.Value;
        _ = _scissorEdge.Value;
        _ = _blendDepth.Value;
        _ = _depthCore.Value;
        _ = _hidden.Value;
        _ = _opaqueAvatar.Value;
        _ = _scissorAvatar.Value;
        _ = _blendAvatar.Value;
        _ = _hashAvatar.Value;
        _ = _opaqueHud.Value;
        _ = _scissorHud.Value;
        _ = _blendHud.Value;
        _ = _opaqueDoubleSided.Value;
        _ = _scissorDoubleSided.Value;
        _ = _blendDoubleSided.Value;
    }

    // --- Uniform names (see app/materials/prim/prim_common.gdshaderinc) ---------------------

    public static readonly StringName AlbedoColor = "albedo_color";
    public static readonly StringName AlbedoTexture = "albedo_texture";
    public static readonly StringName HasAlbedoTexture = "has_albedo_texture";

    public static readonly StringName MetallicFactor = "metallic_factor";
    public static readonly StringName RoughnessFactor = "roughness_factor";

    public static readonly StringName EmissionColor = "emission_color";
    public static readonly StringName EmissionEnabled = "emission_enabled";

    /// <summary>FEAT-RENDER-06: SL's per-face fullbright flag. When set, the face renders unlit
    /// (albedo routed through emission).</summary>
    public static readonly StringName Fullbright = "fullbright";
    public static readonly StringName EmissionTexture = "emission_texture";
    public static readonly StringName HasEmissionTexture = "has_emission_texture";

    public static readonly StringName NormalTexture = "normal_texture";
    public static readonly StringName HasNormalTexture = "has_normal_texture";
    public static readonly StringName NormalScale = "normal_scale";

    /// <summary>A LEGACY Blinn-Phong material places its normal map independently of the diffuse
    /// texture (NormRepeat/NormOffset/NormRotation are the material's own fields), so the normal
    /// map gets its own coordinate. Set <see cref="HasNormalUv"/> alongside them -- with it false
    /// the normal map keeps sharing the diffuse placement, which is what the glTF path wants.</summary>
    public static readonly StringName NormalUvScale = "normal_uv_scale";
    public static readonly StringName NormalUvOffset = "normal_uv_offset";
    public static readonly StringName NormalUvRotation = "normal_uv_rotation";
    public static readonly StringName HasNormalUv = "has_normal_uv";

    /// <summary>A legacy material's specular map and the two scalars that go with it. Godot has
    /// no per-texel specular colour, so the map drives roughness by luminance and
    /// <see cref="SpecularEnvironment"/> folds SL's EnvIntensity into metalness -- see the shader
    /// for why that is an approximation and which part of it is faithful.</summary>
    public static readonly StringName SpecularTexture = "specular_texture";
    public static readonly StringName HasSpecularTexture = "has_specular_texture";
    public static readonly StringName SpecularTint = "specular_tint";
    public static readonly StringName SpecularGlossiness = "specular_glossiness";
    public static readonly StringName SpecularEnvironment = "specular_environment";
    public static readonly StringName SpecularUvScale = "specular_uv_scale";
    public static readonly StringName SpecularUvOffset = "specular_uv_offset";
    public static readonly StringName SpecularUvRotation = "specular_uv_rotation";

    /// <summary>The build tool's legacy Shiny level, as glossiness (none 0, low 0.25, medium 0.5,
    /// high 0.75). A face with a specular map ignores it, exactly as the viewer does.</summary>
    public static readonly StringName LegacyShininess = "legacy_shininess";

    public static readonly StringName OrmTexture = "orm_texture";
    public static readonly StringName HasOrmTexture = "has_orm_texture";

    public static readonly StringName UvScale = "uv_scale";
    public static readonly StringName UvOffset = "uv_offset";
    public static readonly StringName UvRotation = "uv_rotation";

    /// <summary>SL TexGen, NORMALISED for the shader: 0 = default, 1 = planar. The wire value
    /// for planar is 2 (see FaceTexture.TexGen); it is collapsed to a 0/1 flag here so the
    /// shader branch stays a plain comparison and spherical/cylindrical fall back to default.
    /// That fallback is viewer PARITY, not a shortcut: TEX_GEN_SPHERICAL and TEX_GEN_CYLINDRICAL
    /// occur in the whole viewer source only in the enum declaration (lltextureentry.h:80-81) --
    /// no render branch, no build-floater UI -- so the real viewer draws those faces as default
    /// too. LSL cannot even set them (PRIM_TEXGEN exposes default and planar only).</summary>
    /// <summary>Diagnostic V nudge, applied after the full UV transform. See the shader.</summary>

    public static readonly StringName UvTexGen = "uv_texgen";

    /// <summary>The prim's SL-axis size in metres, needed because planar UVs are a function of
    /// vertex position in world units.</summary>
    public static readonly StringName PrimScale = "prim_scale";

    /// <summary>Only meaningful on <see cref="Scissor"/> and <see cref="ScissorEdge"/>.</summary>
    public static readonly StringName AlphaScissorThreshold = "alpha_scissor_threshold";

    /// <summary>BUG-RENDER-16: width of the softened coverage band on <see cref="ScissorEdge"/>.</summary>
    public static readonly StringName AlphaEdge = "alpha_edge";

    /// <summary>Only meaningful on <see cref="Hash"/>. 1.0 is Godot's own default noise scale;
    /// the value only tunes the dither's grain, it does not decide the cutoff.</summary>
    public static readonly StringName AlphaHashScale = "alpha_hash_scale";

    /// <summary>BUG-RENDER-16: only meaningful on <see cref="DepthCore"/>. Alpha at or above this
    /// writes depth; below it the fragment is discarded from the depth pass (and still blends in
    /// the colour pass). The viewer's value is 0.33 (lldrawpoolalpha.cpp:217).</summary>
    public static readonly StringName CoreAlphaThreshold = "core_alpha_threshold";
}
