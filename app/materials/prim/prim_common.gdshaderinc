// Shared body of SLNG's world-surface shader family (FEAT-RENDER-01, ADR 0002).
//
// Every prim face variant includes this. The variants differ ONLY in `render_mode` and in
// how they treat ALPHA, because render_mode is compile-time in Godot -- exactly the same
// reason StandardMaterial3D compiles its own variant set internally.
//
// This is a spatial shader, so it is NOT a PBR reimplementation: it assigns ALBEDO / ALPHA /
// NORMAL_MAP / METALLIC / ROUGHNESS / EMISSION and Godot's clustered lighting, shadows, GI
// and post-processing all still apply unchanged.

// --- Albedo -----------------------------------------------------------------------------
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_albedo_texture = false;

// --- PBR --------------------------------------------------------------------------------
uniform float metallic_factor : hint_range(0.0, 1.0) = 0.0;
uniform float roughness_factor : hint_range(0.0, 1.0) = 1.0;
uniform vec3 emission_color : source_color = vec3(0.0);
uniform bool emission_enabled = false;

// SL's per-face "fullbright" flag (LLTextureEntry::getFullbright, LLDrawPoolFullbright). The
// face ignores scene lighting and shows its texture at full colour. Reproduced at the end of
// slng_shade by routing the albedo into EMISSION and zeroing ALBEDO -- see there. Distinct from
// glow and from glTF emissive.
uniform bool fullbright = false;

uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_normal_texture = false;
uniform float normal_scale : hint_range(-16.0, 16.0) = 1.0;

uniform sampler2D orm_texture : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_orm_texture = false;

uniform sampler2D emission_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_emission_texture = false;

// --- UV transform -----------------------------------------------------------------------
// SL's per-face texture placement. Applied in the VERTEX stage: per-vertex is cheaper than
// per-fragment and the gap widens with resolution (ADR 0002).
//
// uv_scale/uv_offset reproduce SL's CENTRED convention, the same one ObjectRenderer builds
// today: u' = (u - 0.5) * repeat + 0.5 + offset. The caller folds the 0.5 term into
// uv_offset, so here it is a plain multiply-add and stays identical to the
// StandardMaterial3D behaviour it replaces.
//
// uv_rotation is SL's per-face texture rotation, in radians, passed through UNMODIFIED even
// though the meshes are built flipV. The viewer rotates in bottom-origin space with [c, s; -s, c]
// (llface.cpp:734-756); our centred V is the negation of the viewer's, and mapping back through
// that negation flips both sine signs into [c, -s; s, c] -- what this file applies below. The two
// inversions cancel, so the caller passes the raw angle.
//
// The order below is the viewer's: CENTRE, ROTATE, SCALE, OFFSET. Applying scale before rotation
// would shear the texture on any non-square repeat instead of rotating it.
//
// Rotating per-vertex loses nothing: a rotated UV is still an affine function of position, so
// interpolating it across the triangle gives exactly the per-fragment result.
uniform vec2 uv_scale = vec2(1.0);
uniform vec2 uv_offset = vec2(0.0);
uniform float uv_rotation = 0.0;

// A LEGACY Blinn-Phong material places its normal map INDEPENDENTLY of the diffuse texture --
// NormRepeat/NormOffset/NormRotation are their own fields on the material, not the face's
// (llmaterial.cpp:35-40). So the normal map needs its own transformed coordinate rather than
// reusing UV, and it is carried as a varying because the transform belongs in the vertex stage
// for the same reason the diffuse one does.
//
// Defaults are the identity AND has_normal_uv is false, so a face without a legacy material
// behaves exactly as before: the normal map, if any, keeps sharing the diffuse placement.
uniform vec2 normal_uv_scale = vec2(1.0);
uniform vec2 normal_uv_offset = vec2(0.0);
uniform float normal_uv_rotation = 0.0;
uniform bool has_normal_uv = false;

varying vec2 v_normal_uv;

// A legacy material's SPECULAR map, and the two scalars that go with it. The viewer's model
// (class3/deferred/materialF.glsl, getSpecular() + main()):
//
//     #ifdef HAS_SPECULAR_MAP
//         spec  = texture(specularMap); spec.rgb *= specular_color.rgb
//     #else
//         spec  = vec4(specular_color.rgb, 1.0)                // NO map: white tint, alpha 1
//     #endif
//     glossiness = specular_color.a                            // SpecExp, ONE scalar per material
//     env        = env_intensity * spec.a                      // reflection strength
//
// BUG-RENDER-21: note which of those the `#ifdef` actually covers. The MAP is optional; the two
// scalars are not. A material with no specular map still runs this same shader -- LLMaterial::
// getShaderMask() only sets its SPEC_BIT when getSpecularID() is non-null, and mask 0 (no normal
// map, no specular map) is a perfectly ordinary index into llvovolume.cpp's material pass table,
// landing on PASS_MATERIAL -- and it still gets `glossiness = specular_color.a` and
// `env = env_intensity * 1.0`. Gating these on the map dropped both silently, which is exactly
// the case the Build floater produces when a creator raises Environment Intensity without ever
// assigning one.
//
// Godot's metallic-roughness model has no per-texel specular COLOUR, so this is the one place in
// FEAT-RENDER-04 that is an approximation rather than a port, and it is deliberately a
// conservative one: the map's luminance drives how polished the surface is, and the material's
// glossiness sets how polished "fully lit" means. A greyscale spec map -- which is what almost
// all SL content ships -- is reproduced faithfully by that; a coloured one loses its tint.
uniform sampler2D specular_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_specular_texture = false;
// BUG-RENDER-23: the specular MAP is what selects the path, and BUG-RENDER-21's `has_specular_
// material` is gone with the premise that introduced it. That premise came from the shader half
// (getSpecular()'s `#else`, and getShaderMask()'s SPEC_BIT) and never checked where the values
// it reads are FILLED. llvovolume.cpp:5536-5557 fills them, and the gate there is the map:
//
//     float spec = alpha[shiny & TEM_SHINY_MASK];   // 0 / 0.25 / 0.5 / 0.75
//     specColor = vec4(spec, spec, spec, spec);
//     draw_info->mSpecColor = specColor; draw_info->mEnvIntensity = spec;   // <- from SHINY
//     ...
//     if (!mat->getSpecularID().isNull())          // <- ONLY with a map
//     {   specColor.mV[3] = mat->getSpecularLightExponent()/255; ...
//         draw_info->mEnvIntensity = mat->getEnvironmentIntensity()/255; }
//
// So a material WITHOUT a specular map contributes neither glossiness nor Environment Intensity:
// both come from the prim's Shiny level, exactly as for a face with no material at all. These
// three uniforms are therefore read only inside the `has_specular_texture` branch.
uniform vec3 specular_tint = vec3(1.0);
uniform float specular_glossiness = 0.2;   // SpecExp/255; the viewer's default is 0.2*255
uniform float specular_environment = 0.0;  // EnvIntensity/255

