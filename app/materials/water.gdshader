shader_type spatial;
// depth_draw_opaque, NOT depth_draw_always. Every other shader in this family (prim_opaque,
// prim_blend, prim_scissor and all their _avatar/_hud variants) uses depth_draw_opaque -- water
// was the one outlier, with no comment ever explaining why, and no other code depending on it
// (the "declared but unused until the refraction pass exists" screen-space uniforms above don't
// read the depth buffer yet). Reported live on Aditi as "the water plane overwrites textures" at
// every tree crossing the waterline; user follow-up ruled out SL system trees -- these are MESH
// trees with alpha-blended leaf textures. depth_draw_always forces water to write depth
// UNCONDITIONALLY regardless of Godot's transparent-queue sort order, which is only an
// approximate (AABB-distance) ordering between separate transparent objects, not a guaranteed
// per-pixel one. Wherever a large alpha-blended mesh (a tree canopy) happened to render in the
// wrong order relative to the water plane, water's forced depth write could incorrectly fail the
// depth test for leaf fragments that were actually meant to blend, not be occluded -- exactly a
// band where the canopy appeared to be "cut out" by the water. depth_draw_opaque only writes
// depth where water is genuinely opaque-looking, matching how every other blended surface in this
// project already coexists with the others. Revisit if a screen-space refraction/underwater-fog
// pass is ever built and needs water reliably present in the depth buffer.
render_mode blend_mix, depth_draw_opaque, cull_back, specular_schlick_ggx;

#include "res://materials/slng_atmospherics.gdshaderinc"

// SL's water wave field, ported from the viewer (FEAT-ENV-01).
//
// Sources, all in scratch/slviewer:
//   indra/newview/app_settings/shaders/class1/environment/waterV.glsl  -- the three wave UV sets
//   indra/newview/app_settings/shaders/class3/environment/waterF.glsl  -- how they combine
//   indra/newview/lldrawpoolwater.cpp:225-290                          -- what each uniform is fed
//
// class1/environment/waterF.glsl is NOT the legacy water fragment shader -- it is a magenta
// "compilation failed" fallback. The one shader the viewer actually runs for water is the class3
// one, with a `classic_mode` uniform selecting legacy behaviour inside it. Do not go looking for a
// class1 water fragment path; there isn't one.

uniform vec4 albedo : source_color = vec4(0.0, 0.3, 0.5, 0.85);

// repeat_enable is explicit and load-bearing: the wave UVs below are world-space metres scaled
// down, so they run far outside 0..1 across a region. The viewer sets the same address mode on
// this exact texture by hand (llviewertexturelist.cpp -- setAddressMode(TAM_WRAP)).
//
// filter_nearest, NOT mipmapped: lldrawpoolwater.cpp:150 picks TFO_POINT unless RenderWaterMipNormal
// is on (it ships off). Mipmapping the ripple normal averages the high-frequency detail to a flat
// vector at any grazing distance -- which is most of the visible sea -- so the surface reads as a
// mirror ("das Wasser ist zu glatt / weich"). Point sampling keeps the ripple structure at range;
// the aliasing that produces IS the classic SL water shimmer.
uniform sampler2D slng_water_normal_map : hint_normal, filter_nearest, repeat_enable;
uniform float slng_water_fog_density = 2.0;
uniform float slng_water_fresnel_scale = 0.4;
uniform float slng_water_fresnel_offset = 0.5;

// The viewer's `refScale` (WATER_REFSCALE): the magnitude of the screen-space REFRACTION offset,
// picked per frame from scale_above or scale_below depending on whether the camera is under the
// surface (lldrawpoolwater.cpp:285-290). It is NOT a UV tiling factor. fragment() uses
// scale_above as the ripple amount for the slng_screen_tex sample (waterF.glsl:244); scale_below
// stays declared for the underwater path, which does not exist yet.
uniform float slng_water_scale_above = 0.03;
uniform float slng_water_scale_below = 0.2;

uniform float slng_water_blur_multiplier = 0.04;

// The viewer's `screenTex` (WATER_SCREENTEX -> gPipeline.mWaterDis): a copy of the frame rendered
// so far -- sky and opaque geometry -- taken in LLDrawPoolWater::beginPostDeferredPass. class3
// waterF.glsl samples it at the water pixel's own screen position, nudged by a wave-driven ripple,
// and that sample IS the "see-through": `color = mix(fb, radiance, df2.x)`, written OPAQUE with
// GL_BLEND disabled. It is what makes water and sky share a colour at the horizon without either
// shader knowing the other's numbers -- the sky pixel behind the water is already atmospherically
// hazed, and the water just tints it. Godot's equivalent is the screen texture (same as
// ui_blur.gdshader); water draws in the transparent pass at RenderPriority -1, so this holds the
// opaque + sky frame, before any foliage.
uniform sampler2D slng_screen_tex : hint_screen_texture, filter_linear_mipmap;

