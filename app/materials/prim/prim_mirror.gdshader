// BUG-RENDER-32: a real planar mirror.
//
// Everything before this tried to answer a mirror with a CUBEMAP -- the camera-following probe,
// then a parallax box around it, then a real-time probe parked on the mirror itself, then a
// parallax box around THAT. Each got closer and none could be right, for two reasons that no
// setting reaches:
//
//   SCALE  an infinite cubemap shows an object at the angular size it had FROM THE PROBE, so the
//          reflection keeps its size on screen while the mirror shrinks as you back away. The
//          user measured exactly that: "je weiter ich vom Spiegel weg zoome, umso groesser werden
//          die gespiegelten Bilder im Vergleich zum Spiegel". A real mirror's ratio approaches a
//          limit; a cubemap's grows without bound.
//   DETAIL a 512-px cube face spans 90 degrees -- 5.7 pixels per degree against the screen's 32.
//          Raising the atlas to 1024 halves the gap and cannot close it.
//
// A planar mirror has neither problem, because it is not an approximation: a second camera is
// placed at the viewer's own position reflected through the mirror plane, renders the scene at
// SCREEN resolution, and this shader samples that render at the fragment's own screen position.
// Same projection, same pixels, exact perspective. It is also CHEAPER than what it replaces --
// one scene render per frame instead of the hero probe's six cube faces.
//
// Uniform names are deliberately the ones prim_common.gdshaderinc already uses. A ShaderMaterial
// keeps its parameters by name across a shader swap, so ObjectRenderer can turn an ordinary prim
// face into a mirror by assigning this shader and setting ONE new uniform -- the albedo, its
// texture and the Shiny level come across on their own.

shader_type spatial;
render_mode cull_back, depth_draw_opaque, shadows_disabled;

// The reflected render. Set by Boot from the mirror SubViewport's texture; black until then,
// which shows as an unlit dark panel rather than as garbage.
uniform sampler2D mirror_texture : hint_default_black, filter_linear;

// The virtual camera is built with LookAt, which forces a right-handed basis and so loses the
// reflection's handedness flip. Restoring it is one coordinate, not a second render.
uniform bool mirror_flip_u = true;

// --- carried over from prim_common.gdshaderinc by name ---------------------------------------
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_albedo_texture = false;

// SL's Shiny level as SHININESS_TO_ALPHA (none 0, low 0.25, medium 0.5, high 0.75). For a face
// with no specular map this one value IS the environment intensity (llvovolume.cpp:5543,
// BUG-RENDER-23), and applyLegacyEnv weighs the reflection by it.
uniform float legacy_shininess = 0.75;

void fragment() {
    vec4 base = albedo_color;
    if (has_albedo_texture) {
        base *= texture(albedo_texture, UV);
    }

    vec2 uv = SCREEN_UV;
    if (mirror_flip_u) {
        uv.x = 1.0 - uv.x;
    }
    vec3 reflected = texture(mirror_texture, uv).rgb;

    // The viewer's own mix, unchanged from BUG-RENDER-25:
    //     color = mix(color.rgb, reflected_color * 0.5, envIntensity)   applyLegacyEnv:912
    // so Shiny HIGH replaces three quarters of the surface and Shiny LOW a quarter, and a mirror
    // at Shiny NONE is just its own grey -- which is the ladder mirror_panel.lsl steps through.
    //
    // The viewer also scales by a Fresnel term (`min(fresnel + envIntensity, 1.0)`) that brightens
    // the reflection at grazing angles. Left out deliberately for the first version: it is a
    // refinement on top of a reflection that is now geometrically correct, and adding it in the
    // same change would make a wrong result impossible to attribute.
    float env = clamp(legacy_shininess, 0.0, 1.0);
    vec3 lit = mix(base.rgb, reflected * 0.5, env);

    // Fullbright, because that is what SL's fake mirror is: the recipe this shader serves is
    // FULLBRIGHT + Shiny HIGH (BUG-RENDER-27), and the viewer draws it with no lighting at all.
    // ALBEDO zeroed so clustered lights, ambient and GI add nothing on top.
    ALBEDO = vec3(0.0);
    EMISSION = lit;
    ALPHA = base.a;
    SPECULAR = 0.0;
    METALLIC = 0.0;
    ROUGHNESS = 1.0;
}
