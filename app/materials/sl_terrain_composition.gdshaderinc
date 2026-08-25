// Second Life terrain detail-texture composition, ported from the viewer.
//
// Sources (vendored at scratch/slviewer):
//   indra/newview/llvlcomposition.cpp  LLVLComposition::generateHeights  -- the composition value
//   indra/newview/noise.cpp / noise.h  noise2 / turbulence2              -- the Perlin field
//   indra/newview/app_settings/shaders/class1/deferred/terrainF.glsl     -- the four-way blend
//   indra/newview/lldrawpoolterrain.cpp                                  -- the detail UV texgen
//
// Two things about the viewer source are easy to get wrong and worth stating here:
//   * indra/llmath/llperlin.cpp is dead code with no callers. noise.cpp is the live one, and only
//     it seeds srand(42), which is what makes the terrain pattern stable and reproducible.
//   * generateHeights builds a 3-component vector whose third component is height/zScale, but
//     noise2() and turbulence2() read only the first two. Elevation never enters the noise --
//     the field is purely horizontal, and zScale is dead.
//
// The noise tables come from SLNG.Core.SlPerlinNoise via sl_noise_lut, so the CPU reference
// implementation and this shader cannot drift apart.

// 256x1 RGBAF lookup: rg = normalized 2D gradient, b = permutation entry (0..255).
uniform sampler2D sl_noise_lut : filter_nearest, repeat_disable;

// Region-dependent noise origins, one per octave. Each is the region's global SW corner already
// converted into that octave's lattice space and reduced modulo 256.
//
// The reduction is safe because noise2's lattice is 256-periodic, and it is necessary: SL global
// coordinates run to the hundreds of thousands, where float32 quantizes position to a few
// centimetres. The viewer eats that quantization because it evaluates the noise once per metre on
// the CPU; we evaluate per fragment, where it would show up as stair-stepping. The viewer applies
// the identical trick itself in LLSurfacePatch::eval (fmod(..., 256.f)).
uniform vec2 sl_noise_origin_low;
uniform vec2 sl_noise_origin_mid;
uniform vec2 sl_noise_origin_high;

// Origin for the much slower (~155 m) field that drives the blend ramp's V axis.
uniform vec2 sl_noise_origin_ramp;

// fmod(region global SW corner, sl_detail_scale_m), in metres -- the viewer's offset_x/offset_y.
uniform vec2 sl_detail_origin_m = vec2(0.0);

// The viewer's RenderTerrainScale (settings.xml default 12.0): metres per detail texture repeat.
uniform float sl_detail_scale_m = 12.0;

const float SL_XY_SCALE_INV = 1.0 / 4.9215;
const float SL_K_LOW  = SL_XY_SCALE_INV * 0.2222222222;
const float SL_K_MID  = SL_XY_SCALE_INV;
const float SL_K_HIGH = SL_XY_SCALE_INV * 2.0;
const float SL_K_RAMP = (1.0 / (4.9215 * 7.0)) * 0.2222222222;

int sl_perm(int i) {
    return int(texelFetch(sl_noise_lut, ivec2(i & 255, 0), 0).b + 0.5);
}

vec2 sl_grad(int i) {
    return texelFetch(sl_noise_lut, ivec2(i & 255, 0), 0).rg;
}

// noise.h fast_setup(). The +4096 bias is not an optimization detail to tidy away: it is what
// makes the lattice index fall out of a truncation to 8 bits, and b1 wraps because it is a U8.
void sl_fast_setup(float v, out int b0, out int b1, out float r0, out float r1) {
    float t = v + 4096.0;
    int ti = int(t);
    b0 = ti & 255;
    b1 = (b0 + 1) & 255;
    r0 = t - float(ti);
    r1 = r0 - 1.0;
}

// The cubic ease, not the quintic one modern Perlin implementations use.
float sl_s_curve(float t) {
    return t * t * (3.0 - 2.0 * t);
}

// Classic gradient Perlin on a 256 lattice -- noise2() in noise.cpp. Substituting simplex noise
// (e.g. the Ashima snoise in scratch/noise2D.glsl) gives a different pattern from identical
// constants, so this has to be the classic form.
float sl_noise2(vec2 v) {
    int bx0, bx1, by0, by1;
    float rx0, rx1, ry0, ry1;
    sl_fast_setup(v.x, bx0, bx1, rx0, rx1);
    sl_fast_setup(v.y, by0, by1, ry0, ry1);

    int i = sl_perm(bx0);
    int j = sl_perm(bx1);

    // The original indexes p[] past 256 into a mirrored copy of itself; masking is equivalent.
    int b00 = sl_perm(i + by0);
    int b10 = sl_perm(j + by0);
    int b01 = sl_perm(i + by1);
    int b11 = sl_perm(j + by1);

    float sx = sl_s_curve(rx0);
    float sy = sl_s_curve(ry0);

    float a = mix(dot(vec2(rx0, ry0), sl_grad(b00)), dot(vec2(rx1, ry0), sl_grad(b10)), sx);
    float b = mix(dot(vec2(rx0, ry1), sl_grad(b01)), dot(vec2(rx1, ry1), sl_grad(b11)), sx);
    return mix(a, b, sy);
}