// LEGACY per-face shininess -- the build tool's Shiny, as glossiness already mapped through the
// viewer's SHININESS_TO_ALPHA table: none 0, low 0.25, medium 0.5, high 0.75 (llface.cpp:1420).
// The viewer stores it in the vertex alpha and only "if we don't have a specular map"; but the
// deferred material shader it routes a materialed face to never reads vertex alpha for shininess,
// so in practice a legacy MATERIAL -- with or without a map -- takes precedence over this. See the
// branch below. Zero means genuinely matte, and the viewer then skips its whole specular branch
// (`if (spec.a > 0.0)`, softenLightF.glsl:243).
uniform float legacy_shininess = 0.0;

uniform vec2 specular_uv_scale = vec2(1.0);
uniform vec2 specular_uv_offset = vec2(0.0);
uniform float specular_uv_rotation = 0.0;

varying vec2 v_specular_uv;

// --- TexGen -------------------------------------------------------------------------------
// SL's per-face texture GENERATION mode, which decides where the base UV comes from BEFORE the
// transform above runs. 0 = TEX_GEN_DEFAULT (the mesh's own UVs), 1 = TEX_GEN_PLANAR. The wire
// value for planar is 2, not 1 -- the C# side normalises it, see PrimShaderFamily.UvTexGen.
//
// prim_scale is the prim's SL-axis size in metres. Planar UVs are a function of vertex POSITION,
// and the viewer measures that position in metres, so the projection needs the size that
// ObjectRenderer applies as a Godot node scale rather than baking into the mesh. Passing it as a
// uniform (instead of reading MODEL_MATRIX) keeps meshes shareable between prims of the same
// shape and different sizes, which is the whole point of AssignSharedMesh, and avoids silently
// picking up an ancestor's scale from the node hierarchy.
uniform int uv_texgen = 0;
uniform vec3 prim_scale = vec3(1.0);

// SL's planar texture projection, ported from planarProjection (llface.cpp:88-120).
//
// The viewer picks one of four AXIS-ALIGNED binormals from the face normal's dominant axis,
// takes the tangent as their cross product, and projects the vertex position onto that pair:
//
//     binormal = |n.x| >= 0.5  ?  (0, sign(n.x), 0)  :  (n.y > 0 ? (-1,0,0) : (1,0,0))
//     tangent  = cross(binormal, normal)
//     s = 1.0 + (dot(binormal, p) * 2 - 0.5)
//     t =     -((dot(tangent,  p) * 2 - 0.5))
//
// The factor 2 is why a planar face tiles with prim size: p is in metres, so a 2m face spans 4
// units of texture. That size-independence is the entire reason builders use planar -- adjacent
// walls line up regardless of their individual dimensions.
//
// `center` is in planarProjection's signature but its body never reads it; it is not an omission
// here.
//
// Two coordinate conversions are folded in:
//  * AXES. ObjectRenderer.BuildArrayMesh stores SL (x,y,z) as Godot (x, z, -y), for positions and
//    normals alike, so SL space is recovered as (g.x, -g.z, g.y).
//  * V ORIGIN. planarProjection returns SL's bottom-origin t, while every mesh here is built
//    flipV so the rest of this file works in top-origin v = 1 - t. Converting here rather than
//    downstream is what lets slng_transform_uv stay identical for both texgen modes -- its
//    rotation-sign derivation only ever assumed v = 1 - t, never where the UV came from.
vec2 slng_planar_uv(vec3 vertex, vec3 normal) {
    vec3 p = vec3(vertex.x, -vertex.z, vertex.y) * prim_scale;
    vec3 n = vec3(normal.x, -normal.z, normal.y);

    vec3 binormal;
    if (n.x >= 0.5 || n.x <= -0.5) {
        binormal = vec3(0.0, n.x < 0.0 ? -1.0 : 1.0, 0.0);
    } else {
        binormal = vec3(n.y > 0.0 ? -1.0 : 1.0, 0.0, 0.0);
    }
    vec3 tangent = cross(binormal, n);

    float s = 1.0 + (dot(binormal, p) * 2.0 - 0.5);
    float t = -((dot(tangent, p) * 2.0 - 0.5));
    return vec2(s, 1.0 - t);
}

// --- Atmospherics seam ------------------------------------------------------------------
// Windlight / EEP goes here (Phase 5). Included from the shared include so prims, avatars,
// terrain and water all route through the same implementation.
#include "res://materials/slng_atmospherics.gdshaderinc"

// Takes and RETURNS the coordinate rather than using an `inout` parameter. The caller passes
// the UV built-in, and Godot does not treat built-ins as ordinary variables inside user
// functions -- assigning METALLIC/NORMAL_MAP from one is rejected outright ("Unknown identifier
// in expression"). Whether an `inout` argument writes back INTO a built-in at the call site is
// the same question, and it fails silently rather than erroring, which is the worst possible
// failure mode: the transform would simply never be applied. Returning the value sidesteps it.

