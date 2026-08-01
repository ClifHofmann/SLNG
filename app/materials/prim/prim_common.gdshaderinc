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

// --- Atmospherics seam ------------------------------------------------------------------
// Windlight / EEP goes here (Phase 5). Deliberately a no-op today: it exists so that filling
// it later is an edit to one function shared by prims, avatars, terrain and water, rather
// than a migration. See ADR 0002 for why this seam is the actual reason the shader family
// exists at all.
vec3 slng_apply_atmospherics(vec3 color, vec3 view_position) {
    return color;
}

// Takes and RETURNS the coordinate rather than using an `inout` parameter. The caller passes
// the UV built-in, and Godot does not treat built-ins as ordinary variables inside user
// functions -- assigning METALLIC/NORMAL_MAP from one is rejected outright ("Unknown identifier
// in expression"). Whether an `inout` argument writes back INTO a built-in at the call site is
// the same question, and it fails silently rather than erroring, which is the worst possible
// failure mode: the transform would simply never be applied. Returning the value sidesteps it.
vec2 slng_transform_uv(vec2 uv) {
    if (uv_rotation != 0.0) {
        float s = sin(uv_rotation);
        float c = cos(uv_rotation);
        vec2 centred = uv - vec2(0.5);
        uv = vec2(centred.x * c - centred.y * s, centred.x * s + centred.y * c) + vec2(0.5);
    }
    return uv * uv_scale + uv_offset;
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
                 out float out_metallic, out float out_roughness,
                 out vec3 out_normal_map, out float out_normal_depth,
                 out vec3 out_emission) {
    vec4 albedo = albedo_color;
    if (has_albedo_texture) {
        albedo *= texture(albedo_texture, uv);
    }

    out_albedo = slng_apply_atmospherics(albedo.rgb, view_position);

    out_metallic = metallic_factor;
    out_roughness = roughness_factor;

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
        out_normal_map = texture(normal_texture, uv).rgb;
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

    return albedo.a;
}
