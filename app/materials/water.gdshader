shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_back, specular_schlick_ggx;

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
uniform sampler2D slng_water_normal_map : hint_normal, filter_linear_mipmap, repeat_enable;
uniform float slng_water_fog_density = 2.0;
uniform float slng_water_fresnel_scale = 0.4;
uniform float slng_water_fresnel_offset = 0.5;

// The viewer's `refScale`: the scale of the screen-space REFRACTION offset, picked per frame from
// scale_above or scale_below depending on whether the camera is under the surface
// (lldrawpoolwater.cpp:285-290). It is NOT a UV tiling factor. This shader had it multiplying the
// wave UVs, which is why the ripples were ~33x too large: scale_above defaults to 0.0299 and was
// standing in for the viewer's 0.04/0.45/0.1 wave scales. Declared but unused until the refraction
// pass exists.
uniform float slng_water_scale_above = 0.03;
uniform float slng_water_scale_below = 0.2;

uniform float slng_water_blur_multiplier = 0.04;

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

void vertex() {
    // SL region metres: x = east, y = north. Godot world x = SL x, Godot -z = SL y.
    vec3 world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    vec2 v = vec2(world_pos.x, -world_pos.z);

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
    // Deep water color (FogColor)
    ALBEDO = albedo.rgb;

    // waterF.glsl: perceptualRoughness = blurMultiplier, and lldrawpoolwater.cpp:254 feeds that
    // uniform max(0, BlurMultiplier) * 2 -- so the doubling is the viewer's, not ours.
    ROUGHNESS = clamp(slng_water_blur_multiplier * 2.0, 0.0, 1.0);
    // Water is a DIELECTRIC. SL composites it as a fresnel-weighted mix between the refracted,
    // fog-coloured view through the water and the reflection (waterF.glsl:327):
    //     color = mix(fb.rgb, radiance, min(1, df2.x)) + punctual.rgb;
    // -- `fb` carries water_fog_color, `radiance` is the reflection, `df2.x` the fresnel term.
    //
    // This was 0.95 "to mimic SL's sky reflection". In a PBR renderer that makes ALBEDO the
    // SPECULAR TINT and removes the diffuse term almost entirely, so the region's own water colour
    // stops being visible and the surface just shows the sky back. Measured against Firestorm on
    // Howletts 2026-08-21: FS rendered deep blue water, ours near-white grey, with an identical
    // water_fog_color of (0.0157, 0.149, 0.251).
    //
    // At 0 the fog colour reads again and Godot's own Schlick term still gives a grazing-angle
    // reflection. Shaping that with the region's fresnel_scale / fresnel_offset is the next step
    // and deliberately not bundled here -- one variable at a time.
    METALLIC = 0.0;

    // waterF.glsl generateWaveNormals(): the SAME normal map sampled at all three UV sets. The
    // viewer optionally blends a second map (bumpMap2/blend_factor) while the day cycle crosses a
    // water keyframe; WaterSettings.Lerp takes the nearer keyframe's map instead, which is a
    // documented simplification and invisible on a region with one water frame.
    vec3 wave1 = texture(slng_water_normal_map, v_big_wave).xyz * 2.0 - 1.0;
    vec3 wave2 = texture(slng_water_normal_map, v_little_wave.xy).xyz * 2.0 - 1.0;
    vec3 wave3 = texture(slng_water_normal_map, v_little_wave.zw).xyz * 2.0 - 1.0;

    // waterF.glsl:200. Weighted, not an equal sum: the big wave carries full weight, the two
    // detail waves 0.4 and 0.6.
    vec3 wavef = (wave1 + wave2 * 0.4 + wave3 * 0.6) * 0.5;

    // waterF.glsl:216-218. normScale scales the tangent-space normal and the z component is then
    // doubled on top, so the viewer's default (2,2,2) actually FLATTENS the surface relative to
    // wavef -- xy:z ends up 2:4.
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

    // Base transparency (Godot handles PBR reflection opacity automatically)
    ALPHA = 0.9;
}
