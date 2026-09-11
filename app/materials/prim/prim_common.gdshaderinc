// Shared body of SLNG's world-surface shader family (FEAT-RENDER-01, ADR 0002).
//
// Every prim face variant includes this. The variants differ ONLY in `render_mode` and in
// how they treat ALPHA, because render_mode is compile-time in Godot -- exactly the same
// reason StandardMaterial3D compiles its own variant set internally.
//
// This is a spatial shader, so it is NOT a PBR reimplementation: it assigns ALBEDO / ALPHA /
// NORMAL_MAP / METALLIC / ROUGHNESS / EMISSION and Godot's clustered lighting, shadows, GI
// and post-processing all still apply unchanged.

// --- Albedo -----------------------------------------------------------------------------
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_albedo_texture = false;

// --- PBR --------------------------------------------------------------------------------
uniform float metallic_factor : hint_range(0.0, 1.0) = 0.0;
uniform float roughness_factor : hint_range(0.0, 1.0) = 1.0;
uniform vec3 emission_color : source_color = vec3(0.0);
uniform bool emission_enabled = false;

// SL's per-face "fullbright" flag (LLTextureEntry::getFullbright, LLDrawPoolFullbright). The
// face ignores scene lighting and shows its texture at full colour. Reproduced at the end of
// slng_shade by routing the albedo into EMISSION and zeroing ALBEDO -- see there. Distinct from
// glow and from glTF emissive.
uniform bool fullbright = false;

uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_normal_texture = false;
uniform float normal_scale : hint_range(-16.0, 16.0) = 1.0;

uniform sampler2D orm_texture : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_orm_texture = false;

uniform sampler2D emission_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_emission_texture = false;

// --- UV transform -----------------------------------------------------------------------
// SL's per-face texture placement. Applied in the VERTEX stage: per-vertex is cheaper than
// per-fragment and the gap widens with resolution (ADR 0002).
//
// uv_scale/uv_offset reproduce SL's CENTRED convention, the same one ObjectRenderer builds
// today: u' = (u - 0.5) * repeat + 0.5 + offset. The caller folds the 0.5 term into
// uv_offset, so here it is a plain multiply-add and stays identical to the
// StandardMaterial3D behaviour it replaces.
//
// uv_rotation is SL's per-face texture rotation, in radians, passed through UNMODIFIED even
// though the meshes are built flipV. The viewer rotates in bottom-origin space with [c, s; -s, c]
// (llface.cpp:734-756); our centred V is the negation of the viewer's, and mapping back through
// that negation flips both sine signs into [c, -s; s, c] -- what this file applies below. The two
// inversions cancel, so the caller passes the raw angle.
//
// The order below is the viewer's: CENTRE, ROTATE, SCALE, OFFSET. Applying scale before rotation
// would shear the texture on any non-square repeat instead of rotating it.
//
// Rotating per-vertex loses nothing: a rotated UV is still an affine function of position, so
// interpolating it across the triangle gives exactly the per-fragment result.
uniform vec2 uv_scale = vec2(1.0);
uniform vec2 uv_offset = vec2(0.0);
uniform float uv_rotation = 0.0;

// A LEGACY Blinn-Phong material places its normal map INDEPENDENTLY of the diffuse texture --
// NormRepeat/NormOffset/NormRotation are their own fields on the material, not the face's
// (llmaterial.cpp:35-40). So the normal map needs its own transformed coordinate rather than
// reusing UV, and it is carried as a varying because the transform belongs in the vertex stage
// for the same reason the diffuse one does.
//
// Defaults are the identity AND has_normal_uv is false, so a face without a legacy material
// behaves exactly as before: the normal map, if any, keeps sharing the diffuse placement.
uniform vec2 normal_uv_scale = vec2(1.0);
uniform vec2 normal_uv_offset = vec2(0.0);
uniform float normal_uv_rotation = 0.0;
uniform bool has_normal_uv = false;