// The viewer's `normScale`. It does NOT tile the normal map and it is NOT a bump depth -- it
// shapes the normal used for the reflection sample only (waterF.glsl `wave_ibl`). Both of the
// previous uses here were inventions.
uniform vec3 slng_water_normal_scale = vec3(2.0, 2.0, 2.0);

uniform vec2 slng_water_wave1_dir = vec2(1.05, -0.42);
uniform vec2 slng_water_wave2_dir = vec2(1.11, -1.16);

// The three wave UV sets, generated per vertex exactly as waterV.glsl does. Per vertex, not per
// fragment, and that is deliberate: the sweep term below is non-linear, so the viewer's coarse
// water mesh piecewise-linearises it. TerrainRenderer subdivides the water plane to the viewer's
// own 8 quads per 256 m to keep that sampling identical.
varying vec2 v_big_wave;
varying vec4 v_little_wave;
varying vec3 v_view_position;

void vertex() {
    // SL region metres: x = east, y = north. Godot world x = SL x, Godot -z = SL y.
    vec3 world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    vec2 v = vec2(world_pos.x, -world_pos.z);
    v_view_position = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;

    // waterV.glsl:87 -- "create sweeping horizontal waves". Displaces the wave field, not the
    // geometry; period is 2*pi/0.08 ~= 78.5 m in x with a +-6 m amplitude.
    v.x += (cos(v.x * 0.08) + sin(v.y * 0.02)) * 6.0;

    // lldrawpoolwater.cpp:180 -- phase_time is elapsed seconds * 0.5, so every rate below is half
    // the literal in waterV.glsl.
    float t = TIME * 0.5;

    v_big_wave      = v * vec2(0.04, 0.04) + slng_water_wave1_dir * t * 0.055;
    v_little_wave.xy = v * vec2(0.45, 0.9) + slng_water_wave2_dir * t * 0.13;
    v_little_wave.zw = v * vec2(0.1, 0.2)  + slng_water_wave1_dir * t * 0.1;
}

