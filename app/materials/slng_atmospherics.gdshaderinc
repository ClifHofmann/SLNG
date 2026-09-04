// Atmospherics seam for SLNG's world surfaces (FEAT-RENDER-01, ADR 0002, ADR 0003).
//
// Shared by prims, avatars, terrain, and water.
// Windlight / EEP atmospheric scattering is applied here per-fragment in Phase 5.

global uniform float slng_haze_density;
global uniform float slng_density_multiplier;
global uniform float slng_distance_multiplier;
global uniform vec4 slng_haze_color;

vec3 slng_apply_atmospherics(vec3 color, vec3 view_position) {
    float d = length(view_position);
    
    // Windlight / EEP exponential haze density
    float density = slng_haze_density * slng_density_multiplier * slng_distance_multiplier;
    
    // Godot space scale factor (SL distances to Godot world scale for exponential falloff)
    float fog_factor = exp(-d * density);
    
    // Mix the pixel's original color with the atmospheric horizon haze
    return mix(slng_haze_color.rgb, color, clamp(fog_factor, 0.0, 1.0));
}