varying vec2 v_normal_uv;

// A legacy material's SPECULAR map, and the two scalars that go with it. The viewer's model
// (materialF.glsl:242-307):
//
//     spec       = texture(specularMap) * specular_color.rgb   // per-texel reflectivity/tint
//     glossiness = specular_color.a                            // SpecExp, ONE scalar per material
//     env        = env_intensity * spec.a                      // reflection strength
//
// Godot's metallic-roughness model has no per-texel specular COLOUR, so this is the one place in
// FEAT-RENDER-04 that is an approximation rather than a port, and it is deliberately a
// conservative one: the map's luminance drives how polished the surface is, and the material's
// glossiness sets how polished "fully lit" means. A greyscale spec map -- which is what almost
// all SL content ships -- is reproduced faithfully by that; a coloured one loses its tint.
uniform sampler2D specular_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_specular_texture = false;
uniform vec3 specular_tint = vec3(1.0);
uniform float specular_glossiness = 0.2;   // SpecExp/255; the viewer's default is 0.2*255
uniform float specular_environment = 0.0;  // EnvIntensity/255

// LEGACY per-face shininess -- the build tool's Shiny, as glossiness already mapped through the
// viewer's SHININESS_TO_ALPHA table: none 0, low 0.25, medium 0.5, high 0.75 (llface.cpp:1420).
// The viewer stores it in the vertex alpha and only "if we don't have a specular map", so a
// specular map takes precedence over this -- see the branch below. Zero means genuinely matte,
// and the viewer then skips its whole specular branch (`if (spec.a > 0.0)`, softenLightF.glsl:243).
uniform float legacy_shininess = 0.0;

uniform vec2 specular_uv_scale = vec2(1.0);
uniform vec2 specular_uv_offset = vec2(0.0);
uniform float specular_uv_rotation = 0.0;

varying vec2 v_specular_uv;

// --- TexGen -------------------------------------------------------------------------------
// SL's per-face texture GENERATION mode, which decides where the base UV comes from BEFORE the
// transform above runs. 0 = TEX_GEN_DEFAULT (the mesh's own UVs), 1 = TEX_GEN_PLANAR. The wire
// value for planar is 2, not 1 -- the C# side normalises it, see PrimShaderFamily.UvTexGen.
//
// prim_scale is the prim's SL-axis size in metres. Planar UVs are a function of vertex POSITION,
// and the viewer measures that position in metres, so the projection needs the size that
// ObjectRenderer applies as a Godot node scale rather than baking into the mesh. Passing it as a
// uniform (instead of reading MODEL_MATRIX) keeps meshes shareable between prims of the same
// shape and different sizes, which is the whole point of AssignSharedMesh, and avoids silently
// picking up an ancestor's scale from the node hierarchy.
uniform int uv_texgen = 0;
uniform vec3 prim_scale = vec3(1.0);

