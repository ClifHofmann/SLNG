shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_back, specular_schlick_ggx;

uniform vec4 albedo : source_color = vec4(0.0, 0.3, 0.5, 0.8);
uniform float roughness : hint_range(0.0, 1.0) = 0.1;
uniform float metallic : hint_range(0.0, 1.0) = 0.1;
uniform float wave_speed = 0.5;

void fragment() {
    ALBEDO = albedo.rgb;
    ALPHA = albedo.a;
    ROUGHNESS = roughness;
    METALLIC = metallic;
    
    // Simple wave animation on normals
    vec2 uv_moved = UV + vec2(TIME * wave_speed * 0.05, TIME * wave_speed * 0.03);
    
    // Fake normal bumping based on sin waves
    float bump = sin(uv_moved.x * 50.0) * cos(uv_moved.y * 50.0) * 0.1;
    NORMAL = normalize(vec3(bump, 1.0, bump));
}