// The placement transform, parameterised so the diffuse texture and a material's own maps can
// share one implementation. CENTRE, ROTATE, SCALE, OFFSET -- see slng_transform_uv.
vec2 slng_place_uv(vec2 uv, vec2 scale, vec2 offset, float rotation) {
    if (rotation != 0.0) {
        float s = sin(rotation);
        float c = cos(rotation);
        vec2 centred = uv - vec2(0.5);
        uv = vec2(centred.x * c - centred.y * s, centred.x * s + centred.y * c) + vec2(0.5);
    }
    return uv * scale + offset;
}

// Computes the coordinate a legacy material's NORMAL map is sampled at. Takes the same base
// coordinate the diffuse transform starts from (including texgen, which is a property of the
// FACE and applies to every map on it), then applies the material's own placement.
vec2 slng_normal_uv(vec2 uv, vec3 vertex, vec3 normal) {
    if (uv_texgen == 1) {
        uv = slng_planar_uv(vertex, normal);
    }
    return slng_place_uv(uv, normal_uv_scale, normal_uv_offset, normal_uv_rotation);
}

// Same as slng_normal_uv, for the specular map -- SL gives it its own repeat/offset/rotation
// too, and they are not required to match the normal map's.
vec2 slng_specular_uv(vec2 uv, vec3 vertex, vec3 normal) {
    if (uv_texgen == 1) {
        uv = slng_planar_uv(vertex, normal);
    }
    return slng_place_uv(uv, specular_uv_scale, specular_uv_offset, specular_uv_rotation);
}

vec2 slng_transform_uv(vec2 uv, vec3 vertex, vec3 normal) {
    // TexGen chooses the base coordinate; the transform below is identical either way. The
    // viewer has the same shape: getGeometryVolume computes planar tc and then calls the very
    // same xform() the default path uses (llface.cpp:1808-1812).
    if (uv_texgen == 1) {
        uv = slng_planar_uv(vertex, normal);
    }
    // No uv_extra_u/uv_extra_v here any more. They were a DIAGNOSTIC pair, added after the
    // finished transform so a residual sculpt offset could be dialled in from F10/F11 and read
    // off as a number instead of argued about. The offset they were built to measure is gone --
    // it was our sculpt grid, 33x33 against the viewer's 32x32, which put our UV step at 1/32
    // where the viewer's is 1/31 (fixed in `faadfa8`, now pinned index-by-index by
    // ViewerSculptParityTests). They wrote zero on every face after that, and every sculpt face
    // paid two SetShaderParameter calls for it.
    return slng_place_uv(uv, uv_scale, uv_offset, uv_rotation);
}

// Blinn-Phong glossiness -> Godot's PERCEPTUAL roughness, from the code that BUILDS SL's
// specular lookup texture (LLPipeline::createLUTBuffers, pipeline.cpp:1447):
//     n = glossiness * glossiness * RenderSpecularExponent    (default 368)
//     spec = pow(N dot H, n)
// then the standard Blinn-Phong -> GGX conversion alpha = sqrt(2/(n+2)). Godot's ROUGHNESS is
// perceptual, i.e. alpha = roughness^2, so the square root is taken twice.
//
// Getting this wrong is not subtle. A first attempt used roughness = 1 - glossiness, which for
// the reef rock's glossiness of 51/255 gives 0.8 and no visible highlight at all; the ported
// value is 0.59, and that is the difference between a matte rock and one whose ledges catch the
// sun.
//
// Shared by BOTH specular paths -- the legacy material's specular map and the build tool's
// plain Shiny level -- because in the viewer both index the same LUT and only the source of
// `glossiness` differs. Kept as one function so the two cannot drift apart.
//
// FEAT-RENDER-20: the second `sqrt` (alpha -> perceptual roughness) is deliberately NOT taken any
// more. It is the textbook-correct conversion for a HIGHLIGHT in isolation, and it is why this
// function shipped that way -- but Godot drives BOTH the highlight and the reflection-probe mip
// from this one value, and its two curves are nowhere near each other. Measured, with a
// checkerboard courtyard and a real probe at the settings Boot ships: at ROUGHNESS 0.31 (what
// Shiny HIGH produced with the sqrt) a pure mirror resolves the checker pattern into faint
// mottling -- unusable as a reflection; at 0.15 the pattern is recognisable but softened; at 0.05
// it is crisp. So the reflection mip chain is exhausted long before the highlight looks wrong.
//
// The reference viewer does not have to choose: it takes highlight sharpness from this LUT
// exponent and reflection sharpness from a SEPARATE term, `lod = (1.0 - glossiness) *
// reflection_lods` (reflectionProbeF.glsl:857), fed the raw Shiny level. Godot exposes no
// equivalent per-probe sharpness control, so one value has to serve both and the question is
// which compromise lands closer to the reference overall.
//
// Dropping the sqrt moves Shiny low/medium/high to 0.28 / 0.15 / 0.10 -- the measured band where
// a reflection is actually readable -- and tightens the sun highlight in the same step. Live A/B
// screenshots show the highlight was the LOOSER of the two against Firestorm (a small hard glint
// there, a broad soft blob here), so this is not a trade of one regression for one fix: both
// move toward the reference. It remains a compromise, and it does move a value FEAT-RENDER-19
// calibrated -- if a future A/B shows the highlight has overshot into too tight, the honest fix
// is a separate roughness for the two paths (which needs a custom light() or a second material),
// not quietly restoring the sqrt and giving up the reflection again.
// The environment intensity at which a legacy face is a full, sharp mirror: Shiny LOW (0.25), the
// smallest value the build tool produces. Mirrored by LegacyShadeMirror.MirrorFullSharpnessEnvironment.
const float MIRROR_FULL_SHARPNESS_ENV = 0.25;

