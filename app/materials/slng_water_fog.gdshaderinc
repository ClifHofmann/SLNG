// BUG-RENDER-48: the reference viewer's water fog for things seen through the surface from ABOVE it.
//
// Ported from scratch/slviewer:
//   indra/newview/app_settings/shaders/class1/environment/waterFogF.glsl:40-74  getWaterFogViewNoClip
//   indra/newview/llsettingsvo.cpp:1073-1136                                    the uniforms it reads
// and kept in step with the C# mirror SLNG.Core.WaterFog (src/SLNG.Core/WaterFog.cs, tested against
// hand-worked numbers). Change both together.
//
// The result is a vec4: rgb = the water's in-scattered colour (already multiplied by L), a = the
// transmittance D. The viewer uses it two ways and so does SLNG:
//
//   * water.gdshader fogs the OPAQUE frame it refracts, with  behind * pow(D, 1.7) + rgb
//     (waterHazeF.glsl:70-72, blend (ONE, SRC_ALPHA), pipeline.cpp:9059). The haze pass runs before
//     LLDrawPoolWater copies the screen, so waterF.glsl's `fb` already holds fogged below-surface
//     objects (waterF.glsl:258-280, :327).
//   * the blended prim variants fog their OWN colour with  colour * D + rgb  (applyWaterFogViewLinearNoClip,
//     waterFogF.glsl:90-96 via applySkyAndWaterFog :115-144). The two paths differ by the 1.7 on D in the
//     viewer; this follows each.
//
// Camera ABOVE the surface only: with the eye under the water the viewer switches to a different
// density and path-length term, and SLNG has no underwater look yet. There slng_water_fog_height
// returns "no fog" so nothing here changes what an underwater camera sees.

// All four are written once per frame from C# (EnvironmentDriver.ApplyWater, TerrainRenderer,
// AvatarController.PublishViewSpaceWaterPlane).
global uniform vec3 slng_water_fog_color;       // the fog colour, LINEAR (the viewer does srgb_to_linear in-shader)
global uniform float slng_water_fog_density;    // waterFogDensity
global uniform float slng_water_fog_ks;         // waterFogKS = 1 / max(light elevation, 0.3)
// The primary region's water plane in VIEW space, the viewer's `waterPlane` uniform: xyz = world up
// rotated into view space, w = the eye's height above the plane. dot(p, xyz) + w is a view-space point's
// height above the water. Published per frame because a function called from fragment() cannot see
// VIEW_MATRIX / INV_VIEW_MATRIX (the same reason slng_sun_direction_view exists).
global uniform vec4 slng_water_plane_view;
// The water setting's fresnel (scale, offset), for the flat-surface transmission below.
global uniform vec2 slng_water_fresnel;

// eye_h / point_h: heights above the water plane of the eye and of the seen point; dist: the straight
// eye-to-point distance. Returns vec4(0, 0, 0, 1) when there is no fog to apply.
vec4 slng_water_fog_height(float eye_h, float point_h, float dist) {
    // getWaterFogView: nothing above the plane. The eye must be above it too (underwater: not modelled).
    if (!(eye_h > 0.0) || point_h > 0.0 || !(dist > 0.0)) {
        return vec4(0.0, 0.0, 0.0, 1.0);
    }

    float kd = slng_water_fog_density;
    float ks = slng_water_fog_ks;

    // es = -dot(view, waterPlane.xyz), written from heights: positive by construction here.
    float es = (eye_h - point_h) / dist;
    // int_v = view * waterPlane.w / es is where the ray crosses the surface; the water it then passes
    // through is the rest of the distance, at least 0.1 (getWaterFogViewNoClip: max(depth, 0.1)).
    float l = max(dist - eye_h / es, 0.1);

    float F = 0.98;
    float t1 = -kd;                        // -kd * pow(F, ks * e0) with e0 = 0 above the surface
    float t2 = kd + ks * es;
    // t2 can reach zero for a negative density, and 0 * inf is NaN -- which a GPU turns into black.
    if (abs(t2) < 1e-4) {
        t2 = t2 < 0.0 ? -1e-4 : 1e-4;
    }
    float t3 = pow(F, t2 * l) - 1.0;

    float scatter = clamp(t1 / t2 * t3, 0.0, 1.0);
    float L = pow(scatter, 1.0 / 1.7);
    float D = clamp(pow(F, l * kd), 0.0, 1.0);

    return vec4(slng_water_fog_color * L, D);
}

// The same, for a VIEW-space position (eye at the origin): what a blended face's fragment has to hand.
vec4 slng_water_fog_view(vec3 view_position) {
    float eye_h = slng_water_plane_view.w;
    float point_h = dot(view_position, slng_water_plane_view.xyz) + eye_h;
    return slng_water_fog_height(eye_h, point_h, length(view_position));
}

// True where a blended face's fragment should be fogged by itself: below the surface, with the eye above it.
// (An opaque / scissored face is fogged by water.gdshader through the frame it refracts, never here -- doing
// both would fog it twice.)
bool slng_water_fog_applies(vec3 view_position) {
    float eye_h = slng_water_plane_view.w;
    return eye_h > 0.0 && dot(view_position, slng_water_plane_view.xyz) + eye_h <= 0.0;
}

// SLNG.Core.WaterFog.FlatSurfaceTransmission / FlatSurfaceReflectionWeight: what the water's own fresnel does to
// whatever lies under the surface, with all three wave normals pointing up. waterF.glsl calculateFresnelFactors
// (:162-179) and :327 write the frame as
//     mix(fb, radiance * df2.y, df2.x) = fb * (1 - df2.x)  +  radiance * df2.y * df2.x
// -- the first term is the TRANSMISSION, the second the sheen of reflected sky. A blended face below the
// surface is inside `fb` in the viewer; here it is drawn after the water (BUG-RENDER-05), so it applies both to
// itself. Without them a blended leaf under the surface is exactly as bright as the same leaf above it -- "as
// if there were no water" -- while an opaque one beside it is dimmed and veiled (measured in the probe: 0.85
// above, 0.62 below at 30 degrees, plus the sheen on top).
float slng_water_fresnel_d(float view_dot_up) {
    return max(0.0, view_dot_up * slng_water_fresnel.x + slng_water_fresnel.y);
}

