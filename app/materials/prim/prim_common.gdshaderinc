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
// (materialF.glsl:242-307):
//
//     spec       = texture(specularMap) * specular_color.rgb   // per-texel reflectivity/tint
//     glossiness = specular_color.a                            // SpecExp, ONE scalar per material
//     env        = env_intensity * spec.a                      // reflection strength
//
// Godot's metallic-roughness model has no per-texel specular COLOUR, so this is the one place in
// FEAT-RENDER-04 that is an approximation rather than a port, and it is deliberately a
// conservative one: the map's luminance drives how polished the surface is, and the material's
// glossiness sets how polished "fully lit" means. A greyscale spec map -- which is what almost
// all SL content ships -- is reproduced faithfully by that; a coloured one loses its tint.
uniform sampler2D specular_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_specular_texture = false;
uniform vec3 specular_tint = vec3(1.0);
uniform float specular_glossiness = 0.2;   // SpecExp/255; the viewer's default is 0.2*255
uniform float specular_environment = 0.0;  // EnvIntensity/255

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
// DIAGNOSTIC. Added to V after the full transform, so it shifts the finished result rather than
// the source coordinate -- which is what makes it directly readable as "the pattern sits N
// textures too low". Driven live from F10/F11 (ObjectRenderer) and applied to SCULPT faces only,
// to measure the constant offset those still show against Firestorm once the mirror question was
// settled. Zero by default; it exists to turn an argument into a number.
uniform float uv_extra_v = 0.0;
uniform float uv_extra_u = 0.0;

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
    uv = slng_place_uv(uv, uv_scale, uv_offset, uv_rotation);
    uv.x += uv_extra_u;
    uv.y += uv_extra_v;
    return uv;
}

// Samples everything a face needs and hands it back through out-params. It does NOT assign
// the built-ins itself: METALLIC / ROUGHNESS / NORMAL_MAP / EMISSION are fragment-stage
// built-ins and Godot rejects writing them from a user function ("Unknown identifier in
// expression: 'METALLIC'"). Each variant's fragment() does the assigning; this keeps the
// sampling logic in one place, which is the part that must not drift between variants.
//
// Returns the albedo alpha, the one thing the variants genuinely disagree about.
float slng_shade(vec2 uv, vec3 view_position,
                 out vec3 out_albedo,
                 out float out_metallic, out float out_roughness, out float out_specular,
                 out vec3 out_normal_map, out float out_normal_depth,
                 out vec3 out_emission) {
    vec4 albedo = albedo_color;
    if (has_albedo_texture) {
        albedo *= texture(albedo_texture, uv);
    }

    out_albedo = slng_apply_atmospherics(albedo.rgb, view_position);

    out_metallic = metallic_factor;
    out_roughness = roughness_factor;

    out_specular = 0.5;   // Godot's default: a 4% dielectric F0.

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

        // The Blinn-Phong exponent SL actually uses, from the code that BUILDS its specular
        // lookup texture (LLPipeline::createLUTBuffers, pipeline.cpp:1447):
        //     n = glossiness * glossiness * RenderSpecularExponent      (default 368)
        //     spec = pow(N dot H, n)
        // Then the standard Blinn-Phong -> GGX conversion, alpha = sqrt(2/(n+2)), and Godot's
        // ROUGHNESS is PERCEPTUAL, i.e. alpha = roughness^2 -- so the square root is taken twice.
        //
        // Getting this wrong is not subtle. A first attempt used roughness = 1 - glossiness,
        // which for the reef rock's glossiness of 51/255 gives 0.8 and no visible highlight at
        // all; the ported value is 0.59, and that is the difference between a matte rock and one
        // whose ledges catch the sun.
        float n = glossiness * glossiness * 368.0;
        float ggx_alpha = sqrt(2.0 / (n + 2.0));
        out_roughness = clamp(sqrt(ggx_alpha), 0.0, 1.0);

        // The map's RGB scales the highlight's STRENGTH, matching the viewer's
        // `color += lit * scol * sunlit * spec.rgb`. Godot has no per-texel specular colour, so
        // this is the one lossy step: a greyscale map (what nearly all SL content ships) is
        // reproduced faithfully, a coloured one loses its tint. Rec. 709 luminance.
        out_specular = clamp(dot(spec, vec3(0.2126, 0.7152, 0.0722)), 0.0, 1.0);

        // EnvIntensity is SL's "how mirror-like is this". Godot reaches the environment through
        // metalness, so it folds in there, scaled by the same luminance so it only applies where
        // the map says the surface is shiny at all.
        out_metallic = clamp(metallic_factor + specular_environment * out_specular, 0.0, 1.0);
    }

    // glTF ORM packing: occlusion in R, roughness in G, metallic in B.
    if (has_orm_texture) {
        vec4 orm = texture(orm_texture, uv);
        out_roughness = roughness_factor * orm.g;
        out_metallic = metallic_factor * orm.b;
    }

    // A flat normal (0.5, 0.5, 1.0) with zero depth is a no-op, so the variants can assign
    // unconditionally instead of needing a second "has normal" flag threaded through.
    out_normal_map = vec3(0.5, 0.5, 1.0);
    out_normal_depth = 0.0;
    if (has_normal_texture) {
        out_normal_map = texture(normal_texture, has_normal_uv ? v_normal_uv : uv).rgb;
        out_normal_depth = normal_scale;
    }

    out_emission = vec3(0.0);
    if (emission_enabled) {
        // MULTIPLIES factor by texture, which is what glTF specifies for
        // emissiveFactor * emissiveTexture -- and what SLNG's only source of emission data
        // (AssetService's glTF material resolve) means.
        vec3 emission = emission_color;
        if (has_emission_texture) {
            emission *= texture(emission_texture, uv).rgb;
        }
        out_emission = emission;
    }

    // SL fullbright: the face is lit by nothing. Route the (already atmospherics-applied) albedo
    // through EMISSION and zero ALBEDO so clustered lighting, ambient and GI add nothing on top;
    // atmospherics stays applied, matching the viewer's fullbright shaders. Added to any glTF
    // emissive rather than replacing it, since a face can be both. A no-op for the `unshaded` HUD
    // variants, whose output is ALBEDO + EMISSION regardless of which one carries the value.
    if (fullbright) {
        out_emission += out_albedo;
        out_albedo = vec3(0.0);
    }

    return albedo.a;
}