// SL's planar texture projection, ported from planarProjection (llface.cpp:88-120).
//
// The viewer picks one of four AXIS-ALIGNED binormals from the face normal's dominant axis,
// takes the tangent as their cross product, and projects the vertex position onto that pair:
//
//     binormal = |n.x| >= 0.5  ?  (0, sign(n.x), 0)  :  (n.y > 0 ? (-1,0,0) : (1,0,0))
//     tangent  = cross(binormal, normal)
//     s = 1.0 + (dot(binormal, p) * 2 - 0.5)
//     t =     -((dot(tangent,  p) * 2 - 0.5))
//
// The factor 2 is why a planar face tiles with prim size: p is in metres, so a 2m face spans 4
// units of texture. That size-independence is the entire reason builders use planar -- adjacent
// walls line up regardless of their individual dimensions.
//
// `center` is in planarProjection's signature but its body never reads it; it is not an omission
// here.
//
// Two coordinate conversions are folded in:
//  * AXES. ObjectRenderer.BuildArrayMesh stores SL (x,y,z) as Godot (x, z, -y), for positions and
//    normals alike, so SL space is recovered as (g.x, -g.z, g.y).
//  * V ORIGIN. planarProjection returns SL's bottom-origin t, while every mesh here is built
//    flipV so the rest of this file works in top-origin v = 1 - t. Converting here rather than
//    downstream is what lets slng_transform_uv stay identical for both texgen modes -- its
//    rotation-sign derivation only ever assumed v = 1 - t, never where the UV came from.
vec2 slng_planar_uv(vec3 vertex, vec3 normal) {
    vec3 p = vec3(vertex.x, -vertex.z, vertex.y) * prim_scale;
    vec3 n = vec3(normal.x, -normal.z, normal.y);

    vec3 binormal;
    if (n.x >= 0.5 || n.x <= -0.5) {
        binormal = vec3(0.0, n.x < 0.0 ? -1.0 : 1.0, 0.0);
    } else {
        binormal = vec3(n.y > 0.0 ? -1.0 : 1.0, 0.0, 0.0);
    }
    vec3 tangent = cross(binormal, n);

    float s = 1.0 + (dot(binormal, p) * 2.0 - 0.5);
    float t = -((dot(tangent, p) * 2.0 - 0.5));
    return vec2(s, 1.0 - t);
}

// --- Atmospherics seam ------------------------------------------------------------------
// Windlight / EEP goes here (Phase 5). Included from the shared include so prims, avatars,
// terrain and water all route through the same implementation.
#include "res://materials/slng_atmospherics.gdshaderinc"

// Takes and RETURNS the coordinate rather than using an `inout` parameter. The caller passes
// the UV built-in, and Godot does not treat built-ins as ordinary variables inside user
// functions -- assigning METALLIC/NORMAL_MAP from one is rejected outright ("Unknown identifier
// in expression"). Whether an `inout` argument writes back INTO a built-in at the call site is
// the same question, and it fails silently rather than erroring, which is the worst possible
// failure mode: the transform would simply never be applied. Returning the value sidesteps it.

// The placement transform, parameterised so the diffuse texture and a material's own maps can
// share one implementation. CENTRE, ROTATE, SCALE, OFFSET -- see slng_transform_uv.
vec2 slng_place_uv(vec2 uv, vec2 scale, vec2 offset, float rotation) {
    if (rotation != 0.0) {
        float s = sin(rotation);
        float c = cos(rotation);
        vec2 centred = uv - vec2(0.5);
        uv = vec2(centred.x * c - centred.y * s, centred.x * s + centred.y * c) + vec2(0.5);
    }
    return uv * scale + offset;
}

// Computes the coordinate a legacy material's NORMAL map is sampled at. Takes the same base
// coordinate the diffuse transform starts from (including texgen, which is a property of the
// FACE and applies to every map on it), then applies the material's own placement.
vec2 slng_normal_uv(vec2 uv, vec3 vertex, vec3 normal) {
    if (uv_texgen == 1) {
        uv = slng_planar_uv(vertex, normal);
    }
    return slng_place_uv(uv, normal_uv_scale, normal_uv_offset, normal_uv_rotation);
}

// Same as slng_normal_uv, for the specular map -- SL gives it its own repeat/offset/rotation
// too, and they are not required to match the normal map's.
vec2 slng_specular_uv(vec2 uv, vec3 vertex, vec3 normal) {
    if (uv_texgen == 1) {
        uv = slng_planar_uv(vertex, normal);
    }
    return slng_place_uv(uv, specular_uv_scale, specular_uv_offset, specular_uv_rotation);
}

