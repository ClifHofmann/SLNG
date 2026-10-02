// Material lab experiment (Entwickler > Material-Labor > "Viewer-Sonnenglanz"): the OPAQUE prim
// variant with its direct lighting replaced by a custom light() that adds the REFERENCE VIEWER's own
// sun highlight. Selected by ObjectRenderer ONLY for a face that takes the has_specular_texture legacy
// branch and ONLY while the lab checkbox is on (default off), so default rendering is untouched.
//
// Why a separate variant: defining light() REPLACES Godot's built-in direct lighting for the whole
// material (diffuse and specular), so it cannot be a toggle inside prim_opaque. Everything else here
// is prim_opaque.gdshader verbatim -- same fragment(), so IBL (the "veil", scaled by the lab's
// slng_legacy_specular_scale) and every uniform behave exactly as before.
//
// What light() does, per light:
//   DIFFUSE  Lambert: NdotL * ATTENUATION * LIGHT_COLOR / PI. Godot's LIGHT_COLOR is colour * energy
//            * PI, hence the division. The built-in uses Burley diffuse (render_mode diffuse_burley);
//            Lambert differs from it only at grazing angles / high roughness.
//   SPECULAR the viewer's term, softenLightF.glsl:243-263 (materialF.glsl:154-170 for local lights):
//              lit  = min(nl * 6, 1)
//              fres = (1 - vh)^5 * 0.4 + 0.5
//              gt   = max(0, min(2 nh nv / vh, 2 nh nl / vh))
//              scol = shadow * fres * LUT(nh, glossiness) * gt / (nh * nl)
//              color += lit * scol * sunlit * spec.rgb
//            LUT is the viewer's Blinn-Phong table (pipeline.cpp:1466-1475), n = glossiness^2 * 368.
//            `shadow` is ATTENUATION, so the lobe is shadowed like the sun. `sunlit` is replaced by
//            the viewer's own highlight colour for the DIRECTIONAL light while the sun is up:
//            srgb_to_linear(sunlit * 1.35) * 1.1 (softenLightF.glsl:152-153, :226-232, :280-281),
//            published per frame as slng_viewer_sunlit (ClassicLightBalance.ViewerSunlitForSpecular).
//            It is NOT Godot's sun radiance: the viewer builds its diffuse in sRGB on top of the
//            ambient, which whitens it, but the highlight from the bare (reddened) sunlit colour.
//            Any other light, and the moon (slng_viewer_sunlit = 0), uses LIGHT_COLOR / PI.
//            Godot's own GGX lobe is NOT added (light() owns SPECULAR_LIGHT), so there is no double
//            highlight. spec.rgb is srgb_to_linear(texel_srgb * tint), as materialF.glsl:245-246,:315.
//
// Mirrored (constants and formula) by SLNG.Core.LegacyShadeMirror.ViewerSunSpecular; the constants
// below are pinned against it by a test.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx;

#include "res://materials/prim/prim_common.gdshaderinc"

// The viewer's highlight sun colour for the sun, zero while the moon is the light.
global uniform vec3 slng_viewer_sunlit;

varying vec3 v_view_position;
// Written in fragment(), read in light(): the specular colour and the per-texel glossiness the viewer
// would have packed into its G-buffer.
varying vec3 v_vspec_rgb;
varying float v_vspec_gloss;

const float VSPEC_EXPONENT_SCALE = 368.0;
const float VSPEC_LIT_NL_SCALE = 6.0;
const float VSPEC_FRESNEL_BASE = 0.5;
const float VSPEC_FRESNEL_SCALE = 0.4;
const float VSPEC_EPS = 0.000001;

vec3 vspec_linear_to_srgb(vec3 c) {
    c = clamp(c, vec3(0.0), vec3(1.0));
    vec3 low_range = c * vec3(12.92);
    vec3 high_range = vec3(1.055) * pow(c, vec3(1.0 / 2.4)) - vec3(0.055);
    return mix(high_range, low_range, lessThanEqual(c, vec3(0.0031308)));
}

void vertex() {
    // The normal-map coordinate is derived from the SAME base UV, so it must be computed before
    // UV is overwritten by the diffuse placement.
    v_normal_uv = slng_normal_uv(UV, VERTEX, NORMAL);
    v_specular_uv = slng_specular_uv(UV, VERTEX, NORMAL);
    UV = slng_transform_uv(UV, VERTEX, NORMAL);
    v_view_position = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    vec3 albedo;
    float metallic;
    float roughness;
    float specular;
    vec3 normal_map;
    float normal_depth;
    vec3 emission;
    slng_shade(UV, v_view_position, NORMAL, albedo, metallic, roughness, specular, normal_map, normal_depth, emission);

    ALBEDO = albedo;
    METALLIC = metallic;
    ROUGHNESS = roughness;
    SPECULAR = specular;
    NORMAL_MAP = normal_map;
    NORMAL_MAP_DEPTH = normal_depth;
    EMISSION = emission;

    // What the viewer's lighting pass reads back for this face.
    v_vspec_rgb = vec3(0.0);
    v_vspec_gloss = 0.0;
    if (has_specular_texture) {
        // materialF.glsl:245-246 multiplies map and colour in sRGB, :315 linearises the product. The
        // sampler here is source_color (already linear), so go back to sRGB for the multiply.
        vec3 texel = texture(specular_texture, v_specular_uv).rgb;
        v_vspec_rgb = slng_srgb_to_linear(vspec_linear_to_srgb(texel) * specular_tint);

        // materialF.glsl:227: glossiness *= normal map alpha, per texel.
        float glossiness = specular_glossiness;
        if (has_normal_texture) {
            glossiness *= texture(normal_texture, has_normal_uv ? v_normal_uv : UV).a;
        }
        v_vspec_gloss = glossiness;
    }
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