// The noise added to elevation before the band lookup. This is the term that turns geometric
// contour bands into the mottling the real viewer shows, and it dominates everything else here:
// the low-frequency octave alone swings the effective height by roughly +/-9 m at a ~22 m scale.
//
// local_xy is the SL region-local position in metres (east, north).
float sl_twiddle(vec2 local_xy) {
    float t = sl_noise2(sl_noise_origin_low + local_xy * SL_K_LOW) * 6.5;

    // turbulence2(vec, 2) unrolled: freq 2 then freq 1, each contributing noise2(freq*v)/freq.
    float turb = sl_noise2(sl_noise_origin_high + local_xy * SL_K_HIGH) * 0.5
               + sl_noise2(sl_noise_origin_mid + local_xy * SL_K_MID);
    t += turb * 2.25;   // slope_squared = 1.5 * 1.5

    return t * 2.0;     // noise_magnitude
}

// Composition value in [0, 3]: which of the four detail textures belongs here, and how far
// between it and the next. All four slots are selected by elevation; the viewer has no
// slope-driven cliff texture.
float sl_composition_value(vec2 local_xy, float height, float start_height, float height_range) {
    // A region whose RegionHandshake has not arrived yet reports height_range 0. The viewer
    // simply does not draw terrain in that state -- generateHeights returns early on
    // !mParamsReady -- so there is no "right" value here, only a least-wrong one.
    //
    // It must be the LOWEST band. Clamping a divide-by-almost-zero lands on 3.0 instead, which
    // paints the entire region in the HIGHEST detail texture: with a normal terrain set that is
    // rock, unobtrusive enough to go unnoticed, and it did -- it only became visible once a probe
    // texture made band 3 bright magenta.
    if (abs(height_range) < 0.001) {
        return 0.0;
    }

    float scaled = (height + sl_twiddle(local_xy) - start_height) * 4.0 / height_range;
    return clamp(scaled, 0.0, 3.0);
}

// The viewer's own blend ramp, alpha_gradient_2d.j2c (IMG_ALPHA_GRAD_2D), shipped with us as
// textures/sl_alpha_gradient_2d.png under CC BY-SA 3.0 -- see THIRD-PARTY-NOTICES.md.
//
// NOT source_color: this is a lookup table, not a colour, and must not be sRGB-decoded on sample.
//
// Its import settings are load-bearing and Godot strips comments from .import files, so they are
// recorded here and asserted by TerrainRampAssetTests: compress/mode=0, mipmaps/generate=false,
// and above all detect_3d/compress_to=0 -- Godot's default for that last one silently re-imports
// the texture as VRAM-compressed the first time it is used in a 3D material, which would corrupt
// the ramp after it had already looked correct.
uniform sampler2D sl_alpha_ramp : filter_linear, repeat_disable;

// The blend ramp's V coordinate -- LLSurfacePatch::eval's rand_val. A slow field: ~155 m period,
// about one and a half cycles across a 256 m region.
float sl_ramp_v(vec2 local_xy) {
    float n = sl_noise2(sl_noise_origin_ramp + local_xy * SL_K_RAMP);
    return clamp(n * 0.75 + 0.5, 0.0, 1.0);
}

// The ramp is 2D on purpose, and the V axis is what makes the viewer's texture boundaries crisp.
// Holding it at one fixed curve gives every place at a given composition value the SAME blend; the
// viewer varies it with position, spreading the selected detail slot by up to 0.50 units
// mid-transition. That spread IS the contrast, and it is invisible to any measurement of the mean
// blend -- which is how it survived an earlier check here.
//
// Do not replace this lookup with a fitted curve. That was tried (v0.7.41-alpha) and reverted:
// fitting each row to a smoothstep is an unstable parameterization, so interpolating between rows
// produces ramps wider than any real row and the blend washes out further than holding the mean.
float sl_terrain_ramp(float u, float v) {
    // V IS FLIPPED, for the same reason sl_detail_uv flips it: SL's texture V is bottom-origin
    // and Godot's is top-origin. rand_val reaches the viewer as a plain texture coordinate
    // (llsurfacepatch.cpp:240, tex1->mV[1] = rand_val), so it is subject to that convention like
    // any other, and this file was sampling it in SL's.
    //
    // It matters here in a way it would not on a symmetric texture. The ramp's rows are NOT
    // interchangeable -- row 128 crosses over in 62 texels and row 160 takes 137 -- so remapping
    // v to 1-v does not change which rows get used, it changes WHERE each one is used. rand_val
    // is a ~155 m field, so the consequence is large-scale: the terrain is crisp where the viewer
    // is soft and soft where the viewer is crisp, at exactly the scale a Firestorm A/B of the
    // crossfade terrace shows the two disagreeing.
    //
    // CONFIRMED in-world against Firestorm on the known-answer crossfade terrace (v0.9.8-alpha).
    // This was the last open difference in FEAT-RENDER-02, and it is the same root cause as the
    // detail-UV flip in sl_detail_uv -- two texture coordinates, one convention, found five
    // measurement rounds apart because neither is visible to anything computed from position.
    return texture(sl_alpha_ramp, vec2(clamp(u, 0.0, 1.0), 1.0 - v)).r;
}

