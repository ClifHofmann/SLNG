shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_back, specular_schlick_ggx;

uniform vec4 albedo : source_color = vec4(0.0, 0.3, 0.5, 0.85);

// repeat_enable is explicit and load-bearing: the wave UVs below are world-space metres scaled
// down, so they run far outside 0..1 across a region. The viewer sets the same address mode on
// this exact texture by hand (llviewertexturelist.cpp -- setAddressMode(TAM_WRAP)).
uniform sampler2D slng_water_normal_map : hint_normal, filter_linear_mipmap, repeat_enable;
uniform float slng_water_fog_density = 2.0;
uniform float slng_water_fresnel_scale = 0.4;
uniform float slng_water_fresnel_offset = 0.5;
uniform float slng_water_scale_above = 0.03;
uniform float slng_water_scale_below = 0.2;
uniform float slng_water_blur_multiplier = 0.04;
uniform vec3 slng_water_normal_scale = vec3(2.0, 2.0, 2.0);
uniform vec2 slng_water_wave1_dir = vec2(1.05, -0.42);
uniform vec2 slng_water_wave2_dir = vec2(1.11, -1.16);

void fragment() {
    // Deep water color (FogColor)
    ALBEDO = albedo.rgb;
    
    // In SL, reflection blur is defined by BlurMultiplier
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
    
    // Animate UVs based on SL's two wave directions
    // SL wave directions are typically scaled down for time
    float time_scale = 0.05;
    
    // Convert view space VERTEX to world space to keep UV scale independent of mesh size
    vec3 world_pos = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
    
    // Scale UVs using the EEP scale parameters
    // In SL, scale_above typically determines the base size of waves
    vec2 base_uv = world_pos.xz * slng_water_scale_above * slng_water_normal_scale.xy;
    
    vec2 uv1 = base_uv + (slng_water_wave1_dir * TIME * time_scale);
    vec2 uv2 = base_uv + (slng_water_wave2_dir * TIME * time_scale);
    
    // Sample the normal map twice
    vec3 n1 = texture(slng_water_normal_map, uv1).rgb * 2.0 - 1.0;
    vec3 n2 = texture(slng_water_normal_map, uv2).rgb * 2.0 - 1.0;
    
    // Combine normals
    vec3 combined_normal = normalize(n1 + n2);
    
    // Assign tangent-space normal
    NORMAL_MAP = combined_normal * 0.5 + 0.5;
    NORMAL_MAP_DEPTH = slng_water_normal_scale.z;
    
    // Base transparency (Godot handles PBR reflection opacity automatically)
    ALPHA = 0.9;
}
