// SLNG legacy-specular probe -- a reference sphere for Firestorm/SLNG A/B of the
// Blinn-Phong specular path (a Specular MAP on a face), across the alpha modes.
//
// WHY. BUG-RENDER-41 moved the OPAQUE legacy-specular variant onto the viewer's sun highlight
// (RenderConfig / Material-Labor "Viewer-Sonnenglanz") and turned the old env-map veil off.
// The alpha-masked and alpha-blended variants were deliberately left on the old look, so they
// are the part that can differ from Firestorm. This sphere puts all three on the same shape
// under the same sun, one touch apart.
//
// A sphere shows the highlight from every angle at once, and a dark grey body keeps it from
// competing with the diffuse. The specular map is the plain white texture, so the highlight
// strength is decided only by the tint colour, glossiness and environment values below.
//
// USE
//   1. Rez a prim in the OPEN, a few metres clear of walls (no cast shadow, no colour bleed).
//   2. Drop this script in. The prim becomes a 2 m sphere.
//   3. Touch it to print the settings and step through the list.
//   4. Snapshot in both viewers at each step, FROM THE SAME CAMERA ANGLE: a highlight depends
//      on the half vector between sun and eye, so it moves when the camera moves.
//
// WHAT EACH STEP MEASURES
//   0-2  OPAQUE + specular map, glossiness 30 / 128 / 220, environment 0. The path BUG-RENDER-41
//        brought to parity: the reference for the rest. 30 is the terrace floor's value.
//   3    OPAQUE, glossiness 30, environment 5: the terrace floor itself (the mirror-look bug).
//   4-5  ALPHA MASK (cutoff 128) with glossiness 30 / 128: the masked variant.
//   6-7  ALPHA BLEND (face alpha 0.6) with glossiness 30 / 128: the blended variant.
//   8    OPAQUE, no specular map at all: the matte reference to flip against.
//
// If steps 4-7 match 0-1 in Firestorm but not in SLNG, the alpha variants need the viewer sun
// term too. If they match in both, nothing is left to do.

list GLOSS = [30, 128, 220, 30, 30, 128, 30, 128, 0];
list ENVIRONMENT = [0, 0, 0, 5, 0, 0, 0, 0, 0];
list ALPHA_MODE = [
    PRIM_ALPHA_MODE_NONE, PRIM_ALPHA_MODE_NONE, PRIM_ALPHA_MODE_NONE, PRIM_ALPHA_MODE_NONE,
    PRIM_ALPHA_MODE_MASK, PRIM_ALPHA_MODE_MASK,
    PRIM_ALPHA_MODE_BLEND, PRIM_ALPHA_MODE_BLEND,
    PRIM_ALPHA_MODE_NONE
];
list FACE_ALPHA = [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.6, 0.6, 1.0];
list NAMES = [
    "opaque gloss 30",
    "opaque gloss 128",
    "opaque gloss 220",
    "opaque gloss 30 env 5 (terrace floor)",
    "alpha MASK gloss 30",
    "alpha MASK gloss 128",
    "alpha BLEND gloss 30",
    "alpha BLEND gloss 128",
    "opaque, no specular map (matte reference)"
];

integer gStep = 0;

report()
{
    vector s = llGetSunDirection();
    llOwnerSay("step " + (string)gStep + "/" + (string)(llGetListLength(GLOSS) - 1)
        + "  " + llList2String(NAMES, gStep)
        + "  gloss=" + (string)llList2Integer(GLOSS, gStep)
        + "  env=" + (string)llList2Integer(ENVIRONMENT, gStep)
        + "  alphaMode=" + (string)llList2Integer(ALPHA_MODE, gStep)
        + "  faceAlpha=" + (string)llList2Float(FACE_ALPHA, gStep)
        + "  sun_dir=" + (string)s
        + "  elevation=" + (string)(llAsin(s.z) * RAD_TO_DEG) + " deg"
        + "  region=" + llGetRegionName()
        + "  pos=" + (string)llGetPos());
}

apply()
{
    integer gloss = llList2Integer(GLOSS, gStep);
    integer env = llList2Integer(ENVIRONMENT, gStep);
    integer mode = llList2Integer(ALPHA_MODE, gStep);
    float alpha = llList2Float(FACE_ALPHA, gStep);

    // The last step has no specular map: clearing it is what makes it the matte reference.
    key specMap = TEXTURE_BLANK;
    if (gloss == 0) specMap = NULL_KEY;

    llSetPrimitiveParams([
        PRIM_TYPE, PRIM_TYPE_SPHERE, PRIM_HOLE_DEFAULT,
            <0.0, 1.0, 0.0>, 0.0, ZERO_VECTOR, <0.0, 1.0, 0.0>,
        PRIM_SIZE, <2.0, 2.0, 2.0>,
        PRIM_TEXTURE,    ALL_SIDES, TEXTURE_BLANK, <1.0, 1.0, 0.0>, ZERO_VECTOR, 0.0,
        PRIM_COLOR,      ALL_SIDES, <0.2, 0.2, 0.2>, alpha,
        PRIM_FULLBRIGHT, ALL_SIDES, FALSE,
        PRIM_GLOW,       ALL_SIDES, 0.0,
        // The legacy Shiny level stays off: a specular MAP takes precedence over it in both
        // viewers, and leaving it on would make the step test the wrong path.
        PRIM_BUMP_SHINY, ALL_SIDES, PRIM_SHINY_NONE, PRIM_BUMP_NONE,
        PRIM_ALPHA_MODE, ALL_SIDES, mode, 128,
        PRIM_NORMAL,     ALL_SIDES, NULL_KEY, <1.0, 1.0, 0.0>, ZERO_VECTOR, 0.0,
        PRIM_SPECULAR,   ALL_SIDES, specMap, <1.0, 1.0, 0.0>, ZERO_VECTOR, 0.0,
            <1.0, 1.0, 1.0>, gloss, env
    ]);
}

default
{
    state_entry()
    {
        gStep = 0;
        apply();
        llOwnerSay("SLNG specular probe ready -- touch to step through "
            + (string)llGetListLength(GLOSS) + " settings.");
        report();
    }

    touch_start(integer n)
    {
        gStep = (gStep + 1) % llGetListLength(GLOSS);
        apply();
        report();
    }
}