// Material lab (Entwickler > Material-Labor): scales the SPECULAR a face with a specular MAP hands
// to Godot. A dev knob, not persisted, never written by anything but the lab window and project.godot.
//
// Two meanings, chosen by the variant (both are scale 1 = "viewer-faithful for this variant"):
//   * SLNG_LAB_SPECULAR_SCALE (prim_opaque, the checkbox-off fallback): multiplies Godot's own F0
//     reflection, which also drives Godot's GGX sun highlight. 1 = the stock look from before
//     v0.25.13; 0 removes the sky/probe "veil" (and with it the highlight).
//   * SLNG_VIEWER_SPEC (the *_vspec twins): multiplies the viewer's gloss/environment reflection
//     weight, see slng_viewer_gloss_env_specular. 1 = the viewer's applyGlossEnv.
//
// Every other variant (double-sided opaque, avatar, HUD...) is unscaled and keeps the specular it
// always had.
global uniform float slng_legacy_specular_scale;

#ifdef SLNG_VIEWER_SPEC
// ==============================================================================================
// The reference viewer's legacy-specular lighting, for every WORLD-PRIM variant that has a
// *_vspec twin (opaque, alpha-mask, alpha-blend; BUG-RENDER-41, extended by BUG-RENDER-44).
// A twin differs from its base shader by exactly `#define SLNG_VIEWER_SPEC` before including this
// file; nothing else is copied, so the maths below exists once.
//
// It has two halves:
//
// 1. light(): the viewer's sun highlight. Defining light() REPLACES Godot's built-in direct lighting
//    for the whole material (diffuse and specular), so it cannot be a toggle inside a shared
//    variant -- hence the twins. Per light:
//      DIFFUSE  Lambert: NdotL * ATTENUATION * LIGHT_COLOR / PI. Godot's LIGHT_COLOR is colour *
//               energy * PI, hence the division. The built-in uses Burley diffuse (render_mode
//               diffuse_burley); Lambert differs from it only at grazing angles / high roughness.
//      SPECULAR the viewer's term, softenLightF.glsl:243-263 (materialF.glsl:154-170 for local
//               lights; materialF.glsl:355-368 is the same expression in the ALPHA-BLEND branch):
//                 lit  = min(nl * 6, 1)
//                 fres = (1 - vh)^5 * 0.4 + 0.5
//                 gt   = max(0, min(2 nh nv / vh, 2 nh nl / vh))
//                 scol = shadow * fres * LUT(nh, glossiness) * gt / (nh * nl)
//                 color += lit * scol * sunlit * spec.rgb
//               LUT is the viewer's Blinn-Phong table (pipeline.cpp:1466-1475), n = glossiness^2 *
//               368. `shadow` is ATTENUATION, so the lobe is shadowed like the sun. `sunlit` is
//               replaced by the viewer's own highlight colour for the DIRECTIONAL light while the
//               sun is up: srgb_to_linear(sunlit * 1.35) * 1.1 (softenLightF.glsl:152-153,
//               :226-232, :280-281), published per frame as slng_viewer_sunlit
//               (ClassicLightBalance.ViewerSunlitForSpecular). It is NOT Godot's sun radiance: the
//               viewer builds its diffuse in sRGB on top of the ambient, which whitens it, but the
//               highlight from the bare (reddened) sunlit colour. Any other light, and the moon
//               (slng_viewer_sunlit = 0), uses LIGHT_COLOR / PI. Godot's own GGX lobe is NOT added
//               (light() owns SPECULAR_LIGHT), so there is no double highlight. spec.rgb is
//               srgb_to_linear(texel_srgb * tint), as materialF.glsl:245-246,:315.
//    On an alpha-BLENDED twin the highlight is blended with the face alpha like the rest of the
//    colour: the viewer's blend branch outputs `vec4(color_incl_spec, al)` into SRC_ALPHA /
//    ONE_MINUS_SRC_ALPHA (materialF.glsl:446, lldrawpoolalpha.cpp:254, llrender.cpp:1313), and
//    Godot's blend_mix does the same with the SPECULAR_LIGHT it is given. (`al` is only lifted by
//    `glare`, the Environment term, never by the sun highlight.)
//
// 2. SPECULAR for Godot's image-based light: the viewer's gloss/environment reflection, see
//    slng_viewer_gloss_env_specular. With light() owning the direct specular, SPECULAR only steers
//    Godot's sky/probe reflection, so it can carry exactly that term.
//
// A twin's fragment() is its base's plus one block, after `EMISSION = emission;`, that hands light()
// the face's specular colour and glossiness (see slng_viewer_spec_capture).
//
// Mirrored (constants and formulas) by SLNG.Core.LegacyShadeMirror; the constants below are pinned
// against it by a test.
// ==============================================================================================

// The viewer's highlight sun colour for the sun, zero while the moon is the light.
global uniform vec3 slng_viewer_sunlit;

// Written by the twin's fragment() from slng_viewer_spec_capture, read in light(): the specular
// colour and the per-texel glossiness the viewer would have packed into its G-buffer.
varying vec3 v_vspec_rgb;
varying float v_vspec_gloss;

const float VSPEC_EXPONENT_SCALE = 368.0;
const float VSPEC_LIT_NL_SCALE = 6.0;
const float VSPEC_FRESNEL_BASE = 0.5;
const float VSPEC_FRESNEL_SCALE = 0.4;
const float VSPEC_EPS = 0.000001;

// applyGlossEnv (reflectionProbeF.glsl:893-902) adds, to the lit colour,
//     glossenv * 0.5 (fudge) * spec.rgb * fresnel^2 * spec.a * (1 - color) * 0.5
// with fresnel = clamp(1 + dot(normalize(pos), n), 0.3, 1) = clamp(1 - N.V, 0.3, 1) and
// glossenv = the probe sampled at lod (1 - glossiness) * reflection_lods (:857). So the reflection
// weight is LINEAR in glossiness: ~0.002 at gloss 30 (the terrace floor), ~0.019 at gloss 220,
// head-on, rising with the fresnel^2 ramp to 0.22 at the silhouette.
const float VSPEC_GLOSS_ENV_WEIGHT = 0.25;          // 0.5 (fudge) * 0.5 (the final `color += glossenv*0.5`)
const float VSPEC_GLOSS_ENV_FRESNEL_MIN = 0.3;
// What Godot reflects of the environment for a dielectric: F0 = 0.08 * SPECULAR, times the
// ReflectionProbe's Intensity (Boot.cs, mirrored as LegacyShadeMirror.ReflectionProbeIntensity).
const float VSPEC_GODOT_F0_PER_SPECULAR = 0.08;
const float VSPEC_PROBE_INTENSITY = 1.5;