// srgb_to_linear from the viewer's class1/environment/srgbF.glsl -- the standard sRGB transfer,
// reproduced here because the four detail textures must be blended BEFORE it is applied.
vec3 sl_srgb_to_linear(vec3 cs) {
    vec3 low_range = cs / vec3(12.92);
    vec3 high_range = pow((cs + vec3(0.055)) / vec3(1.055), vec3(2.4));
    return mix(high_range, low_range, lessThanEqual(cs, vec3(0.04045)));
}

// terrainF.glsl's three-tap blend, verbatim in structure. Low composition selects detail 0 and
// high selects detail 3: the shipped ramp runs 255 -> 0, which is what sets that polarity.
//
// The samples come in sRGB-ENCODED and are blended that way, because that is what the viewer does.
// terrainF.glsl mixes raw texels and writes them straight to the albedo G-buffer; nothing in the
// legacy path calls srgb_to_linear, and `TCS_SRGB` is declared in llrender.h but used nowhere in
// the tree, so the detail textures are not bound as sRGB either. The PBR terrain path is the one
// that linearizes first (pbrterrainUtilF.glsl:158), and it is not the path Firestorm takes on
// OpenSim without PBR terrain.
//
// The distinction is not pedantic. Blending in linear space weights the brighter sample more, and
// SL's default terrain set has exactly one green slot (grass) against three brown/grey ones, so a
// linear blend of grass against mountain lands visibly browner than the viewer's sRGB blend:
// 50/50 gives (124,123,103) linear against (113,118,91) sRGB -- red overtakes green.
vec3 sl_terrain_blend(vec3 c0, vec3 c1, vec3 c2, vec3 c3, float value, float ramp_v) {
    float alpha1 = sl_terrain_ramp(value, ramp_v);
    float alpha2 = sl_terrain_ramp(value - 2.0, ramp_v);
    float alpha_final = sl_terrain_ramp(value - 1.0, ramp_v);
    vec3 blended = mix(mix(c3, c2, alpha2), mix(c1, c0, alpha1), alpha_final);
    return sl_srgb_to_linear(blended);
}

// Detail texture UV: global-continuous, at one repeat per sl_detail_scale_m metres.
vec2 sl_detail_uv(vec2 local_xy) {
    // s and t are the viewer's exactly: LLDrawPoolTerrain sets an OBJECT_LINEAR texgen plane
    // tp0 = (sDetailScale, 0, 0, offset_x), tp1 = (0, sDetailScale, 0, offset_y), giving
    // s = x/scale + offset_x and t = y/scale + offset_y with y running NORTH
    // (lldrawpoolterrain.cpp:242-248; note offset_* is already multiplied by sDetailScale
    // there, so it is in texture units, not metres, and our origin uniform matches).
    vec2 st = (local_xy + sl_detail_origin_m) / sl_detail_scale_m;

    // ...and then V is flipped, which the plane equations do NOT say and the rest of this
    // renderer already knows. SL's texture V is bottom-origin and Godot's is top-origin, so
    // every world mesh in ObjectRenderer is built with `v = 1 - v` (BuildArrayMesh's flipV,
    // true for every LLMesh face). Terrain never went through that path: it has no mesh UVs at
    // all, its coordinate is generated here from position, so it silently kept SL's convention
    // and sampled every detail texture upside down.
    //
    // Invisible on ordinary terrain art -- grass and rock are near enough symmetric that a
    // vertical mirror reads as "some other patch of grass". It took a probe texture with a
    // deliberate corner marker to expose it: the marker sits in the TOP-LEFT of the image
    // (tools/testassets/gen_terrain_probe.py), and it landed on the wrong side of the tile
    // against Firestorm while the terrain SHAPE matched exactly.
    //
    // `1.0 - t` rather than `-t` only for legibility; the sampler repeats, so they differ by a
    // whole tile and sample identically.
    return vec2(st.x, 1.0 - st.y);
}

// Bilinear blend of a per-corner region parameter. Corner order is the RegionHandshake order,
// which is also LLVLComposition::ECorner: 0 = SW, 1 = SE, 2 = NW, 3 = NE.
float sl_bilinear_corners(vec4 corners, float east, float north) {
    return mix(mix(corners[0], corners[1], north), mix(corners[2], corners[3], north), east);
}