vec2 slng_transform_uv(vec2 uv, vec3 vertex, vec3 normal) {
    // TexGen chooses the base coordinate; the transform below is identical either way. The
    // viewer has the same shape: getGeometryVolume computes planar tc and then calls the very
    // same xform() the default path uses (llface.cpp:1808-1812).
    if (uv_texgen == 1) {
        uv = slng_planar_uv(vertex, normal);
    }
    // No uv_extra_u/uv_extra_v here any more. They were a DIAGNOSTIC pair, added after the
    // finished transform so a residual sculpt offset could be dialled in from F10/F11 and read
    // off as a number instead of argued about. The offset they were built to measure is gone --
    // it was our sculpt grid, 33x33 against the viewer's 32x32, which put our UV step at 1/32
    // where the viewer's is 1/31 (fixed in `faadfa8`, now pinned index-by-index by
    // ViewerSculptParityTests). They wrote zero on every face after that, and every sculpt face
    // paid two SetShaderParameter calls for it.
    return slng_place_uv(uv, uv_scale, uv_offset, uv_rotation);
}

// Blinn-Phong glossiness -> Godot's PERCEPTUAL roughness, from the code that BUILDS SL's
// specular lookup texture (LLPipeline::createLUTBuffers, pipeline.cpp:1447):
//     n = glossiness * glossiness * RenderSpecularExponent    (default 368)
//     spec = pow(N dot H, n)
// then the standard Blinn-Phong -> GGX conversion alpha = sqrt(2/(n+2)). Godot's ROUGHNESS is
// perceptual, i.e. alpha = roughness^2, so the square root is taken twice.
//
// Getting this wrong is not subtle. A first attempt used roughness = 1 - glossiness, which for
// the reef rock's glossiness of 51/255 gives 0.8 and no visible highlight at all; the ported
// value is 0.59, and that is the difference between a matte rock and one whose ledges catch the
// sun.
//
// Shared by BOTH specular paths -- the legacy material's specular map and the build tool's
// plain Shiny level -- because in the viewer both index the same LUT and only the source of
// `glossiness` differs. Kept as one function so the two cannot drift apart.
float slng_glossiness_to_roughness(float glossiness) {
    float n = glossiness * glossiness * 368.0;
    float ggx_alpha = sqrt(2.0 / (n + 2.0));
    return clamp(sqrt(ggx_alpha), 0.0, 1.0);
}