float slng_water_surface_transmission(vec3 view_position) {
    float d = slng_water_fresnel_d(dot(normalize(view_position), slng_water_plane_view.xyz));
    return 1.0 - min(1.0, 3.0 * d * d);
}

// The sky dome's colour along a direction -- sky.gdshader's `sky_col` (skyV.glsl:126-152) up to its final
// srgb_to_linear, WITHOUT clouds, sun disc or stars. A MIRROR of that block: change both together. It is
// what the water's METALLIC = 1 reflection shows for a clear sky, and the one thing a blended face cannot
// get from Godot's own radiance sample. eye_y = the direction's elevation (sine); dir_dot_light = its
// cosine to the light, for the haze glow. Both are frame-independent, so the caller can hand them over in
// view space (a function called from fragment() cannot see the world-space EYEDIR the sky itself reads).
vec3 slng_sky_dome_color(float eye_y, float dir_dot_light) {
    float view_y = max(0.001, eye_y);
    vec3 combined_haze = max(slng_blue_density.rgb + vec3(slng_haze_density), vec3(1e-6));
    vec3 blue_weight = slng_blue_density.rgb / combined_haze;
    vec3 haze_weight = vec3(slng_haze_density) / combined_haze;

    float density_dist = (slng_max_y / view_y) * slng_density_multiplier;
    vec3 sky_haze = exp(-combined_haze * density_dist);

    float haze_glow = max(1.0 - dir_dot_light, 0.001) * slng_glow.x;
    haze_glow = clamp(pow(haze_glow, slng_glow.z), 0.0, 1000.0);
    float haze_glow_sky = (slng_sun_moon_glow_factor < 1.0) ? 0.0 : slng_sun_moon_glow_factor * (haze_glow + 0.25);

    bool sun_is_up = slng_sun_direction.y >= 0.0;
    vec3 light_dir = normalize(sun_is_up ? slng_sun_direction : slng_moon_direction);
    vec3 light_color = sun_is_up ? slng_sunlight_color.rgb : slng_moonlight_color.rgb * 0.7;
    vec3 raw_ambient = slng_ambient.rgb;

    vec3 light_atten = (slng_blue_density.rgb + vec3(slng_haze_density * 0.25))
                     * (slng_density_multiplier * slng_max_y);
    float off_axis_view = 1.0 / max(1e-6, max(0.0, eye_y) + light_dir.y);
    vec3 sunlight = light_color * exp(-light_atten * off_axis_view);
    vec3 cs = sunlight * max(0.0, 1.0 - slng_cloud_shadow);

    vec3 sky_col = (slng_blue_horizon.rgb * blue_weight) * (sunlight + raw_ambient)
                 + (vec3(slng_haze_horizon) * haze_weight) * (sunlight * haze_glow_sky + raw_ambient);
    sky_col *= vec3(1.0) - sky_haze;

    vec3 sky_amb_below = raw_ambient + max(vec3(0.0), vec3(1.0) - raw_ambient) * slng_cloud_shadow * 0.5;
    vec3 sky_below = (slng_blue_horizon.rgb * blue_weight) * (cs + sky_amb_below)
                   + (vec3(slng_haze_horizon) * haze_weight) * (cs * haze_glow_sky + sky_amb_below);

    sky_haze = sqrt(sky_haze);
    sky_col += (sky_below - sky_col) * (vec3(1.0) - sqrt(sky_haze));

    return slng_srgb_to_linear(clamp(clamp(sky_col * 2.0, vec3(0.0), vec3(5.0)), vec3(0.0), vec3(1.0)));
}

// radiance * df2.y * df2.x for flat water: df2.y = dot(view, wavef) * scale + offset = d, and the radiance is
// the sky along the MIRRORED ray. (The viewer takes it from its reflection probe, sampleReflectionProbesWater;
// SLNG's water takes it from Godot's sky radiance, which this stands in for.) The mirrored ray's elevation is
// the negative of the view ray's and its angle to the sun follows from the same plane normal, so everything
// is expressed in view space with the plane normal and the view-space sun that are already published.
vec3 slng_water_surface_reflection(vec3 view_position) {
    vec3 v = normalize(view_position);
    vec3 up = slng_water_plane_view.xyz;
    float d = slng_water_fresnel_d(dot(v, up));
    float df2_x = min(1.0, 3.0 * d * d);
    vec3 mirrored = reflect(v, up);
    vec3 sky = slng_sky_dome_color(dot(mirrored, up), dot(mirrored, normalize(slng_sun_direction_view)));
    return sky * (df2_x * d);
}

// What a blended face below the surface shows: (colour * D + rgb) * T + reflection. Returns the fog as
// vec4(rgb = in-scatter * T, a = D * T) so the caller does  colour * a + rgb  exactly as for the plain fog,
// and hands the reflection back separately: it is the SURFACE's, so it is not scaled by the fog or by the
// face's own colour. Only call where slng_water_fog_applies() holds.
vec4 slng_water_blend_fog(vec3 view_position, out vec3 reflection) {
    vec4 fog = slng_water_fog_view(view_position);
    float t = slng_water_surface_transmission(view_position);
    reflection = slng_water_surface_reflection(view_position);
    return vec4(fog.rgb * t, fog.a * t);
}