void fragment() {
    // waterF.glsl generateWaveNormals(): the SAME normal map sampled at all three UV sets. The
    // viewer optionally blends a second map (bumpMap2/blend_factor) while the day cycle crosses a
    // water keyframe; WaterSettings.Lerp takes the nearer keyframe's map instead, which is a
    // documented simplification and invisible on a region with one water frame. Tangent-space
    // normals; for the flat water plane tangent space IS SL world space (east, north, up).
    vec3 wave1 = texture(slng_water_normal_map, v_big_wave).xyz * 2.0 - 1.0;
    vec3 wave2 = texture(slng_water_normal_map, v_little_wave.xy).xyz * 2.0 - 1.0;
    vec3 wave3 = texture(slng_water_normal_map, v_little_wave.zw).xyz * 2.0 - 1.0;

    // waterF.glsl:208. Weighted, not an equal sum: the big wave carries full weight, the two
    // detail waves 0.4 and 0.6.
    vec3 wavef = (wave1 + wave2 * 0.4 + wave3 * 0.6) * 0.5;

    // waterF.glsl:226-228. normScale scales the tangent-space normal and the z component is then
    // doubled on top, so the viewer's default (2,2,2) actually FLATTENS the surface relative to
    // wavef -- xy:z ends up 2:4. This is the normal the reflection is sampled along.
    vec3 wave_ibl = wavef * slng_water_normal_scale;
    wave_ibl.z *= 2.0;
    wave_ibl = normalize(wave_ibl);

    // The viewer's transform_normal() maps tangent space through vT/vB/vN, which for the water
    // plane are east / north / up -- i.e. it is the identity in SL world space. So wave_ibl is
    // already an SL-space normal here and only the axis convention has to change:
    // SL (east, north, up) -> Godot (east, up, -north).
    //
    // Set as NORMAL in view space rather than via NORMAL_MAP on purpose: NORMAL_MAP would route
    // this through Godot's own tangent frame for PlaneMesh, whose binormal sign is not guaranteed
    // to be SL's north, and a silent Y flip lights every wave from the wrong side.
    vec3 n_godot = vec3(wave_ibl.x, wave_ibl.z, -wave_ibl.y);
    NORMAL = normalize((VIEW_MATRIX * vec4(n_godot, 0.0)).xyz);

    // --- fresnel factor df2, ported from waterF.glsl calculateFresnelFactors() (lines 162-179) ---
    // viewVec = normalize(world-space eye->fragment); wave1..3/wavef are the tangent-space bump
    // normals, dotted directly since tangent space == SL world for the flat plane. In view space
    // the eye is the origin, so eye->frag is normalize(v_view_position); rotate to Godot world
    // with INV_VIEW_MATRIX, then to SL world. fresnelOffset 0.5 is NOT "half reflective head-on":
    // straight down the wave dot is ~ -1 so (dot*scale+offset) ~ 0.1, squared ~ 0.01, x3 ~ 0.03;
    // toward the horizon df2.x climbs to ~0.75. Cross-checked against scratch/slviewer.
    vec3 eye_to_frag_view  = normalize(v_view_position);
    vec3 eye_to_frag_world = normalize((INV_VIEW_MATRIX * vec4(eye_to_frag_view, 0.0)).xyz);
    vec3 view_sl = vec3(eye_to_frag_world.x, -eye_to_frag_world.z, eye_to_frag_world.y);

    vec3 df3 = max(vec3(0.0), vec3(
        dot(view_sl, wave1),
        dot(view_sl, (wave2 + wave3) * 0.5),
        dot(view_sl, wave3)
    ) * slng_water_fresnel_scale + slng_water_fresnel_offset);
    df3 *= df3;
    float df2_x = min(1.0, df3.x + df3.y + df3.z);   // reflection <-> refraction mix
    float df2_y = max(0.0, dot(view_sl, wavef) * slng_water_fresnel_scale + slng_water_fresnel_offset);

    // --- fb: the scene behind the water, from the screen, rippled -------------------------------
    // waterF.glsl:206/244: distort = this pixel's screen UV; distort2 = distort + waver.xy *
    // refScale / max(sqrt(dist), 1) * 2, waver = wavef * 3, refScale = scale_above. SL world
    // (east=x, north=y) maps to screen (x, -y) since Godot's screen V runs top-down.
    float dist = max(length(v_view_position), 5.0);
    vec2 ripple = (wavef.xy * 3.0) * slng_water_scale_above / max(sqrt(dist), 1.0) * 2.0;
    vec2 distort2 = clamp(SCREEN_UV + vec2(ripple.x, -ripple.y), vec2(0.0), vec2(1.0));
    vec3 fb = textureLod(slng_screen_tex, distort2, 0.0).rgb;

    // --- sun glitter: waterF.glsl `punctual` (306-323). The glint normal is waterF.glsl:321's
    // normalize(wavef + up * max(dist,32)/32 * (1 - vdu)) -- the RAW wave normal blended toward
    // vertical with distance; vdu = clamp(-dot(viewVec, up) * 2, 0, 1) so a straight-down view
    // keeps the raw facet and a grazing one flattens (which, for a low sun viewed toward it where
    // half_v is near vertical, spreads the glitter into the sunset path). dist/32 capped so it
    // does not run away on the void plane.
    vec3 up_view  = normalize((VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz);
    float vdu     = clamp(-dot(normalize(v_view_position), up_view) * 2.0, 0.0, 1.0);
    // Cap the up-blend low: the viewer's dist/32 runs to 6+ but its razor GGX still resolves flecks
    // from a near-vertical normal. Ours would just wash the whole up-facing region white, so keep
    // more of the wave-normal variation to break the glitter into a fleck field.
    float flatten = clamp(max(dist, 32.0) / 64.0, 0.0, 1.5) * (1.0 - vdu);
    vec3 glint_sl = normalize(wavef + vec3(0.0, 0.0, 1.0) * flatten);
    vec3 glint_n  = normalize((VIEW_MATRIX * vec4(glint_sl.x, glint_sl.z, -glint_sl.y, 0.0)).xyz);
    // atten.r for the glint's distance falloff (waterF.glsl:323 punctual *= atten). color = 1 in,
    // so slng_apply_atmospherics returns atten.r; the in-scatter out-param is unused here.
    vec3 glint_scratch;
    float atten_r = clamp(slng_apply_atmospherics(vec3(1.0), v_view_position, glint_scratch).r, 0.0, 1.0);
    vec3 glint = slng_water_sun_glint(glint_n, v_view_position, slng_water_blur_multiplier * 2.0, atten_r) * 0.6;

    // --- composite: waterF.glsl:324-327  color = mix(fb, radiance * df2.y, min(1, df2.x)) + punctual
    //             =  fb * (1 - df2.x)  +  radiance * df2.y * df2.x  +  glint
    // radiance is the sky reflection along the wave normal -- Godot's METALLIC = 1 BRDF supplies it
    // along NORMAL at ROUGHNESS, tinted by ALBEDO (the greyscale weight df2.x * df2.y). fb *
    // (1 - df2.x) and glint are finished pixels, so they go through EMISSION.
    ALBEDO   = vec3(df2_x * df2_y);
    METALLIC = 1.0;
    // waterF.glsl:290  perceptualRoughness = blurMultiplier, and lldrawpoolwater.cpp:254 feeds
    // that uniform max(0, BlurMultiplier) * 2 -- the doubling is the viewer's. Kept sharp so the
    // wave normals break up the reflection.
    ROUGHNESS = clamp(slng_water_blur_multiplier * 2.0, 0.0, 1.0);
    EMISSION = fb * (1.0 - df2_x) + glint;

    // The viewer writes water OPAQUE (LLDrawPoolWater::renderPostDeferred disables GL_BLEND); the
    // see-through is `fb`, composited above, not framebuffer blending. Stay in the transparent
    // pass though (blend_mix + RenderPriority -1) so slng_screen_tex holds the frame behind.
    ALPHA = 1.0;
}