// BUG-RENDER-20: the environment-reflection half of the viewer's specular model, for a face with
// legacy Shiny and no specular map (the build tool's Shiny low/medium/high -- probe steps 6-8).
// Ported from applyGlossEnv (reflectionProbeF.glsl:893, quoted in the spec) as its OWN additive
// EMISSION term, deliberately NOT folded into METALLIC/SPECULAR/ROUGHNESS the way the sun lobe
// above is.
//
// Why separate: the viewer reads the SAME `spec`/`glossiness` for its Blinn-Phong sun lobe and for
// applyGlossEnv, but applies a DIFFERENT overall scale to each (the env term gets its own
// `*0.5*fresnel*0.5`, on top of a Fresnel that is a completely different curve from the lobe's
// exponent). Godot's built-in metallic-roughness IBL has only one shared strength knob
// (SPECULAR/METALLIC feed both the direct-light response AND the automatic sky-radiance
// reflection identically -- confirmed empirically, not assumed: a ShaderMaterial and a
// StandardMaterial3D given the same METALLIC/ROUGHNESS/SPECULAR produced matching, non-zero,
// silhouette-strongest, shininess-scaling reflections in isolated probes, so Godot's automatic
// path is not broken). Raising that shared knob enough to make the reflection clearly visible
// would also brighten the sun highlight FEAT-RENDER-19 already calibrated against Firestorm past
// its confirmed-correct level -- so the reflection needs its own, independent term instead, exactly
// mirroring the viewer's own separation.
//
// `glossenv` (the reference reads an actual reflection-probe radiance map, a full deferred-
// renderer feature) is approximated here from the same Windlight globals sky.gdshader itself
// paints the sky from (slng_atmospherics.gdshaderinc, already in scope) -- a reflection and the
// sky it echoes stay in the same palette this way. SLNG places no reflection probes (this bug's
// own "untested variable" turned out NOT to be the custom ShaderMaterial; building a real probe
// capture of nearby geometry is FEAT-ENV work, not this fix), so this will not show actual
// courtyard walls the way a placed probe would -- only a Fresnel-weighted, shininess-scaled sky
// streak, which is what the acceptance criteria asks for.
//
// A flat single-colour glossenv (the first cut of this fix) was nearly invisible from a head-on
// framing: with only the Fresnel term varying it, the additive term is tiny anywhere near the
// centre of a sphere and only picks up near the silhouette -- but live in-world comparison
// against Firestorm (same probe, same camera position) showed its reflection is NOT just a rim
// brightening. It is visibly brighter across the UPPER half of the sphere, where the surface
// faces up toward the open sky, than lower down -- a real environment reflection varies with
// which part of the sky/scene a point's normal faces, not only with the VIEWING angle. Fresnel
// alone cannot produce that: it depends on the angle between the VIEW ray and the normal, which
// is symmetric regardless of which way the surface points in the world.
//
// Fixed (v2) by blending two already-available Windlight globals by how much the normal faces
// up: slng_blue_horizon (the horizon colour) for a normal at or below the horizon, toward
// slng_blue_density (SL's own zenith-leaning scattering term -- see EnvironmentDriver
// .ApplySkyDome's identical reasoning for reusing it as a zenith stand-in) for one facing
// straight up. Still an approximation of an actual radiance-map sample (see below), but now one
// that varies with surface orientation the way a real reflection has to.
//
// Getting "which way is up" here needs INV_VIEW_MATRIX, a fragment()-only built-in -- unreachable
// from a function fragment() calls, exactly the restriction slng_apply_atmospherics' own comment
// already documents for VIEW_MATRIX. Unlike slng_sun_direction_view (a per-FRAME global, because
// the sun moves), the camera's own basis changes every fragment of every frame, so the same
// per-frame-global trick does not apply here -- instead each variant's fragment() computes
// `(INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).y` (Godot world space: +Y is up) once, itself, and hands
// the single float in, the same way it already hands in NORMAL directly.
//
// v2 STILL produced no visible change live (v0.22.68-alpha, reported again: "ich seh aber noch
// keine reflexion"). This time verified with an actual rendered PICTURE rather than hand-derived
// arithmetic or single-pixel samples -- `scratch/probes/*.gd`'s pattern, but with
// `Image.save_png()` on the viewport texture so the frame could be looked at (see BUG-RENDER-20
// spec for the saved files). The v2 code, screenshotted unmodified: a dark grey sphere at shiny
// HIGH showed the FEAT-RENDER-19 sun highlight and, on close comparison against a matte control,
// a barely-there lightening of the upper rim -- nothing a person would describe as "a reflection"
// without already knowing where to look. Two compounding problems, both numeric:
//
// 1. MAGNITUDE. `glossenv` in the reference is an actual captured HDR radiance sample, explicitly
//    clamped to [0, 10] (sampleReflectionProbes, reflectionProbeF.glsl:890) -- i.e. it can be an
//    order of magnitude brighter than 1.0. SLNG's stand-in is `slng_blue_horizon`/
//    `slng_blue_density`, LDR Windlight haze colours that top out around 0.5-0.8 pre-linearize
//    and land under 0.55 in linear after `slng_srgb_to_linear`. Applying the viewer's literal
//    `*0.5 ... *0.5` attenuation chain -- tuned by LL against that HDR input -- to an input roughly
//    20x dimmer produces a term on the order of 0.01-0.05, which is exactly what the probe
//    screenshot showed: technically present, invisible in practice.
// 2. GEOMETRY. Simply removing the two `*0.5` fudges and boosting the raw colour (tried first,
//    boost x6, both `*0.5`s dropped) does not reproduce Firestorm's look either -- rendered, it is
//    a bright RING around the ENTIRE silhouette (a "glass bubble" look), because Fresnel
//    (`clamp(1+dot(view,normal), 0.3, 1)`) is symmetric all the way around a sphere's limb: the
//    BOTTOM of the silhouette gets exactly as much of the boosted term as the top, which is not
//    what a real environment reflection does (the bottom of a sphere reflects the ground/floor,
//    not the bright sky) and not what Firestorm's screenshot shows (bright on top, dark below,
//    not an even halo).
//
// Fixed by gating the term's INTENSITY on `upness` as well as its hue -- not just picking the
// colour between horizon and zenith, but scaling the whole reflection down toward zero as the
// surface stops facing up. This is the piece that actually produces "a patch on the upper
// hemisphere, fading below" instead of "a ring around the silhouette": Fresnel alone cannot tell
// the top of a sphere from the bottom (it only knows the VIEW angle), so intensity has to carry
// the part of the shape that varies with which way the surface points in the WORLD, exactly the
// same insight v2's colour-blend already had, now applied to brightness and not only hue.
// Verified visually (not just numerically) against a saved probe screenshot at each step:
// Fresnel-gated-only produced the ring; adding the `upness` intensity gate produced a patch
// confined to the upper hemisphere, tracking shininess LOW < MEDIUM < HIGH exactly as before,
// with the equator-line cutoff itself a real, expected consequence of how steeply a sphere's
// surface curves away right at that latitude when viewed head-on -- not an artefact of the gate.
vec3 slng_env_reflection(vec3 view_position, vec3 normal, float world_normal_up,
                          float shininess_level, vec3 base_color) {
    // fresnel: reflectionProbeF.glsl:894-896, unchanged. `pos`/`norm` there are the fragment's
    // VIEW-SPACE position and normal -- exactly view_position and NORMAL here (both already in
    // view space in every prim variant's fragment()).
    float fresnel = clamp(1.0 + dot(normalize(view_position), normal), 0.3, 1.0);
    fresnel *= fresnel;
    fresnel *= shininess_level;   // spec.a: the shininess LEVEL scales the whole term

    // 0 at or below the horizon, rising to 1 facing straight up. Not the full hemisphere: SLNG
    // carries no distinct "ground" colour as a shader global (only the sky dome material gets
    // one, and only on the CPU side), so a downward-facing normal picks the horizon tint rather
    // than fading toward black -- a reflection that is merely un-varying below the horizon, not an
    // inaccurate one, since nothing here claims to show the actual ground. `upness` now ALSO
    // scales the term's intensity (`patch` below), not only its hue -- see the v3 note above for
    // why that is the change that actually made a visible difference.
    float upness = clamp(world_normal_up, 0.0, 1.0);
    vec3 sky_color = mix(slng_blue_horizon.rgb, slng_blue_density.rgb, upness);
    vec3 glossenv = slng_srgb_to_linear(clamp(sky_color, vec3(0.0), vec3(1.0)));

    // ENV_REFLECTION_BOOST (8.0): compensates for `sky_color` being an LDR haze-colour stand-in
    // (tops out well under 1.0 after linearizing) where the viewer's own `glossenv` is a genuine
    // HDR radiance sample clamped only at 10.0 -- see the v3 note above. Tuned empirically against
    // saved probe screenshots (not derived): 8.0 is the smallest value at shiny HIGH that reads
    // unambiguously as a bright reflected patch rather than a faint tint, without blowing the
    // upper rim's blue channel out past a small, physically-plausible sliver near the true
    // grazing+straight-up point (where `fresnel` and `patch` both peak at once -- a single point
    // on a sphere, in practice a handful of pixels, not a visible blob in any of the probe
    // screenshots at shiny low/medium/high).
    //
    // `patch`: the intensity gate itself. Deliberately linear in `upness`, not `pow(upness, n)` --
    // a steeper falloff (tried at n=1.5) pulled the visible patch in to a thin crescent right at
    // the top rim; linear let the reflection extend down across roughly the upper half of the
    // visible sphere, which is what the Firestorm reference screenshot shows and what a plain
    // `upness` already produces once foreshortening (a sphere's own geometry) is accounted for.
    float patch = upness;
    glossenv *= 8.0 * fresnel * patch;
    glossenv *= (vec3(1.0) - base_color);   // applyGlossEnv's "fake energy conservation"
    return glossenv;
}

