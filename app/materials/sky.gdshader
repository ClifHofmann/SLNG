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

// Where the camera sits inside the sky dome, as a fraction of its radius. Viewer default 0.96.
// getCamHeight = dome_offset * dome_radius (llenvironment.cpp:1045-1048); the radius cancels out
// of the cloud UV, so only this ratio is needed here.
global uniform float slng_dome_offset;

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

global uniform vec4 slng_moonlight_color;
global uniform vec4 slng_haze_color;

// Colour of the sun BILLBOARD, which is getSunDiffuse() and not the raw sunlight_color --
// llvosky.cpp:527. See the sun-disc block in sky() for why the two must not be swapped.
global uniform vec4 slng_sun_disc_color;

// Celestial
global uniform float slng_star_brightness;
global uniform vec3 slng_sun_direction;
global uniform vec3 slng_moon_direction;
global uniform float slng_sun_scale;
global uniform float slng_moon_scale;
global uniform float slng_moon_brightness;

// Clouds
global uniform vec4 slng_cloud_color;
global uniform vec4 slng_cloud_pos_density1;
global uniform vec4 slng_cloud_pos_density2;
global uniform float slng_cloud_scale;
global uniform float slng_cloud_variance;
uniform sampler2D slng_cloud_texture : hint_default_black, filter_linear_mipmap, repeat_enable;

