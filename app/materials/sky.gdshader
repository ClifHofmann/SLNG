shader_type sky;
render_mode use_half_res_pass;

// Windlight global uniforms (FEAT-ENV-01)
global uniform vec4 slng_ambient;
global uniform vec4 slng_blue_density;
global uniform vec4 slng_blue_horizon;
global uniform float slng_haze_density;
global uniform float slng_haze_horizon;
global uniform float slng_density_multiplier;
global uniform float slng_distance_multiplier;
global uniform float slng_max_y;
global uniform vec4 slng_glow;
global uniform float slng_cloud_shadow;

// 1.0 under the sun, moon_brightness * 0.25 under the moon, 0 when neither is up.
// llsettingssky.cpp:1317-1321 (getSunMoonGlowFactor).
global uniform float slng_sun_moon_glow_factor;

// Computed Lighting global uniforms
global uniform vec4 slng_sunlight_color;

// hint_default_TRANSPARENT, not black. The billboard code below decides whether a real texture is
// present by testing sampled alpha (`sun_tex.a > 0.01`), and Godot's default-black placeholder is
// OPAQUE black -- alpha 1.0. An unloaded sun therefore passed that test and blended in solid black
// over the billboard square, punching the procedural disk out of its own centre and leaving only
// the surrounding halo: the sun rendered as a soft SQUARE instead of a disc. Transparent black
// fails the test, so an absent texture correctly falls through to the procedural disk.
uniform sampler2D slng_sun_texture : hint_default_transparent;
uniform sampler2D slng_moon_texture : hint_default_transparent;

global uniform vec4 slng_ambient_color;
global uniform vec4 slng_moonlight_color;
global uniform vec4 slng_moon_ambient;
global uniform vec4 slng_haze_color;

// Celestial
global uniform float slng_star_brightness;
global uniform vec3 slng_sun_direction;
global uniform float slng_sun_scale;
global uniform float slng_moon_scale;

// Clouds
global uniform vec4 slng_cloud_color;
global uniform vec4 slng_cloud_pos_density1;
global uniform vec4 slng_cloud_pos_density2;
global uniform float slng_cloud_scale;
global uniform float slng_cloud_variance;
uniform sampler2D slng_cloud_texture : hint_default_black, filter_linear_mipmap, repeat_enable;

// Port of LLSettingsSky::calcAtmosphericVars and atmosphericsFuncs.glsl
// A simple 2D value noise for clouds
float hash21(vec2 p) {
    return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}
vec3 mod289_3(vec3 x) {
  return x - floor(x * (1.0 / 289.0)) * 289.0;
}

vec2 mod289_2(vec2 x) {
  return x - floor(x * (1.0 / 289.0)) * 289.0;
}

vec3 permute(vec3 x) {
  return mod289_3(((x * 34.0) + 10.0) * x);
}

float snoise(vec2 v) {
  const vec4 C = vec4(0.211324865405187, 0.366025403784439,
                     -0.577350269189626, 0.024390243902439);
  vec2 i  = floor(v + dot(v, C.yy));
  vec2 x0 = v - i + dot(i, C.xx);

  vec2 i1;
  i1 = (x0.x > x0.y) ? vec2(1.0, 0.0) : vec2(0.0, 1.0);
  vec4 x12 = x0.xyxy + C.xxzz;
  x12.xy -= i1;

  i = mod289_2(i);
  vec3 p = permute(permute(i.y + vec3(0.0, i1.y, 1.0))
                   + i.x + vec3(0.0, i1.x, 1.0));

  vec3 m = max(0.5 - vec3(dot(x0,x0), dot(x12.xy,x12.xy), dot(x12.zw,x12.zw)), 0.0);
  m = m * m;
  m = m * m;

  vec3 x = 2.0 * fract(p * C.www) - 1.0;
  vec3 h = abs(x) - 0.5;
  vec3 ox = floor(x + 0.5);
  vec3 a0 = x - ox;

  m *= 1.79284291400159 - 0.85373472095314 * (a0*a0 + h*h);

  vec3 g;
  g.x  = a0.x  * x0.x  + h.x  * x0.y;
  g.yz = a0.yz * x12.xz + h.yz * x12.yw;
  return 130.0 * dot(m, g);
}