// Samples everything a face needs and hands it back through out-params. It does NOT assign
// the built-ins itself: METALLIC / ROUGHNESS / NORMAL_MAP / EMISSION are fragment-stage
// built-ins and Godot rejects writing them from a user function ("Unknown identifier in
// expression: 'METALLIC'"). Each variant's fragment() does the assigning; this keeps the
// sampling logic in one place, which is the part that must not drift between variants.
//
// Returns the albedo alpha, the one thing the variants genuinely disagree about.
float slng_shade(vec2 uv, vec3 view_position, vec3 normal, float world_normal_up,
                 out vec3 out_albedo,
                 out float out_metallic, out float out_roughness, out float out_specular,
                 out vec3 out_normal_map, out float out_normal_depth,
                 out vec3 out_emission) {
    vec4 albedo = albedo_color;
    if (has_albedo_texture) {
        albedo *= texture(albedo_texture, uv);
    }

    vec3 atmos_additive;
    out_albedo = slng_apply_atmospherics(albedo.rgb, view_position, atmos_additive);

    out_metallic = metallic_factor;
    out_roughness = roughness_factor;

    // 0, not Godot's default 0.5. The viewer gates its ENTIRE specular and environment branch on
    // `if (spec.a > 0.0)` (softenLightF.glsl:243), and spec.a is the face's legacy shininess --
    // zero for PRIM_SHINY_NONE, which is most SL content. SLNG does not read legacy shininess at
    // all (there is no uniform for it), so a flat 0.5 here was a 4% dielectric highlight driven by
    // no SL data whatsoever, on every face without a specular map.
    //
    // Found with the probe sphere: greys matched Firestorm exactly while the pure primaries came
    // out visibly duller. That is the signature -- 4% of neutral reflection is invisible on a
    // white or grey surface and washes out a fully saturated one, and a blue sphere went violet
    // because what it was reflecting was the warm courtyard. A specular map still overrides this
    // below, which is the only path SL actually feeds.
    out_specular = 0.0;

    // BUG-RENDER-20: an additive, independent environment-reflection term for the legacy-shininess
    // branch below -- see slng_env_reflection's own comment for why it is not folded into
    // out_metallic/out_specular/out_roughness the way the sun highlight is. Zero unless that
    // branch actually runs, so PRIM_SHINY_NONE (probe steps 0-5) is untouched.
    vec3 env_reflection = vec3(0.0);

    if (has_specular_texture) {
        vec3 spec = texture(specular_texture, v_specular_uv).rgb * specular_tint;

        // SL modulates glossiness PER TEXEL with the normal map's ALPHA channel
        // (materialF.glsl:227, `glossiness *= vNt.a`). Not an optional refinement: content is
        // authored expecting it, and without it a material that varies its polish across the
        // surface comes out uniformly shiny or uniformly matte.
        float glossiness = specular_glossiness;
        if (has_normal_texture) {
            glossiness *= texture(normal_texture, has_normal_uv ? v_normal_uv : uv).a;
        }

        out_roughness = slng_glossiness_to_roughness(glossiness);

        // The map's RGB scales the highlight's STRENGTH, matching the viewer's
        // `color += lit * scol * sunlit * spec.rgb`. Godot has no per-texel specular colour, so
        // this is the one lossy step: a greyscale map (what nearly all SL content ships) is
        // reproduced faithfully, a coloured one loses its tint. Rec. 709 luminance.
        out_specular = clamp(dot(spec, vec3(0.2126, 0.7152, 0.0722)), 0.0, 1.0);

        // EnvIntensity is SL's "how mirror-like is this". Godot reaches the environment through
        // metalness, so it folds in there, scaled by the same luminance so it only applies where
        // the map says the surface is shiny at all.
        out_metallic = clamp(metallic_factor + specular_environment * out_specular, 0.0, 1.0);
    }
    else if (legacy_shininess > 0.0) {
        // The build tool's Shiny, for a face with no specular map. `else`, not a second `if`:
        // the viewer packs shininess into the vertex alpha only when there is no specular map
        // (llface.cpp:1412), so a map always wins and the two never combine.
        //
        // 0.5 is Godot's standard 4% dielectric F0. The viewer's legacy highlight is a
        // Blinn-Phong lobe scaled by a white specular colour and is not energy-conserving, so
        // there is no exact equivalent; what SL genuinely varies between low/medium/high is the
        // TIGHTNESS of the lobe, and that is what the glossiness above carries. Taking the
        // conservative physical value for the strength keeps a polished face polished without
        // inventing brightness the protocol never sent.
        out_specular = 0.5;
        out_roughness = slng_glossiness_to_roughness(legacy_shininess);
        env_reflection = slng_env_reflection(view_position, normal, world_normal_up, legacy_shininess, out_albedo);
    }

    // glTF ORM packing: occlusion in R, roughness in G, metallic in B.
    if (has_orm_texture) {
        vec4 orm = texture(orm_texture, uv);
        out_roughness = roughness_factor * orm.g;
        out_metallic = metallic_factor * orm.b;
    }

    // A flat normal (0.5, 0.5, 1.0) with zero depth is a no-op, so the variants can assign
    // unconditionally instead of needing a second "has normal" flag threaded through.
    out_normal_map = vec3(0.5, 0.5, 1.0);
    out_normal_depth = 0.0;
    if (has_normal_texture) {
        out_normal_map = texture(normal_texture, has_normal_uv ? v_normal_uv : uv).rgb;
        out_normal_depth = normal_scale;
    }

    out_emission = atmos_additive;
    if (emission_enabled) {
        // MULTIPLIES factor by texture, which is what glTF specifies for
        // emissiveFactor * emissiveTexture -- and what SLNG's only source of emission data
        // (AssetService's glTF material resolve) means.
        vec3 emission = emission_color;
        if (has_emission_texture) {
            emission *= texture(emission_texture, uv).rgb;
        }
        out_emission = emission + atmos_additive;
    }

    // Fullbright ignores scene lighting entirely (see below), and the viewer never runs its
    // specular/env branch for a fullbright face either -- so this is skipped rather than added
    // and then cancelled out by the fullbright block's own albedo routing.
    if (!fullbright) {
        out_emission += env_reflection;
    }

    // SL fullbright: the face is lit by nothing. Route the (already atmospherics-applied) albedo
    // through EMISSION and zero ALBEDO so clustered lighting, ambient and GI add nothing on top;
    // atmospherics stays applied, matching the viewer's fullbright shaders. Added to any glTF
    // emissive rather than replacing it, since a face can be both.
    //
    // NOT a no-op for the `unshaded` HUD variants, which is what an earlier version of this
    // comment claimed. Godot's `unshaded` render_mode outputs ALBEDO *only* -- EMISSION is
    // dropped -- so zeroing albedo here rendered every fullbright HUD face pure black, and HUD
    // faces are almost always fullbright (measured in-world: all six faces of a worn HUD, each
    // correctly textured 512-1024px, drawn as solid black rectangles). The HUD variants therefore
    // fold emission back into ALBEDO themselves; see prim_opaque_hud.gdshader.
    if (fullbright) {
        out_emission += out_albedo;
        out_albedo = vec3(0.0);
    }

    return albedo.a;
}