vec3 vspec_linear_to_srgb(vec3 c) {
    c = clamp(c, vec3(0.0), vec3(1.0));
    vec3 low_range = c * vec3(12.92);
    vec3 high_range = vec3(1.055) * pow(c, vec3(1.0 / 2.4)) - vec3(0.055);
    return mix(high_range, low_range, lessThanEqual(c, vec3(0.0031308)));
}

// What the viewer's lighting pass reads back for this face: rgb = specular colour, a = glossiness;
// all zero (no sun highlight) for a face without a specular map. Called from the twin's fragment()
// as `vec4 vspec = slng_viewer_spec_capture(UV); v_vspec_rgb = vspec.rgb; v_vspec_gloss = vspec.a;`
// -- a varying can only be assigned there, not inside this function or slng_shade.
vec4 slng_viewer_spec_capture(vec2 uv) {
    if (!has_specular_texture) {
        return vec4(0.0);
    }
    // materialF.glsl:245-246 multiplies map and colour in sRGB, :315 linearises the product. The
    // sampler is source_color (already linear), so go back to sRGB for the multiply.
    vec3 texel = texture(specular_texture, v_specular_uv).rgb;
    vec3 rgb = slng_srgb_to_linear(vspec_linear_to_srgb(texel) * specular_tint);
    // materialF.glsl:227: glossiness *= normal map alpha, per texel (the same product slng_shade makes).
    float glossiness = specular_glossiness;
    if (has_normal_texture) {
        glossiness *= texture(normal_texture, has_normal_uv ? v_normal_uv : uv).a;
    }
    return vec4(rgb, glossiness);
}

// The SPECULAR value that makes Godot's probe/sky reflection as strong as the viewer's applyGlossEnv
// for this pixel. spec_lum is the specular colour's luminance (Godot has one scalar, not an RGB),
// nv = N.V, albedo_lum stands in for the `(1 - color)` energy-conservation factor (the lit colour is
// not known at this stage; for a lit surface it is albedo-like).
//
// Honest limits: the viewer samples a probe at a LOD that grows with (1 - glossiness); Godot picks
// the mip from ROUGHNESS, which slng_glossiness_to_roughness derives from the same glossiness, so
// the two blur curves are only similar. Godot's own Schlick term still adds a grazing rim on top
// (its f90 scales with F0, so it is small for the small values used here), and F0 saturates at
// SPECULAR 1 = 0.12 with the probe boost, below the viewer's 0.22 peak at the silhouette of a very
// glossy face. At gloss 30 the result is ~0.002, i.e. none: the constant veil that made the terrace
// floor look like glass stays gone.
float slng_viewer_gloss_env_specular(float spec_lum, float glossiness, float nv, float albedo_lum) {
    float fresnel = clamp(1.0 - nv, VSPEC_GLOSS_ENV_FRESNEL_MIN, 1.0);
    float weight = VSPEC_GLOSS_ENV_WEIGHT * spec_lum * fresnel * fresnel * glossiness * (1.0 - clamp(albedo_lum, 0.0, 1.0));
    return clamp(weight * slng_legacy_specular_scale / (VSPEC_GODOT_F0_PER_SPECULAR * VSPEC_PROBE_INTENSITY), 0.0, 1.0);
}

void light() {
    float nl_raw = dot(NORMAL, LIGHT);
    DIFFUSE_LIGHT += clamp(nl_raw, 0.0, 1.0) * ATTENUATION * LIGHT_COLOR / PI;

    // `if (spec.a > 0.0)`, softenLightF.glsl:243: no glossiness, no highlight.
    if (v_vspec_gloss > 0.0) {
        vec3 l = normalize(LIGHT);
        vec3 v = normalize(VIEW);
        vec3 h = normalize(l + v);
        // calcHalfVectors, deferredUtil.glsl:114-128: every dot product clamped to [eps, 1].
        float nh = clamp(dot(NORMAL, h), VSPEC_EPS, 1.0);
        float nl = clamp(nl_raw, VSPEC_EPS, 1.0);
        float nv = clamp(dot(NORMAL, v), VSPEC_EPS, 1.0);
        float vh = clamp(dot(v, h), VSPEC_EPS, 1.0);

        float lit = min(nl * VSPEC_LIT_NL_SCALE, 1.0);
        float fres = pow(1.0 - vh, 5.0) * VSPEC_FRESNEL_SCALE + VSPEC_FRESNEL_BASE;
        float gt = max(0.0, min(2.0 * nh * nv / vh, 2.0 * nh * nl / vh));

        // The viewer's lightFunc lookup (pipeline.cpp:1466-1475).
        float n = v_vspec_gloss * v_vspec_gloss * VSPEC_EXPONENT_SCALE;
        float lut = pow(nh, n) * ((n + 2.0) * (n + 4.0)) / (8.0 * PI * (exp2(-n / 2.0) + n));

        float scol = ATTENUATION * fres * lut * gt / (nh * nl);
        vec3 radiance = LIGHT_COLOR / PI;
        if (LIGHT_IS_DIRECTIONAL && dot(slng_viewer_sunlit, vec3(1.0)) > 0.0) {
            radiance = slng_viewer_sunlit;
        }
        SPECULAR_LIGHT += lit * scol * radiance * v_vspec_rgb;
    }
}
#endif

float slng_glossiness_to_roughness(float glossiness) {
    float n = glossiness * glossiness * 368.0;
    float ggx_alpha = sqrt(2.0 / (n + 2.0));
    return clamp(ggx_alpha, 0.0, 1.0);
}