void sky() {
    // SL uses Z up, Godot uses Y up.
    float view_y = max(0.001, EYEDIR.y);
    float rel_pos_len = slng_max_y / view_y; // Distance to the sky dome "edge"
    
    vec3 sun_dir_godot = vec3(0.0, 1.0, 0.0);
    if (length(slng_sun_direction) > 0.001) {
        sun_dir_godot = normalize(slng_sun_direction);
    }
    vec3 moon_dir_godot = -sun_dir_godot;
    
    // Light direction (using sun for day, moon for night if we had a full night cycle)
    vec3 light_dir = sun_dir_godot;
    if (sun_dir_godot.y < 0.0) light_dir = moon_dir_godot;
    
    // --- SL Atmospherics Math ---
    vec3 combined_haze = max(slng_blue_density.rgb + vec3(slng_haze_density), vec3(1e-6));
    vec3 blue_weight = slng_blue_density.rgb / combined_haze;
    vec3 haze_weight = vec3(slng_haze_density) / combined_haze;
    
    float density_dist = rel_pos_len * slng_density_multiplier;
    vec3 transmittance = exp(-combined_haze * density_dist * slng_distance_multiplier);
    
    // compute haze glow
    float haze_glow = dot(EYEDIR, light_dir);
    // restrict glow to the sky
    haze_glow *= max(0.0, dot(light_dir, EYEDIR));
    haze_glow = 1.0 - haze_glow;
    haze_glow = max(haze_glow, 0.001);
    haze_glow *= slng_glow.x;
    
    // glow.z in SL is often negative (e.g. -0.5) to invert the falloff.
    // We use abs(slng_glow.z) to avoid NaN on negative base, and invert if slng_glow.z < 0.
    float glow_exp = slng_glow.z;
    if (glow_exp < 0.0) {
        haze_glow = pow(haze_glow, glow_exp);
    } else {
        haze_glow = pow(haze_glow, glow_exp);
    }
    haze_glow = clamp(haze_glow, 0.0, 1000.0);

    // sun_moon_glow_factor gates the halo by which body is up: 1.0 under the sun,
    // moon_brightness * 0.25 under the moon, 0 when neither is up
    // (llsettingssky.cpp:1317-1321). Adding the 0.25 "minimum anti-solar illumination"
    // unconditionally, as this did, gave the NIGHT sky a solar halo the real viewer never draws.
    //
    // The sky and cloud paths genuinely differ and both are ported here rather than sharing one
    // value:
    //   sky    (atmosphericsFuncs.glsl:105-108) adds the minimum first, THEN scales -- so a dim
    //          moon still leaves a faint anti-solar term.
    //   clouds (cloudsF.glsl:146-150) scales first and then ZEROES outright whenever the factor
    //          is below 1. This is SL-13768: "For sun, add to glow. For moon, remove glow
    //          entirely."
    float haze_glow_sky = (haze_glow + 0.25) * slng_sun_moon_glow_factor;
    float haze_glow_cloud = (slng_sun_moon_glow_factor < 1.0)
        ? 0.0
        : (haze_glow * slng_sun_moon_glow_factor + 0.25);
    
    vec3 amb_color = slng_ambient_color.rgb;
    vec3 diff_color = slng_sunlight_color.rgb;
    if (sun_dir_godot.y < 0.0) {
        amb_color = slng_moon_ambient.rgb;
        diff_color = slng_moonlight_color.rgb;
    }
    
    // increase ambient when there are more clouds
    vec3 tmpAmbient = amb_color + (vec3(1.0) - amb_color) * slng_cloud_shadow * 0.5;
    
    // Original sunlight/moonlight (no artificial boost)
    vec3 cs = diff_color * (1.0 - slng_cloud_shadow);
    
    // calculate additive sky color
    vec3 additive = (slng_blue_horizon.rgb * blue_weight) * (cs + tmpAmbient) + 
                    (vec3(slng_haze_horizon) * haze_weight) * (cs * haze_glow_sky + tmpAmbient);
                    
    // Kept before the sky path attenuates `additive` below: SL feeds this same pre-attenuation
    // term up into the cloud layer as `additiveColorBelowCloud` (cloudsF.glsl:158-160), which is
    // how haze from beneath tints the underside of a cloud.
    vec3 additive_below_cloud = additive;

    additive *= vec3(1.0) - transmittance;
    additive = min(additive, vec3(10.0));
    // -----------------------------

    // Sun Disk (Procedural Glow + Optional Texture)
    float sun_dot = max(0.0, dot(EYEDIR, sun_dir_godot));
    float sun_falloff = max(10.0, 8000.0 / clamp(slng_glow.x * slng_sun_scale, 1.0, 100.0));
    float sun_disk = pow(sun_dot, sun_falloff);
    float sun_halo = pow(sun_dot, max(1.0, sun_falloff * 0.05)) * 0.25;
    
    // Attempt to map sun texture (Billboard projection)
    // We create a local tangent space around the sun direction to map a 2D texture
    vec3 sun_right = cross(vec3(0.0, 1.0, 0.0), sun_dir_godot);
    float sl = length(sun_right);
    if (sl < 0.001) {
        sun_right = vec3(1.0, 0.0, 0.0);
    } else {
        sun_right = sun_right / sl;
    }
    vec3 sun_up = cross(sun_dir_godot, sun_right);
    
    // Scale UV by sun size (roughly 0.1 radians)
    float sun_tex_scale = 10.0 / clamp(slng_sun_scale, 0.1, 10.0);
    vec2 sun_uv = vec2(dot(EYEDIR, sun_right), dot(EYEDIR, sun_up)) * sun_tex_scale + 0.5;
    vec4 sun_tex = texture(slng_sun_texture, sun_uv);
    
    // If texture is transparent or outside UV bounds, fall back to procedural disk
    float use_sun_tex = (sun_uv.x >= 0.0 && sun_uv.x <= 1.0 && sun_uv.y >= 0.0 && sun_uv.y <= 1.0 && sun_tex.a > 0.01) ? 1.0 : 0.0;
    vec3 final_sun = mix(vec3(sun_disk), sun_tex.rgb * sun_tex.a, use_sun_tex);
    vec3 sun_core = slng_sunlight_color.rgb * (final_sun + sun_halo) * 5.0; 
    
    // Moon Disk (Procedural Glow + Optional Texture)
    float moon_dot = max(0.0, dot(EYEDIR, moon_dir_godot));
    float moon_falloff = max(10.0, 2000.0 / clamp(slng_moon_scale * 5.0, 1.0, 100.0));
    float moon_disk = pow(moon_dot, moon_falloff);
    
    vec3 moon_right = cross(vec3(0.0, 1.0, 0.0), moon_dir_godot);
    float ml = length(moon_right);
    if (ml < 0.001) {
        moon_right = vec3(1.0, 0.0, 0.0);
    } else {
        moon_right = moon_right / ml;
    }
    vec3 moon_up = cross(moon_dir_godot, moon_right);
    
    float moon_tex_scale = 10.0 / clamp(slng_moon_scale, 0.1, 10.0);
    vec2 moon_uv = vec2(dot(EYEDIR, moon_right), dot(EYEDIR, moon_up)) * moon_tex_scale + 0.5;
    vec4 moon_tex = texture(slng_moon_texture, moon_uv);
    
    float use_moon_tex = (moon_uv.x >= 0.0 && moon_uv.x <= 1.0 && moon_uv.y >= 0.0 && moon_uv.y <= 1.0 && moon_tex.a > 0.01) ? 1.0 : 0.0;
    vec3 final_moon = mix(vec3(moon_disk), moon_tex.rgb * moon_tex.a, use_moon_tex);
    vec3 moon_core = slng_moonlight_color.rgb * final_moon * 2.0;

    // Basic Stars
    float star_noise = hash21(EYEDIR.xz * 500.0);
    float star_val = smoothstep(0.995, 1.0, star_noise) * slng_star_brightness * max(0.0, -sun_dir_godot.y);
    vec3 stars = vec3(star_val);
    
    // Cloud dome UV. SL does not compute this in a shader at all -- cloudsV.glsl consumes the
    // cloud dome MESH's own texcoord0, generated once in LLVOWLSky::buildStripsBuffer
    // (llvowlsky.cpp:430) as
    //
    //     texCoord = vec2((-z0 + 1) / 2, (-x0 + 1) / 2)
    //
    // where (x0, y0, z0) is the UNIT direction to that dome vertex. x and z are transposed on
    // purpose, so cloud drift animates correctly in the global frame where +x is east and +y is
    // north (the viewer says so in that comment). We have no dome mesh, so this reproduces the
    // same function of view direction directly.
    //
    // What this replaces: a `/ max(EYEDIR.y, 0.02)` cube-face projection with a hand-fitted
    // `* 0.05` in front of it. Dividing by the vertical component is the part SL does NOT do --
    // its UV is a plain planar read of the normalised direction. The division made the UV span
    // collapse toward the zenith and blow up at the horizon, so the scale had to be fudged, and
    // the fudge left the UV range roughly 10x too small: few texture repeats across the sky,
    // i.e. clouds far coarser than Firestorm's at the same cloud_scale. Measured against FS on
    // Howletts 2026-08-21 with the neutral parity preset -- ours rendered heavy blobs where FS
    // showed fine wisps.
    //
    // The old comment claimed SL's dome "only covers the middle 38% of the texture". That is not
    // what the source does: (-z + 1) / 2 maps the full direction range onto the full 0..1 UV.
    // Cloud dome UV, derived rather than fitted. Three steps, all from source:
    //
    // 1. LLVOWLSky::buildStripsBuffer (llvowlsky.cpp:430) gives each dome vertex
    //        texCoord = ((-z0 + 1) / 2, (-x0 + 1) / 2)
    //    from its UNIT direction (x0, y0, z0), in the dome's own Y-UP local frame.
    //
    // 2. renderDome (lldrawpoolwlsky.cpp:115) then rotates the dome by 120 degrees about
    //    (1,1,1)/sqrt(3). That is a cyclic axis permutation -- checked with Rodrigues:
    //        R*(1,0,0) = (-0.5,0,0) + (0,0.5,-0.5) + (0.5,0.5,0.5) = (0,1,0)
    //    so dome x -> world y, dome y -> world z, dome z -> world x. The dome's local up (y)
    //    becoming SL's up (z) confirms the direction of the cycle.
    //    Therefore, in SL WORLD terms:  u = (-world_x + 1)/2,  v = (-world_y + 1)/2.
    //    The viewer says as much in that same function: "x and z are transposed in order for
    //    things to animate correctly in the global coordinate system where +x is east and +y is
    //    north."
    //
    // 3. SL world -> Godot is our own (x, z, -y) convention, so world_x = EYEDIR.x and
    //    world_y = -EYEDIR.z.
    //
    // Two earlier attempts got this wrong in opposite directions and are worth naming, because
    // both looked plausible:
    //   * Reading the formula straight off EYEDIR without step 2 SWAPS THE TWO UV AXES, which
    //     rotates the foreshortening by 90 degrees -- clouds stretch into vertical columns.
    //   * "Fixing" that by dividing by EYEDIR.y and fitting a scale in front of it replaces the
    //     projection with a different one entirely, and then the scale has to absorb the error.
    // The camera-height offset renderDome applies (`translatef(0, -camHeightLocal, 0)` before a
    // 0.333 scale) is genuinely negligible here: dome_radius defaults to 15000, so at an avatar
    // altitude of 22-100 m the ratio is 0.0015-0.0067 of the dome radius.
    float world_x = EYEDIR.x;    // SL +x, east
    float world_y = -EYEDIR.z;   // SL +y, north
    vec2 dome_uv = vec2((-world_x + 1.0) * 0.5, (-world_y + 1.0) * 0.5);

    // SL-13084: cloud textures are flipped horizontally to match the Clouds > Cloud Scroll
    // preview (cloudsV.glsl). Kept in sync with the X negation on the scroll delta in
    // EnvironmentDriver.
    vec2 vary_texcoord0 = vec2(-dome_uv.x, dome_uv.y);

    // Centred divide, in cloudsV.glsl's order.
    vary_texcoord0 -= 0.5;
    vary_texcoord0 /= max(slng_cloud_scale, 0.001);
    vary_texcoord0 += 0.5;

    vec2 base_uv = vary_texcoord0;
    
    vec2 uv1 = base_uv;
    vec2 uv2 = base_uv;
    uv2.x += LIGHT0_DIRECTION.x * 0.0125;
    uv2.y += LIGHT0_DIRECTION.z * 0.0125;
    
    vec2 uv3 = uv1 * 16.0;
    vec2 uv4 = uv2 * 16.0;
    
    // Modulo 10000.0 to prevent floating-point precision loss
    vec2 offset1 = mod(slng_cloud_pos_density1.xy, vec2(10000.0));
    vec2 offset2 = mod(slng_cloud_pos_density2.xy, vec2(10000.0));
    
    float alpha1 = 0.0;
    // Self-shadow term. 0 means "fully sunlit", which is the right neutral value for the
    // no-texture fallback branch below, where there is no noise to shadow against.
    float alpha2 = 0.0;

    vec2 tex_size = vec2(textureSize(slng_cloud_texture, 0));
    if (tex_size.x > 1.0) {
        // Domain warping via lower frequency noise
        vec2 dist1 = vec2(texture(slng_cloud_texture, uv1 / 8.0).r, texture(slng_cloud_texture, (uv3 + uv1) / 16.0).r);
        vec2 dist2 = vec2(texture(slng_cloud_texture, (uv1 + uv3) / 4.0).r, texture(slng_cloud_texture, (uv4 + uv2) / 8.0).r);
        
        vec2 disturbance = dist1 * slng_cloud_variance * (1.0 - slng_cloud_scale * 0.25);
        vec2 disturbance2 = dist2 * slng_cloud_variance * (1.0 - slng_cloud_scale * 0.25);
        
        uv1 += offset1 + (disturbance * 0.2);
        uv2 += offset1;
        uv3 += offset2;
        uv4 += offset2;
        
        float density_variance = min(1.0, (disturbance.x * 2.0 + disturbance.y * 2.0 + disturbance2.x + disturbance2.y) * 4.0);

        // Cloud cover comes from CLOUD_SHADOW, not from the two pos_density z components
        // (cloudsF.glsl:179 -- "Make a nice cloud density based on the cloud_shadow value").
        // Those two z values already have their own, different jobs further down: density2.z
        // weights the second noise octave and density1.z is the coefficient of the *10 term.
        // Summing them here as if they were the cover amount double-used both and blew the
        // figure up ~25x on a normal sky (cloud_shadow 0.27 -> SL 0.04, this -> 1.0), which
        // saturates alpha1 to 1.0 for every fragment: a seamless opaque lid over the whole sky
        // with no shape to it at all. That does not read as heavy cloud, it reads as haze --
        // reported as "no clouds at all".
        float cloud_density = 2.0 * (slng_cloud_shadow - 0.25);
        cloud_density *= 1.0 - (density_variance * density_variance);
        
        alpha1 = (texture(slng_cloud_texture, uv1).r - 0.5) + (texture(slng_cloud_texture, uv3).r - 0.5) * slng_cloud_pos_density2.z;
        alpha1 = min(max(alpha1 + cloud_density, 0.0) * 10.0 * slng_cloud_pos_density1.z, 1.0);

        // Self-shadowing (cloudsF.glsl:210-217). uv2 is the same noise sampled slightly offset
        // TOWARDS the sun, so (1 - alpha2) is how much sunlight reaches this fragment -- the term
        // that makes a cloud look like a lit volume instead of a flat cut-out. uv2 was already
        // built and offset for exactly this purpose at the top of this block, and then never
        // sampled, which is the main reason our clouds read as flat next to the real viewer's.
        // Note the 2.5 here against alpha1's 10.0, and the single (not doubled) smoothing step --
        // both are SL's own values.
        alpha2 = (texture(slng_cloud_texture, uv2).r - 0.5);
        alpha2 = min(max(alpha2 + cloud_density, 0.0) * 2.5 * slng_cloud_pos_density1.z, 1.0);
        alpha2 = 1.0 - alpha2;
        alpha2 = 1.0 - alpha2 * alpha2;
    } else {
        // Basic fallback without texture
        float f = 0.0; float w = 0.5; vec2 p1 = (base_uv + offset1) * 0.01;
        for (int i = 0; i < 4; i++) { f += w * (snoise(p1) * 0.5 + 0.5); p1 *= 2.0; w *= 0.5; }
        float f2 = 0.0; float w2 = 0.5; vec2 p2 = (base_uv * 1.5 + offset2) * 0.01;
        for (int j = 0; j < 4; j++) { f2 += w2 * (snoise(p2) * 0.5 + 0.5); p2 *= 2.0; w2 *= 0.5; }
        
        float total_density = (slng_cloud_pos_density1.z + slng_cloud_pos_density2.z) * 0.5;
        float cloud_noise = smoothstep(0.45, 0.75, (f + f2 * 0.5) / 1.5);
        alpha1 = cloud_noise * min(total_density, 1.0);
    }
    
    // And smooth (matches SL cloudsF.glsl)
    alpha1 = 1.0 - alpha1 * alpha1;
    alpha1 = 1.0 - alpha1 * alpha1;
    
    // Apply altitude blend
    // We aggressively fade out clouds before the horizon to completely hide the geometrical 
    // equator distortion of the dome projection.
    float altitude_blend_factor = smoothstep(0.05, 0.2, EYEDIR.y);
    
    alpha1 *= altitude_blend_factor;
    alpha1 = clamp(alpha1, 0.0, 1.0);
    
    // Cloud colour, ported from cloudsF.glsl:168-182 and :221. TWO terms mixed by the self-shadow
    // above, not one flat blend:
    //   cloudColorSun     -- carries haze_glow, which is what turns cloud undersides orange near
    //                        a low sun. This is the term that was missing entirely, so our clouds
    //                        stayed neutral white through a sunset that the sky behind them was
    //                        already rendering warm.
    //   cloudColorAmbient -- the shadow side, plus haze bleeding up from below the layer.
    // Deliberately fed from diff_color rather than `cs`: SL dims sunlight by cloud_shadow for the
    // SKY, then resets it to the undimmed sunlight_color for the cloud term (cloudsF.glsl:163-166)
    // -- cloud cover must not shade the clouds casting it. slng_sunlight_color is already the
    // atmospherically attenuated value (SkyLighting.Calculate's output), so no further attenuation.
    // sqrt on the transmittance is SL's own: less atmospheric opacity below the cloud layer.
    vec3 cloud_atten = sqrt(clamp(transmittance, vec3(0.0), vec3(1.0)));
    vec3 cloudColorSun = (diff_color * haze_glow_cloud) * slng_cloud_color.rgb * cloud_atten;
    vec3 cloudColorAmbient = tmpAmbient * slng_cloud_color.rgb * cloud_atten
                           + additive_below_cloud * (vec3(1.0) - cloud_atten);
    // SL's own clamp (cloudsF.glsl:222), kept literally. Raising this ceiling to match the sky's
    // 10.0 was tried and was WRONG: cloudColorSun carries haze_glow, which line 135 permits up to
    // 1000, so without the LDR clamp the sunlit term overshoots massively and the whole upper sky
    // blows out to flat white -- clouds included. The clamp is not an artefact of SL compositing
    // clouds in a separate pass, it is part of the algorithm: it is what bounds cloud brightness
    // so the noise still reads as shape. If clouds look too weak against our sky, the fault is
    // the SKY term being too bright, not this bound.
    vec3 cloud_col = clamp(cloudColorSun * (1.0 - alpha2) + cloudColorAmbient, vec3(0.0), vec3(1.0));
    
    // Start with the base sky background
    vec3 sky_color = additive + stars;
    
    // Mix the clouds over the sky
    sky_color = mix(sky_color, cloud_col, alpha1);
    
    // Add the sun and moon OVER the clouds (or additive through them)
    // In Firestorm, the huge sun halo bleeds over/through clouds, so additive blending here is correct.
    sky_color += sun_core + moon_core;
    
    // Smooth transition for the lower hemisphere instead of a hard cut,
    // so that water reflections (radiance map) capture a natural gradient and the sun!
    float ground_blend = smoothstep(0.0, -0.1, EYEDIR.y);
    vec3 ground_color = slng_haze_color.rgb * 0.85;
    sky_color = mix(sky_color, ground_color, ground_blend * 0.5); // Only dim it slightly for natural reflections
    
    COLOR = sky_color;
}
