// SLNG lighting probe -- a reference sphere for Firestorm/SLNG A/B.
//
// WHY A SPHERE. A flat face gives one NdotL sample; a sphere gives the whole falloff from
// NdotL 1 at the sun-facing pole down to 0 at the terminator, in a single screenshot. That
// curve is the thing under test: SLNG hits the viewer's two ENDPOINTS exactly (verified in
// the log to three decimals) but sums its lights linearly, where the viewer sums sun and
// ambient in sRGB and runs pow(NdotL, 1.2) first. Endpoints cannot distinguish those; a
// falloff can. A sphere is also the only shape that shows a specular highlight from every
// angle at once, which is what the last three steps are for.
//
// Everything that would add a second, unknown term to a pixel is switched off: no texture,
// no fullbright, no glow, no normal or specular material. What is left is the light that
// reached the sphere, plus -- in the shiny steps only -- the highlight SL itself asked for.
//
// USE
//   1. Rez a prim in the OPEN -- nothing overhead, a few metres clear of walls, so no cast
//      shadow and no colour bleed from a neighbour lands on it.
//   2. Drop this script in. The prim becomes a 2 m sphere.
//   3. Touch it to print the sun direction and step through the list below.
//   4. Snapshot in both viewers at each step.
//
// SHOOT THE SHINY STEPS FROM THE SAME CAMERA ANGLE. Diffuse shading depends only on the sun,
// so a rough angle match is fine for steps 0-5; a highlight depends on the HALF vector between
// sun and eye, so it moves when the camera moves. Two screenshots from different positions
// will disagree about where the highlight sits even when both viewers are correct.
//
// WHAT EACH GROUP MEASURES
//   Greys (0-2)      the light's LEVEL -- white reads the sun end, the dark step the shadow
//                    end. A difference at EVERY albedo means a scale is wrong, not a curve.
//   Primaries (3-5)  the same thing PER CHANNEL. Not redundant: a grey sphere weights the
//                    channels by luminance, so blue contributes only 7% and an error there
//                    hides almost entirely. A pure blue sphere has zero albedo in red and
//                    green, so that one channel stands alone at full strength. The bug this
//                    probe was built for was exactly a per-channel ratio error (R/B off by a
//                    2.4 power), which grey alone would not have caught. They also catch
//                    anything NEUTRAL being added wrongly -- 4% of white reflection is
//                    invisible on grey and visibly washes out a saturated colour.
//   Shiny (6-8)      FEAT-RENDER-19's legacy shininess. Dark grey on purpose: a highlight
//                    reads best against a body that is not competing with it. Going none ->
//                    low -> medium -> high the highlight should APPEAR and then TIGHTEN; SL
//                    varies the lobe's sharpness with the level, not its brightness. Step 2
//                    is the same dark grey with no shininess, so it doubles as the matte
//                    reference for this group -- flip between 2 and 6 to see the highlight
//                    switch on.

list ALBEDOS = [
    <1.0, 1.0, 1.0>,
    <0.5, 0.5, 0.5>,
    <0.2, 0.2, 0.2>,
    <1.0, 0.0, 0.0>,
    <0.0, 1.0, 0.0>,
    <0.0, 0.0, 1.0>,
    <0.2, 0.2, 0.2>,
    <0.2, 0.2, 0.2>,
    <0.2, 0.2, 0.2>
];
list SHINY = [
    PRIM_SHINY_NONE,
    PRIM_SHINY_NONE,
    PRIM_SHINY_NONE,
    PRIM_SHINY_NONE,
    PRIM_SHINY_NONE,
    PRIM_SHINY_NONE,
    PRIM_SHINY_LOW,
    PRIM_SHINY_MEDIUM,
    PRIM_SHINY_HIGH
];
list NAMES = [
    "white",
    "grey-50",
    "grey-20",
    "red",
    "green",
    "blue",
    "grey-20 + shiny LOW",
    "grey-20 + shiny MEDIUM",
    "grey-20 + shiny HIGH"
];

integer gStep = 0;

report()
{
    vector s = llGetSunDirection();
    llOwnerSay("step " + (string)gStep + "/" + (string)(llGetListLength(ALBEDOS) - 1)
        + "  " + llList2String(NAMES, gStep)
        + "  albedo=" + (string)llList2Vector(ALBEDOS, gStep)
        + "  shiny=" + (string)llList2Integer(SHINY, gStep)
        + "  sun_dir=" + (string)s
        + "  elevation=" + (string)(llAsin(s.z) * RAD_TO_DEG) + " deg"
        + "  region=" + llGetRegionName()
        + "  pos=" + (string)llGetPos());
}

apply()
{
    vector a = llList2Vector(ALBEDOS, gStep);
    integer shiny = llList2Integer(SHINY, gStep);
    llSetPrimitiveParams([
        PRIM_TYPE, PRIM_TYPE_SPHERE, PRIM_HOLE_DEFAULT,
            <0.0, 1.0, 0.0>, 0.0, ZERO_VECTOR, <0.0, 1.0, 0.0>,
        PRIM_SIZE, <2.0, 2.0, 2.0>,
        PRIM_TEXTURE,    ALL_SIDES, TEXTURE_BLANK, <1.0, 1.0, 0.0>, ZERO_VECTOR, 0.0,
        PRIM_COLOR,      ALL_SIDES, a, 1.0,
        PRIM_FULLBRIGHT, ALL_SIDES, FALSE,
        PRIM_GLOW,       ALL_SIDES, 0.0,
        // Bump stays NONE even in the shiny steps: a bump map would perturb the normal and
        // make the highlight's shape a second variable.
        PRIM_BUMP_SHINY, ALL_SIDES, shiny, PRIM_BUMP_NONE,
        PRIM_ALPHA_MODE, ALL_SIDES, PRIM_ALPHA_MODE_NONE, 0,
        // Clear any legacy material. A specular MAP would take precedence over the plain Shiny
        // level in both viewers (llface.cpp:1412 packs shininess into the vertex alpha only
        // "if we don't have a specular map"), so leaving one on would test the wrong path.
        PRIM_NORMAL,     ALL_SIDES, NULL_KEY, <1.0, 1.0, 0.0>, ZERO_VECTOR, 0.0,
        PRIM_SPECULAR,   ALL_SIDES, NULL_KEY, <1.0, 1.0, 0.0>, ZERO_VECTOR, 0.0,
            <1.0, 1.0, 1.0>, 0, 0
    ]);
}

default
{
    state_entry()
    {
        gStep = 0;
        apply();
        llOwnerSay("SLNG lighting probe ready -- touch to print the sun and step through "
            + (string)llGetListLength(ALBEDOS) + " settings.");
        report();
    }

    touch_start(integer n)
    {
        gStep = (gStep + 1) % llGetListLength(ALBEDOS);
        apply();
        report();
    }
}