// FEAT-RENDER-20: BUG-RENDER-20's hand-rolled slng_env_reflection() -- a Fresnel-weighted,
// shininess-scaled SKY-TINT approximation of applyGlossEnv (reflectionProbeF.glsl:893), ported as
// an independent additive EMISSION term because SLNG placed no real reflection probe at the time
// -- lived here through four rounds of hand-tuning (see BUG-RENDER-20's spec for the full history:
// a flat single colour was invisible head-on, blending by "which way is up" needed a per-fragment
// INV_VIEW_MATRIX computation threaded through every variant as world_normal_up, gating intensity
// by that same upness produced a visible hard seam at the world-space equator, and the final
// shipped shape was a Fresnel-only ring around the whole silhouette -- an honestly-labelled
// approximation, never a real capture of surrounding geometry).
//
// Removed once FEAT-RENDER-20 placed an actual Godot ReflectionProbe (see Boot.cs
// SetupEnvironment/UpdateReflectionProbe) and confirmed empirically that Godot's built-in
// metallic-roughness pipeline already routes a real probe's capture into the same
// METALLIC/ROUGHNESS/SPECULAR this file already writes -- no new shader plumbing needed, exactly
// as BUG-RENDER-20 round 1 had already shown for the sky-only IBL fallback this term stood in
// for. Leaving both active was measured to double-count: a courtyard probe scene with real
// coloured walls around a shiny-HIGH sphere showed crisp, clearly separated wall colours with
// ONLY the real probe active, but a washed-out, near-uniform blue-grey haze smearing across the
// whole sphere with this term ALSO active on top -- the old sky-tint drowning out the real
// capture it was written to approximate. Removing it is what lets the real probe's capture reach
// the screen undiluted. The `world_normal_up` parameter this function needed (threaded through
// every prim_*.gdshader variant's slng_shade() call site as
// `(INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).y`) went with it -- nothing else ever used it.
//
// Samples everything a face needs and hands it back through out-params. It does NOT assign
// the built-ins itself: METALLIC / ROUGHNESS / NORMAL_MAP / EMISSION are fragment-stage
// built-ins and Godot rejects writing them from a user function ("Unknown identifier in
// expression: 'METALLIC'"). Each variant's fragment() does the assigning; this keeps the
// sampling logic in one place, which is the part that must not drift between variants.
//
// Returns the albedo alpha, the one thing the variants genuinely disagree about.
float slng_shade(vec2 uv, vec3 view_position, vec3 normal,
                 out vec3 out_albedo,
                 out float out_metallic, out float out_roughness, out float out_specular,
                 out vec3 out_normal_map, out float out_normal_depth,
                 out vec3 out_emission) {
    vec4 albedo = albedo_color;
    if (has_albedo_texture) {
        albedo *= texture(albedo_texture, uv);
    }

    vec3 atmos_additive;
    out_albedo = slng_apply_atmospherics(albedo.rgb, view_position, atmos_additive);

    out_metallic = metallic_factor;
    out_roughness = roughness_factor;

    // 0, not Godot's default 0.5. The viewer gates its ENTIRE specular and environment branch on
    // `if (spec.a > 0.0)` (softenLightF.glsl:243), and spec.a is the face's legacy shininess --
    // zero for PRIM_SHINY_NONE, which is most SL content. SLNG does not read legacy shininess at
    // all (there is no uniform for it), so a flat 0.5 here was a 4% dielectric highlight driven by
    // no SL data whatsoever, on every face without a specular map.
    //
    // Found with the probe sphere: greys matched Firestorm exactly while the pure primaries came
    // out visibly duller. That is the signature -- 4% of neutral reflection is invisible on a
    // white or grey surface and washes out a fully saturated one, and a blue sphere went violet
    // because what it was reflecting was the warm courtyard. A legacy MATERIAL still overrides
    // this below (BUG-RENDER-21 -- a specular MAP is not what decides that), which is the other
    // path SL feeds.
    out_specular = 0.0;

    // A specular MAP selects this path (BUG-RENDER-23 put the gate back where llvovolume.cpp
    // keeps it; see the uniform block above for the source that settles it).
    //
    // SL's environment reflection lives on BOTH paths, which is the half BUG-RENDER-20 was
    // missing for its whole four-round history: `env_intensity` is a real per-face value even
    // with no material anywhere in sight, because the viewer fills it from the Shiny level.
    float env_intensity = 0.0;

    if (has_specular_texture) {
        vec3 spec = texture(specular_texture, v_specular_uv).rgb * specular_tint;

        // SL modulates glossiness PER TEXEL with the normal map's ALPHA channel
        // (materialF.glsl:227, `glossiness *= vNt.a`). Not an optional refinement: content is
        // authored expecting it, and without it a material that varies its polish across the
        // surface comes out uniformly shiny or uniformly matte.
        float glossiness = specular_glossiness;
        if (has_normal_texture) {
            glossiness *= texture(normal_texture, has_normal_uv ? v_normal_uv : uv).a;
        }

        out_roughness = slng_glossiness_to_roughness(glossiness);

        // The map's RGB scales the highlight's STRENGTH, matching the viewer's
        // `color += lit * scol * sunlit * spec.rgb`. Godot has no per-texel specular colour, so
        // this is the one lossy step: a greyscale map (what nearly all SL content ships) is
        // reproduced faithfully, a coloured one loses its tint. Rec. 709 luminance.
        //
        // Zero at glossiness 0: the viewer wraps its entire specular branch in
        // `if (glossiness > 0.0)` (materialF.glsl:367) and skips the gloss-env sample on the same
        // test (reflectionProbeF.glsl:855-860), so such a face gets neither highlight nor
        // reflection from this path.
        out_specular = glossiness > 0.0
            ? clamp(dot(spec, vec3(0.2126, 0.7152, 0.0722)), 0.0, 1.0)
            : 0.0;

        // The material's own EnvIntensity -- the Build floater's Environment slider. Only
        // reachable here, because only a material WITH a specular map ever reaches the shader
        // with its own value (llvovolume.cpp:5556).
        env_intensity = specular_environment;
        out_metallic = clamp(metallic_factor + env_intensity * out_specular, 0.0, 1.0);

        // After the metallic fold above on purpose: the lab scales only what Godot reads as the
        // dielectric F0, never the Environment-slider mirror path.
#ifdef SLNG_LAB_SPECULAR_SCALE
        out_specular *= slng_legacy_specular_scale;
#endif

#ifdef SLNG_VIEWER_SPEC
        // (The viewer's lighting pass reads its colour and glossiness back through
        // slng_viewer_spec_capture, which the twin's fragment() calls: Godot does not let a function
        // other than fragment() assign a varying.)
        //
        // Godot's SPECULAR now only steers its image-based reflection (light() owns the direct
        // highlight), so it carries the viewer's applyGlossEnv weight instead of the map luminance.
        float vspec_nv = clamp(dot(normalize(normal), normalize(-view_position)), 0.0, 1.0);
        float vspec_albedo_lum = dot(albedo.rgb, vec3(0.2126, 0.7152, 0.0722));
        out_specular = slng_viewer_gloss_env_specular(out_specular, glossiness, vspec_nv, vspec_albedo_lum);
#endif
    }
    else if (legacy_shininess > 0.0) {
        // No specular map -- with or without a legacy material. `legacy_shininess` is already the
        // SHININESS_TO_ALPHA value the viewer packs (none 0, low 0.25, medium 0.5, high 0.75),
        // and llvovolume.cpp:5543-5546 hands that ONE number to the shader twice: as the
        // glossiness (specColor.a) and as the environment intensity (mEnvIntensity).
        //
        // The env half is what BUG-RENDER-20 spent four rounds hand-tuning a sky-tint
        // approximation for, and what FEAT-RENDER-20 then wired a real ReflectionProbe to --
        // without ever routing this value into it, so a plain shiny prim still had no way to
        // reflect anything. Shiny HIGH is a 0.75-strength mirror in SL, and that is the chrome
        // ball the reference viewer draws.
        //
        // 0.5 for the highlight is Godot's standard 4% dielectric F0, left as FEAT-RENDER-19
        // calibrated it against the probe sphere. The viewer's own spec.rgb here is
        // vec3(legacy_shininess), i.e. the highlight strength varies with the Shiny level too --
        // a smaller, separate question than the missing reflection, and one that needs its own
        // A/B rather than a quiet change on the way past.
        out_specular = 0.5;
        out_roughness = slng_glossiness_to_roughness(legacy_shininess);
        env_intensity = legacy_shininess;
    }

    // glTF ORM packing: occlusion in R, roughness in G, metallic in B.
    if (has_orm_texture) {
        vec4 orm = texture(orm_texture, uv);
        out_roughness = roughness_factor * orm.g;
        out_metallic = metallic_factor * orm.b;
    }

    // SL's Environment Intensity -- the slider that makes a prim a MIRROR. Reported live as a
    // mirror rendering near-black and "extrem blurry" against Firestorm's sharp room reflection.
    //
    // Folding it into metalness alone (which the branch above still does, and which was the whole
    // of our handling before this) is wrong in two separate ways, both reproduced in an isolated
    // render through this very shader before changing anything:
    //
    //   BLURRY -- a metallic reflection is sampled from the probe at a mip chosen by ROUGHNESS.
    //             The viewer does not do that for this term: it samples at a forced `lod 0.0`,
    //             the sharpest mip, regardless of glossiness
    //             (`legacyenv = sampleProbes(pos, normalize(refnormpersp), 0.0)`,
    //             reflectionProbeF.glsl:862-865) -- contrast the `glossenv` sample three lines
    //             above it, which does use `(1.0 - glossiness) * reflection_lods`.
    //   BLACK  -- and this is the one that actually hid the reflection: Godot tints a METALLIC
    //             surface's reflection by its ALBEDO. Rendered side by side, `metallic 0.9,
    //             roughness 0` gives a pure black rectangle at albedo 0.05 and a working mirror
    //             at albedo 0.9. Mirrors are dark-textured almost by definition, so the more
    //             Environment Intensity we folded into metalness, the less there was to see.
    //
    // The viewer's model is not a tinted reflection at all, it REPLACES the surface
    // (`applyLegacyEnv`, reflectionProbeF.glsl:904-913):
    //
    //     fresnel = min(fresnel + envIntensity, 1.0);        // full-surface, not a grazing rim
    //     reflected_color *= (envIntensity * fresnel);
    //     color = mix(color.rgb, reflected_color * 0.5, envIntensity);
    //
    // Godot offers no way to add a probe sample additively from a spatial shader, so the three
    // built-ins are driven together instead, which reproduces all three properties: roughness to 0
    // is the `lod 0.0`; metallic to 1 is the full-surface response the forced Fresnel gives; and
    // albedo toward a mid grey is what makes the result albedo-INDEPENDENT -- a dark mirror stops
    // eating its own reflection -- with 0.5 standing in for the viewer's own `* 0.5`.
    //
    // Deliberately placed AFTER the ORM block so a glTF material's own roughness/metallic cannot
    // undo it, and gated purely on `env_intensity` -- which BUG-RENDER-23 made a real per-face
    // value on BOTH paths: the material's own slider where there is a specular map, the prim's
    // Shiny level where there is not.
    //
    // Overriding out_albedo does discard the atmospherics already applied to it. That is the
    // viewer's behaviour too -- its mix replaces the shaded colour outright -- and on an indoor
    // mirror there is nothing to lose; on a distant outdoor one it costs a little haze tint.
    // BUG-RENDER-25: the surface colour as it stands BEFORE the env block rewrites it toward mid
    // grey. The fullbright branch at the end needs the real one for its unlit share -- feeding it
    // the greyed value would wash out exactly the faces that carry the strongest reflection.
    vec3 pre_env_albedo = out_albedo;

    env_intensity = clamp(env_intensity, 0.0, 1.0);
    if (env_intensity > 0.0) {
        // BUG-RENDER-23: roughness 0 FLAT, never interpolated by the intensity. The viewer takes
        // this sample at the sharpest mip whatever the slider says --
        // `legacyenv = sampleProbes(pos, normalize(refnormpersp), 0.0)`
        // (reflectionProbeF.glsl:862-865, and note the `glossenv` sample seven lines above it
        // DOES use `(1.0 - glossiness) * reflection_lods`, so the two sharpnesses are deliberately
        // independent) -- and the intensity then weighs only how much of that reflection replaces
        // the surface (`color = mix(color.rgb, reflected_color * 0.5, envIntensity)`,
        // applyLegacyEnv:912). Interpolating the ROUGHNESS by it made sharpness a function of
        // intensity instead: a half-mirror came out half-blurred.
        //
        // Load-bearing now in a way it was not before: with `env_intensity` fed by the Shiny
        // level, every shiny prim in the world takes this branch, and interpolating the roughness
        // would have made shiny LOW a blurred smear and only shiny HIGH anything like a mirror.
        //
        // ...but only for a face that IS a mirror. "Flat, never interpolated by the intensity" was
        // written for the Shiny levels (0.25 and up) and it is still true of them, but it forced
        // roughness to exactly 0 for ANY non-zero intensity -- and the Build floater's Environment
        // slider is one byte, so a material with Environment 5 (0.02, i.e. 2% of a reflection in the
        // viewer) came out as a perfectly smooth dielectric: its specular term (F0 up to 0.08, not
        // weighted by the intensity at all) became a sharp mirror of the sky and the probe, modulated
        // by every texel of the normal map. Reported on the Millenium terrace floor (gloss 30,
        // environment 5): glass with puddle-like blotches, in Firestorm a matte terracotta. So the
        // sharp lobe is weighted by how much of a mirror the face is, reaching full sharpness at
        // MIRROR_FULL_SHARPNESS_ENV -- Shiny LOW, the smallest intensity the build tool can produce
        // -- which leaves every Shiny level and every real mirror material exactly as it was.
        out_roughness = mix(out_roughness, 0.0, clamp(env_intensity / MIRROR_FULL_SHARPNESS_ENV, 0.0, 1.0));
        out_metallic = mix(out_metallic, 1.0, env_intensity);
        out_albedo = mix(out_albedo, vec3(0.5), env_intensity);
    }

    // A flat normal (0.5, 0.5, 1.0) with zero depth is a no-op, so the variants can assign
    // unconditionally instead of needing a second "has normal" flag threaded through.
    out_normal_map = vec3(0.5, 0.5, 1.0);
    out_normal_depth = 0.0;
    if (has_normal_texture) {
        out_normal_map = texture(normal_texture, has_normal_uv ? v_normal_uv : uv).rgb;
        out_normal_depth = normal_scale;
    }

    out_emission = atmos_additive;
    if (emission_enabled) {
        // MULTIPLIES factor by texture, which is what glTF specifies for
        // emissiveFactor * emissiveTexture -- and what SLNG's only source of emission data
        // (AssetService's glTF material resolve) means.
        vec3 emission = emission_color;
        if (has_emission_texture) {
            emission *= texture(emission_texture, uv).rgb;
        }
        out_emission = emission + atmos_additive;
    }

    // SL fullbright: the face is lit by nothing. Route the (already atmospherics-applied) albedo
    // through EMISSION and zero ALBEDO so clustered lighting, ambient and GI add nothing on top;
    // atmospherics stays applied, matching the viewer's fullbright shaders. Added to any glTF
    // emissive rather than replacing it, since a face can be both.
    //
    // NOT a no-op for the `unshaded` HUD variants, which is what an earlier version of this
    // comment claimed. Godot's `unshaded` render_mode outputs ALBEDO *only* -- EMISSION is
    // dropped -- so zeroing albedo here rendered every fullbright HUD face pure black, and HUD
    // faces are almost always fullbright (measured in-world: all six faces of a worn HUD, each
    // correctly textured 512-1024px, drawn as solid black rectangles). The HUD variants therefore
    // fold emission back into ALBEDO themselves; see prim_opaque_hud.gdshader.
    if (fullbright) {
        // BUG-RENDER-25: a fullbright face is not automatically an unreflective one. SL's classic
        // fake mirror is exactly this combination -- fullbright + Shiny HIGH, no material, no PBR
        // -- and the viewer gives it its own shader, `class3/deferred/fullbrightShinyF.glsl`:
        //
        //     float env_intensity = vertex_color.a;        // the SHINY level, nothing else
        //     vec4 spec = vec4(0,0,0,0);                   // no highlight at all
        //     sampleReflectionProbesLegacy(..., spec.a, env_intensity, false, amblit);
        //     color.rgb = srgb_to_linear(color.rgb);
        //     applyLegacyEnv(color.rgb, legacyenv, spec, pos, norm, env_intensity);
        //
        // i.e. the unlit texture and the environment reflection are MIXED by the Shiny level
        // (`color = mix(color, reflected*0.5, envIntensity)`), not chosen between. Zeroing albedo
        // outright killed the reflection twice over: Godot tints a metallic reflection by the
        // albedo, so albedo 0 reflects nothing, and the leftover flat EMISSION is then the entire
        // surface. Reported live as a wall mirror rendering as a flat light-grey panel while
        // Firestorm showed the room in it.
        //
        // Mapped onto Godot's built-ins rather than approximated: the (1 - env) share of the
        // fullbright colour goes to EMISSION as before, and the reflective share stays a real
        // METALLIC surface -- the env block above already set metallic/roughness -- with an albedo
        // of `0.5 * env * env`, because Godot reflects `probe * albedo` where the viewer's two
        // steps give `env * (env * 0.5) * probe`. Godot's own Fresnel stands in for
        // applyLegacyEnv's `min(fresnel + envIntensity, 1.0)`.
        out_emission += pre_env_albedo * (1.0 - env_intensity);
        out_albedo = vec3(0.5 * env_intensity * env_intensity);
    }

    return albedo.a;
}