// srgbF.glsl's srgb_to_linear, component-wise. Godot has no built-in for it in a sky shader, and
// the sky needs it because the value SL's atmospherics produce is display-referred -- see the
// COLOR assignment at the end of sky().
vec3 srgb_to_linear(vec3 c) {
    bvec3 low = lessThanEqual(c, vec3(0.04045));
    return mix(pow((c + vec3(0.055)) / vec3(1.055), vec3(2.4)), c / vec3(12.92), vec3(low));
}

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
    if (length(slng_moon_direction) > 0.001) {
        moon_dir_godot = normalize(slng_moon_direction);
    }
    
    // Light direction (using sun for day, moon for night if we had a full night cycle)
    vec3 light_dir = (sun_dir_godot.y >= 0.0) ? sun_dir_godot : moon_dir_godot;
    
    // --- SL Atmospherics Math -------------------------------------------------------------------
    // Ported from skyV.glsl and cloudsV.glsl. Those are BOTH different functions from the surface
    // atmospherics in atmosphericsFuncs.glsl, and where they disagree the sky dome follows the sky
    // shaders. Each disagreement below is called out, because several of them were previously
    // "fixed" the wrong way round.
    //
    // The inputs are the RAW sky settings. llsettingsvo.cpp:782-785 binds SG_SKY's sunlight_color
    // to psky->getSunlightColor(), and AMBIENT to getTotalAmbient() -- which just returns the
    // unmodified `ambient` (llsettingssky.cpp:1756). Each shader then applies its own
    // exp(-light_atten * off_axis). We were feeding slng_sunlight_color = SkyLighting.SunDiffuse,
    // which ALREADY carries exp(-light_atten * lighty) AND lightTransmittance, so the dome
    // attenuated it a second time. On PARITY-00 that is raw (0.734, 0.782, 0.900) against
    // SunDiffuse (0.489, 0.460, 0.439): blue-dominant turned red-dominant before the sky colour
    // was even built. Third time calculateLightSettings' output has been used where the viewer
    // uses a raw setting -- per ADR 0003 its results feed the DirectionalLight and nothing else.
    vec3 combined_haze = max(slng_blue_density.rgb + vec3(slng_haze_density), vec3(1e-6));
    vec3 blue_weight = slng_blue_density.rgb / combined_haze;
    vec3 haze_weight = vec3(slng_haze_density) / combined_haze;

    float density_dist = rel_pos_len * slng_density_multiplier;

    // NO distance_multiplier. skyV.glsl:58 declares the uniform and never reads it; cloudsV.glsl
    // does not even declare it. Only atmosphericsFuncs.glsl:84 -- the SURFACE path -- multiplies
    // by it. Ours did, making the dome and the clouds too transparent by exp(density_dist * 0.2).
    vec3 transmittance = exp(-combined_haze * density_dist);

    // Haze glow, skyV.glsl:141-155 and cloudsV.glsl:142-155: a plain `1 - dot`. The extra
    // `*= max(0, dot(light_dir, view))` we had belongs to atmosphericsFuncs.glsl:95 alone, and it
    // flattens the gradient -- squaring the dot dims the glow near the light and pins it to a
    // constant 1.0 across the entire anti-solar hemisphere.
    float haze_glow = 1.0 - dot(EYEDIR, light_dir);
    haze_glow = max(haze_glow, 0.001);
    haze_glow *= slng_glow.x;
    // glow.z is expected to be negative, making this a 1/angle falloff. The clamp is ours, not
    // the viewer's: pow() with a negative exponent on a near-zero base is unbounded.
    haze_glow = clamp(pow(haze_glow, slng_glow.z), 0.0, 1000.0);

    // sun_moon_glow_factor gates the halo by which body is up: 1.0 under the sun,
    // moon_brightness * 0.25 under the moon, 0 when neither is up (llsettingssky.cpp:1317-1321).
    //
    // The sky and cloud paths genuinely differ by one parenthesis, so both are ported rather than
    // sharing a value:
    //   sky    (skyV.glsl:155)    factor * (glow + 0.25)
    //   clouds (cloudsV.glsl:155) factor *  glow + 0.25
    // Both ZERO the glow outright at night -- SL-13768, "For sun, add to glow. For moon, remove
    // glow entirely." Only atmosphericsFuncs' SURFACE path keeps a scaled remnant; ours kept one
    // here too, which put a solar halo in the night sky.
    float haze_glow_sky = (slng_sun_moon_glow_factor < 1.0)
        ? 0.0
        : slng_sun_moon_glow_factor * (haze_glow + 0.25);
    float haze_glow_cloud = (slng_sun_moon_glow_factor < 1.0)
        ? 0.0
        : (haze_glow * slng_sun_moon_glow_factor + 0.25);

    // skyV.glsl:82 -- the moon reuses the sun's colour (getMoonlightColor returns
    // getSunlightColor, llsettingssky.cpp:1681-1684) with a "magic 0.7 to match legacy color".
    vec3 light_color = (sun_dir_godot.y >= 0.0)
        ? slng_sunlight_color.rgb
        : slng_moonlight_color.rgb * 0.7;

    // The raw `ambient` setting, used by all three atmospherics paths. There is NO night ambient
    // here: skyV uses the same value whichever body is up. Our scotopic slng_moon_ambient branch
    // was an invention, and MoonAmbient is one of calculateLightSettings' dead outputs (ADR 0003).
    vec3 raw_ambient = slng_ambient.rgb;

    vec3 light_atten = (slng_blue_density.rgb + vec3(slng_haze_density * 0.25))
                     * (slng_density_multiplier * slng_max_y);

    // skyV.glsl:112-113 and cloudsV.glsl:130-131 are identical: the sunlight is attenuated PER
    // VIEW DIRECTION, mixing the view elevation with the light's. That is what reddens the sky
    // toward the horizon on its own, and it is why the dome needs the unattenuated colour above.
    float off_axis_view = 1.0 / max(1e-6, max(0.0, EYEDIR.y) + light_dir.y);
    vec3 sunlight = light_color * exp(-light_atten * off_axis_view);

    // Sunlight dimmed by cloud cover, shared by skyV.glsl:147 and cloudsV.glsl:162.
    vec3 cs = sunlight * max(0.0, 1.0 - slng_cloud_shadow);

    // More cloud cover means more of the sky diffuses light, so ambient goes UP. cloudsV.glsl:158
    // does NOT clamp (1 - ambient) at zero where skyV.glsl:144 does; kept asymmetric on purpose,
    // so an ambient above 1.0 dims the cloud path very slightly and leaves the sky path alone.
    vec3 tmpAmbient = raw_ambient + (vec3(1.0) - raw_ambient) * slng_cloud_shadow * 0.5;

    // additiveColorBelowCloud (cloudsV.glsl:165-166): the haze that bleeds up into the underside
    // of a cloud. NOT multiplied by (1 - transmittance) here -- cloudsF applies that itself.
    vec3 additive_below_cloud = (slng_blue_horizon.rgb * blue_weight) * (cs + tmpAmbient)
                              + (vec3(slng_haze_horizon) * haze_weight) * (cs * haze_glow_cloud + tmpAmbient);

    // The cloud body's own sunlight, cloudsV.glsl:169-171: the raw colour again, attenuated with a
    // SECOND off_axis that ignores the view direction entirely. Not a typo in the viewer -- the
    // clouds are a flat layer, so their lighting does not vary with where you look.
    vec3 cloud_body_sunlight = light_color * exp(-light_atten / max(1e-6, light_dir.y * 2.0));

    // --- Sky dome colour, skyV.glsl:126-152 ----------------------------------------------------
    // Two haze terms blended by distance, and that blend IS the horizon gradient. Ours built one
    // term, which is why the sky read as a flat wash -- isolated by the PARITY-12/20 probes, where
    // removing the clouds left the bare sky visible against Firestorm's blue.
    vec3 sky_haze = transmittance;

    // Haze colour ABOVE the cloud layer: raw ambient, undimmed sunlight.
    vec3 sky_col = (slng_blue_horizon.rgb * blue_weight) * (sunlight + raw_ambient)
                 + (vec3(slng_haze_horizon) * haze_weight) * (sunlight * haze_glow_sky + raw_ambient);
    sky_col *= vec3(1.0) - sky_haze;

    // Haze colour BELOW it: ambient lifted by cloud cover, sunlight dimmed by it.
    vec3 sky_amb_below = raw_ambient + max(vec3(0.0), vec3(1.0) - raw_ambient) * slng_cloud_shadow * 0.5;
    vec3 sky_below = (slng_blue_horizon.rgb * blue_weight) * (cs + sky_amb_below)
                   + (vec3(slng_haze_horizon) * haze_weight) * (cs * haze_glow_sky + sky_amb_below);

    // The sqrt lands TWICE in the viewer -- once on combined_haze, then again inside the blend
    // weight -- so the effective weight is 1 - t^0.25. That steepness near the horizon is the
    // gradient.
    sky_haze = sqrt(sky_haze);
    sky_col += (sky_below - sky_col) * (vec3(1.0) - sqrt(sky_haze));

    // skyF.glsl:108-109 ends with `color.rgb *= 2.` and a clamp to 5.
    vec3 additive = clamp(sky_col * 2.0, vec3(0.0), vec3(5.0));
    // -----------------------------

    // --- Sun and moon discs, ported from LLVOSky::updateHeavenlyBodyGeometry (llvosky.cpp:1113-1140)
    // and LLDrawPoolWLSky::renderHeavenlyBodies (lldrawpoolwlsky.cpp:371-410).
    //
    // SIZE DOES NOT DEPEND ON GLOW. The billboard is a quad of half-width
    //     scale * HEAVENLY_BODY_DIST * HEAVENLY_BODY_FACTOR * getDiskRadius()
    // drawn at distance HEAVENLY_BODY_DIST, with HEAVENLY_BODY_FACTOR = 0.1 (llvosky.h:43) and
    // SUN_DISK_RADIUS = 0.5 / MOON_DISK_RADIUS = 0.45 (llvosky.cpp:72-73). The distance cancels,
    // leaving an angular half-size of 0.05 * sun_scale radians -- about 2.9 degrees, a 5.7 degree
    // disc. Ours derived a pow() falloff from `8000 / glow.x` instead, so this parcel's glow of
    // 11.94 blew the disc up to tens of degrees across.
    //
    // COLOUR IS getSunDiffuse(), not the raw sunlight_color: `mSun.setColor(psky->getSunDiffuse())`
    // (llvosky.cpp:527), read back through getInterpColor(). On this sunset that is
    // (0.48, 0.16, 0.03) against a raw (2.43, 2.44, 2.46) -- the difference between an orange-red
    // setting sun and a neutral white blob five times too bright. The disc is the ONE consumer of
    // calculateLightSettings the modern renderer kept; ADR 0003's claim that llvosky is "the
    // pre-EEP sky object" was wrong and is corrected there.
    //
    // The moon disc is pure white (llvosky.cpp:528), deliberately untinted.
    //
    // THERE IS NO SEPARATE HALO TERM. The halo is haze_glow in the sky dome above, which we
    // already compute. Ours added a second `pow(dot, falloff*0.05) * 0.25` on top, and at this
    // glow that term alone stayed above the display ceiling for ~20 degrees around the sun. Note
    // also lldrawpoolwlsky.cpp:380 -- "if and only if we have a texture defined, render the sun
    // disc" -- so with no texture the viewer draws no disc at all and only the haze glow remains.
    // We keep a procedural disc as a fallback while the texture loads; it is sized to the same
    // billboard, so it does not change the sky's shape when the texture arrives.
    //
    // The horizon enlargement is SL's own (llvosky.cpp:1131-1135): (1 - dir.z) reaches 1 at the
    // horizon, widening the disc by 30% and heightening it by 20%. That is the oversized sunset
    // sun everyone recognises, and it is why the disc is elliptical rather than round.
    // Sun disc: only drawn if sun is above the horizon (LLVOSky::updateHeavenlyBodyGeometry: draw_sun &= getIsSunUp())
    vec3 sun_core = vec3(0.0);
    if (sun_dir_godot.y >= -0.02) {
        float sun_dot = dot(EYEDIR, sun_dir_godot);
        float sun_enlarge = 1.0 - clamp(sun_dir_godot.y, 0.0, 1.0);
        float sun_half = 0.05 * clamp(slng_sun_scale, 0.1, 10.0);
        float sun_rh = sun_half * (1.0 + sun_enlarge * 0.3);
        float sun_rv = sun_half * (1.0 + sun_enlarge * 0.2);

        // Billboard tangent frame. At these angles the tangent dot products ARE the offsets in
        // radians, so the ellipse below is in the same units as the radii.
        vec3 sun_right = cross(vec3(0.0, 1.0, 0.0), sun_dir_godot);
        float sl = length(sun_right);
        sun_right = (sl < 0.001) ? vec3(1.0, 0.0, 0.0) : sun_right / sl;
        vec3 sun_up = cross(sun_dir_godot, sun_right);

        float sun_sx = dot(EYEDIR, sun_right);
        float sun_sy = dot(EYEDIR, sun_up);
        float sun_ellipse = length(vec2(sun_sx / sun_rh, sun_sy / sun_rv));

        float sun_horizon_clip = smoothstep(-0.01, 0.02, EYEDIR.y) * smoothstep(-0.01, 0.02, sun_dir_godot.y);

        // The billboard spans +/- one radius, so halving maps its extent onto 0..1.
        vec2 sun_uv = vec2(sun_sx / (2.0 * sun_rh), sun_sy / (2.0 * sun_rv)) + 0.5;
        vec4 sun_tex = texture(slng_sun_texture, sun_uv);
        float use_sun_tex = (sun_dot > 0.0 && sun_uv.x >= 0.0 && sun_uv.x <= 1.0
            && sun_uv.y >= 0.0 && sun_uv.y <= 1.0 && sun_tex.a > 0.01) ? 1.0 : 0.0;

        if (use_sun_tex > 0.5) {
            sun_core = slng_sun_disc_color.rgb * sun_tex.rgb * sun_tex.a * sun_horizon_clip;
        } else if (sun_dot > 0.0 && sun_ellipse < 1.0) {
            float sun_disk = 1.0 - smoothstep(0.75, 1.0, sun_ellipse);
            sun_core = slng_sun_disc_color.rgb * sun_disk * sun_horizon_clip;
        }
    }

    // Moon: same geometry with MOON_DISK_RADIUS = 0.45, hence 0.045, and an untinted white disc.
    // Only drawn if moon is above the horizon (draw_moon &= getIsMoonUp()).
    vec3 moon_core = vec3(0.0);
    if (moon_dir_godot.y >= -0.02) {
        float moon_dot = dot(EYEDIR, moon_dir_godot);
        float moon_enlarge = 1.0 - clamp(moon_dir_godot.y, 0.0, 1.0);
        float moon_half = 0.045 * clamp(slng_moon_scale, 0.1, 10.0);
        float moon_rh = moon_half * (1.0 + moon_enlarge * 0.3);
        float moon_rv = moon_half * (1.0 + moon_enlarge * 0.2);

        vec3 moon_right = cross(vec3(0.0, 1.0, 0.0), moon_dir_godot);
        float ml = length(moon_right);
        moon_right = (ml < 0.001) ? vec3(1.0, 0.0, 0.0) : moon_right / ml;
        vec3 moon_up = cross(moon_dir_godot, moon_right);

        float moon_sx = dot(EYEDIR, moon_right);
        float moon_sy = dot(EYEDIR, moon_up);
        float moon_ellipse = length(vec2(moon_sx / moon_rh, moon_sy / moon_rv));

        float moon_horizon_clip = smoothstep(-0.01, 0.02, EYEDIR.y) * smoothstep(-0.01, 0.02, moon_dir_godot.y);

        vec2 moon_uv = vec2(moon_sx / (2.0 * moon_rh), moon_sy / (2.0 * moon_rv)) + 0.5;
        vec4 moon_tex = texture(slng_moon_texture, moon_uv);
        float use_moon_tex = (moon_dot > 0.0 && moon_uv.x >= 0.0 && moon_uv.x <= 1.0
            && moon_uv.y >= 0.0 && moon_uv.y <= 1.0 && moon_tex.a > 0.01) ? 1.0 : 0.0;

        float brightness = clamp(slng_moon_brightness, 0.05, 1.0);
        if (use_moon_tex > 0.5) {
            // moonF.glsl:53 -- c.rgb *= moon_brightness
            moon_core = moon_tex.rgb * moon_tex.a * brightness * moon_horizon_clip;
        } else if (moon_dot > 0.0 && moon_ellipse < 1.0) {
            float moon_disk = 1.0 - smoothstep(0.75, 1.0, moon_ellipse);
            moon_core = vec3(moon_disk) * brightness * moon_horizon_clip;
        }
    }

    // Stars: rendered as discrete point stars across the upper celestial sphere
    vec3 stars = vec3(0.0);
    float star_elevation_fade = smoothstep(0.01, 0.15, EYEDIR.y);
    float star_sun_fade = clamp(-sun_dir_godot.y * 6.0 + 0.15, 0.0, 1.0);
    float star_visibility = star_elevation_fade * star_sun_fade * slng_star_brightness;

    if (star_visibility > 0.0001) {
        vec3 sp = normalize(EYEDIR) * 160.0;
        vec3 sid = floor(sp);
        vec3 sf = fract(sp) - 0.5;
        float sh = fract(sin(dot(sid, vec3(127.1, 311.7, 74.7))) * 43758.5453);
        if (sh > 0.965) {
            vec3 soff = vec3(
                fract(sin(dot(sid + vec3(1.0, 0.0, 0.0), vec3(127.1, 311.7, 74.7))) * 43758.5453),
                fract(sin(dot(sid + vec3(0.0, 1.0, 0.0), vec3(127.1, 311.7, 74.7))) * 43758.5453),
                fract(sin(dot(sid + vec3(0.0, 0.0, 1.0), vec3(127.1, 311.7, 74.7))) * 43758.5453)
            ) - 0.5;
            float sdist = length(sf - soff * 0.7);
            float star_pt = smoothstep(0.12, 0.01, sdist);
            float star_mag = (sh - 0.965) * 28.5; // 0..1
            vec3 star_tint = mix(vec3(0.85, 0.92, 1.0), vec3(1.0, 0.88, 0.7), fract(sh * 13.3));
            stars = star_tint * star_pt * (star_mag * 1.5 + 0.5) * star_visibility * 3.5;
        }
    }
    
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
    // Reading the formula straight off EYEDIR without step 2 SWAPS THE TWO UV AXES and rotates
    // the foreshortening by 90 degrees. Watch for that if this is ever touched again.
    //
    // The camera-height offset renderDome applies (`translatef(0, -camHeightLocal, 0)` before a
    // 0.333 scale) is genuinely negligible: dome_radius is 15000 in both regions we test against,
    // so an avatar at 22-100 m sits 0.0015-0.0067 of a dome radius off centre. That is what makes
    // substituting EYEDIR for the vertex direction legitimate here.
    //
    // Note the UV radius only ever reaches sin(22.5 deg) / 2 = 0.191 across the dome's real
    // extent, i.e. the middle ~38% of the texture. An earlier comment here said exactly that and
    // a previous commit of mine wrongly called it invented -- it is correct, and it follows
    // directly from calcPhi's PI/8 cap. See the extent gate further down.
    // 4. The camera sits INSIDE the dome, near its top. getCamHeight returns
    //    dome_offset * dome_radius and renderDome translates the dome down by exactly that
    //    (llenvironment.cpp:1045-1048, lldrawpoolwlsky.cpp:119) -- at the default 0.96 that is
    //    14400 of a 15000 radius, NOT the avatar's altitude. So a pixel's ray meets the dome far
    //    from where a naive "UV = f(view direction)" read would put it, and it is that mistake,
    //    not the axis order alone, that smeared clouds vertically in two earlier versions here.
    //
    //    Solve the intersection. The radius cancels, so only k = dome_offset matters: a ray
    //    leaving (0, k) at elevation e meets the unit sphere at
    //        t = -k*sin(e) + sqrt(1 - k^2 * cos^2(e))
    //    and the hit point's horizontal offset from the dome axis is t*cos(e). That offset IS
    //    sin(phi) of the dome vertex there, which is exactly what the UV is built from.
    float k = clamp(slng_dome_offset, 0.0, 0.999);
    float sin_e = clamp(EYEDIR.y, -1.0, 1.0);
    float cos_e = sqrt(max(0.0, 1.0 - sin_e * sin_e));
    float t_hit = -k * sin_e + sqrt(max(0.0, 1.0 - k * k * cos_e * cos_e));
    float sin_phi = t_hit * cos_e;

    // Horizontal bearing in SL world terms (+x east, +y north) through our (x, z, -y) convention.
    vec2 hdir = vec2(EYEDIR.x, -EYEDIR.z);
    float hlen = length(hdir);
    vec2 hunit = hlen > 1e-5 ? hdir / hlen : vec2(0.0, 0.0);

    vec2 dome_horiz = hunit * sin_phi;
    vec2 dome_uv = vec2((-dome_horiz.x + 1.0) * 0.5, (-dome_horiz.y + 1.0) * 0.5);

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
    
    // Cloud dome EXTENT. This is the piece that was missing, and it is not a fade-for-looks: SL's
    // cloud dome is a small CAP around the zenith, not a hemisphere, so beyond its rim there are
    // simply no clouds to draw.
    //
    // calcPhi (llvowlsky.cpp:100-118) returns (PI/8) * f(t) with f in 0..1, so the dome spans at
    // most 22.5 degrees from the apex. `NEW_TESS` is never defined, so the #else branch is live
    // and drops the outermost two rings by 2048 -- but against the dome_radius both our test
    // regions actually send (15000) that barely tilts them: the outer ring lands at
    // (0.331*15000, 0.944*15000 - 2048) = (4961, 12108), a direction of (0.379, 0.925), i.e. an
    // apparent elevation of 67.7 degrees. Clouds therefore live only in the top ~22 degrees of
    // sky. It also means the visible extent depends on dome_radius: at radius 1000 that same ring
    // would fall BELOW the horizon, which is presumably what the 2048 skirt was for.
    //
    // Fading from 0.2 instead, as this did, paints clouds down to ~11 degrees elevation. That is
    // where the dome UV's own compression is worst (its radius is cos(elevation)/2, so it barely
    // moves near the horizon), which is exactly the vertical smearing reported twice here. The
    // cure was never a different projection -- it was not drawing that region at all.
    //
    // The smoothstep is only edge anti-aliasing; SL's dome rim is hard geometry.
    //
    // Gated on sin(phi) of the dome hit, not on view elevation: beyond sin(22.5 deg) = 0.3827 the
    // ray leaves the cap and there is nothing to draw. Viewed from 96% of the way up the inside,
    // that rim lands at roughly -5.4 degrees elevation -- just BELOW the horizon, which is how a
    // narrow cap ends up covering the whole visible sky. An earlier version of this gate tested
    // EYEDIR.y > 0.90 instead, which is only correct for a camera at the dome's centre and hid
    // the clouds entirely.
    float altitude_blend_factor = 1.0 - smoothstep(0.3600, 0.3827, sin_phi);
    
    alpha1 *= altitude_blend_factor;
    alpha1 = clamp(alpha1, 0.0, 1.0);
    
    // Cloud colour, ported from cloudsF.glsl:168-182 and :221. TWO terms mixed by the self-shadow
    // above, not one flat blend:
    //   cloudColorSun     -- carries haze_glow, which is what turns cloud undersides orange near
    //                        a low sun. This is the term that was missing entirely, so our clouds
    //                        stayed neutral white through a sunset that the sky behind them was
    //                        already rendering warm.
    //   cloudColorAmbient -- the shadow side, plus haze bleeding up from below the layer.
    // Deliberately fed from cloud_body_sunlight rather than `cs`: SL dims sunlight by cloud_shadow
    // for the SKY, then re-reads the undimmed sunlight_color for the cloud term
    // (cloudsV.glsl:169-171) -- cloud cover must not shade the clouds casting it.
    // sqrt on the transmittance is SL's own: less atmospheric opacity below the cloud layer.
    vec3 cloud_atten = sqrt(clamp(transmittance, vec3(0.0), vec3(1.0)));
    vec3 cloudColorSun = (cloud_body_sunlight * haze_glow_cloud) * slng_cloud_color.rgb * cloud_atten;
    vec3 cloudColorAmbient = tmpAmbient * slng_cloud_color.rgb * cloud_atten
                           + additive_below_cloud * (vec3(1.0) - cloud_atten);
    // cloudsF.glsl:150-152, all three lines and in this order:
    //
    //     color = (cloudColorSun*(1.-alpha2) + cloudColorAmbient);
    //     color.rgb = clamp(color.rgb, vec3(0), vec3(1));
    //     color.rgb *= 2.0;
    //
    // The clamp is genuinely at 1.0 and is part of the algorithm -- cloudColorSun carries
    // haze_glow, which is permitted up to 1000 above, so without it the sunlit term overshoots and
    // the noise stops reading as shape. Raising the ceiling was tried and blew the upper sky to
    // flat white.
    //
    // But the DOUBLING comes after it, exactly as skyF.glsl:108 doubles the sky. Having ported the
    // sky's factor and not this one left the clouds at half brightness: modelled at the current sun
    // a mid cloud is (0.54, 0.55, 0.59) here and (1.00, 1.00, 1.00) in Firestorm -- storm grey
    // against white, which is what "die Wolken sind falsch beleuchtet" was. Both values are
    // display-referred sRGB, so the final clamp in the COLOR assignment absorbs the overshoot the
    // same way the viewer's own postDeferredGammaCorrect does.
    vec3 cloud_col = clamp(cloudColorSun * (1.0 - alpha2) + cloudColorAmbient, vec3(0.0), vec3(1.0));
    cloud_col *= 2.0;
    
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
    
    // The viewer's sky is DISPLAY-REFERRED, not scene-linear, and that mismatch -- not the haze
    // maths -- is what made our sky a pale wash.
    //
    // Follow one sky pixel through Firestorm. skyF.glsl:108-109 writes clamp(hazeColor * 2, 0, 5).
    // softenLightF.glsl:195-204 picks it up under GBUFFER_FLAG_SKIP_ATMOS, calls srgb_to_linear on
    // it and multiplies by sky_hdr_scale -- which is 1.0 for a legacy sky (llsettingsvo.cpp:855).
    // postDeferredGammaCorrect.glsl then does linear_to_srgb and clamps to [0,1]. The two
    // conversions cancel, so the pixel Firestorm actually shows IS clamp(hazeColor * 2, 0, 1),
    // read straight as sRGB. No tonemap touches it (see Boot.cs for why).
    //
    // Godot treats a sky shader's COLOR as linear radiance, so handing it the viewer's value
    // unconverted overstated every channel and then let ACES flatten what was left. Measured at
    // 25 degrees elevation on PARITY-00: Firestorm (0.48, 0.63, 1.00), ours (0.85, 0.90, 0.97) --
    // the washed-out grey-blue with no horizon gradient, reproduced numerically.
    //
    // Clamping before the conversion is the viewer's own final clamp. It also stops the sky from
    // tripping GlowHdrThreshold by itself, which srgb_to_linear(5.0) = 43.0 certainly would.
    COLOR = srgb_to_linear(clamp(sky_color, vec3(0.0), vec3(1.0)));
}
