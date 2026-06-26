shader_type spatial;

uniform sampler2D detail0 : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D detail1 : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D detail2 : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D detail3 : source_color, filter_linear_mipmap, repeat_enable;

// Heights for the 4 corners: SW, NW, SE, NE
uniform vec4 start_height = vec4(10.0, 10.0, 10.0, 10.0);
uniform vec4 height_range = vec4(60.0, 60.0, 60.0, 60.0);

uniform float uv_scale = 0.2;

varying float v_height;
varying vec3 v_normal_world;
varying vec2 v_global_uv;

void vertex() {
    v_height = VERTEX.y;
    
    // In Godot, VERTEX in vertex shader is local to the mesh.
    // If our terrain mesh is positioned at 0,0, global coords are the same as local coords.
    // X goes 0..256, Z goes 0..256 (or negative, depending on orientation).
    // Let's assume VERTEX.x and VERTEX.z are the world coordinates.
    // We normalize to 0..1 for corner interpolation.
    v_global_uv = vec2(clamp(VERTEX.x / 256.0, 0.0, 1.0), clamp(-VERTEX.z / 256.0, 0.0, 1.0));
    
    // Pass normal to fragment shader (approximate global normal)
    v_normal_world = NORMAL;
}

void fragment() {
    // Generate UVs based on world coordinates passed from vertex shader
    // (VERTEX in fragment shader is in view space, which moves with the camera)
    vec2 detail_uv = v_global_uv * 256.0 * uv_scale;

    vec4 t0 = texture(detail0, detail_uv);
    vec4 t1 = texture(detail1, detail_uv);
    vec4 t2 = texture(detail2, detail_uv);
    vec4 t3 = texture(detail3, detail_uv);

    // Bilinear interpolation of start_height and height_range across the 4 corners
    // index 0: SW (0,0), 1: NW (0,1), 2: SE (1,0), 3: NE (1,1)
    float s0 = mix(start_height[0], start_height[1], v_global_uv.y);
    float s1 = mix(start_height[2], start_height[3], v_global_uv.y);
    float s = mix(s0, s1, v_global_uv.x);

    float r0 = mix(height_range[0], height_range[1], v_global_uv.y);
    float r1 = mix(height_range[2], height_range[3], v_global_uv.y);
    float r = mix(r0, r1, v_global_uv.x);

    // Calculate weights based on height
    // t0: < s
    // t1: s to s+r
    // t2: > s+r
    float w0 = 1.0 - smoothstep(s - 2.0, s + 2.0, v_height);
    float w1 = smoothstep(s - 2.0, s + 2.0, v_height) * (1.0 - smoothstep(s + r - 2.0, s + r + 2.0, v_height));
    float w2 = smoothstep(s + r - 2.0, s + r + 2.0, v_height);

    vec3 flat_color = t0.rgb * w0 + t1.rgb * w1 + t2.rgb * w2;

    // Calculate cliff weight based on slope
    // v_normal_world.y is 1.0 on flat ground, approaches 0 on vertical cliffs
    float slope = 1.0 - v_normal_world.y;
    
    // If slope > 0.35, it starts blending into cliff texture (t3)
    float cliff_weight = smoothstep(0.35, 0.55, slope);

    ALBEDO = mix(flat_color, t3.rgb, cliff_weight);
    
    // Add a bit of roughness to terrain
    ROUGHNESS = 0.9;
    METALLIC = 0.0;
}
