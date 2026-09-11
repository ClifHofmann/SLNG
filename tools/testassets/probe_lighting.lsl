// SLNG lighting probe -- a neutral reference sphere for Firestorm/SLNG A/B.
//
// WHY A SPHERE. A flat face gives one NdotL sample; a sphere gives the whole falloff from
// NdotL 1 at the sun-facing pole down to 0 at the terminator, in a single screenshot. That
// curve is the thing under test: SLNG hits the viewer's two ENDPOINTS exactly (verified in
// the log to three decimals) but sums its lights linearly, where the viewer sums sun and
// ambient in sRGB and runs pow(NdotL, 1.2) first. Endpoints cannot distinguish those; a
// falloff can.
//
// Everything that would add a second, unknown term to a pixel is switched off here: no
// texture, no fullbright, no shininess or bump, no glow, no normal/specular material. What
// is left on the sphere is only the light that reached it.
//
// USE
//   1. Rez a prim in the OPEN -- nothing overhead, a few metres clear of walls, so no cast
//      shadow and no colour bleed from a neighbour lands on it.
//   2. Drop this script in. The prim becomes a 2 m white sphere.
//   3. Touch it to print the sun direction and to cycle albedo.
//   4. Snapshot in both viewers from roughly the same angle, at each step.
//
// WHY THESE SIX STEPS. The greys measure the light's LEVEL: white reads the sun end, the
// dark step reads the shadow end, which is where "jeans and hair look flat" lives. The pure
// primaries measure it PER CHANNEL, and they are not redundant -- a grey sphere weights the
// channels by luminance, so blue contributes only 7% and an error there hides almost
// completely in grey. A pure blue sphere has zero albedo in red and green, so that one
// channel stands alone at full strength. The bug this probe was built for was exactly a
// per-channel ratio error (R/B off by a 2.4 power), which grey alone would not have caught.

list ALBEDOS = [
    <1.0, 1.0, 1.0>,
    <0.5, 0.5, 0.5>,
    <0.2, 0.2, 0.2>,
    <1.0, 0.0, 0.0>,
    <0.0, 1.0, 0.0>,
    <0.0, 0.0, 1.0>
];
list NAMES = ["white", "grey-50", "grey-20", "red", "green", "blue"];
integer gStep = 0;

report()
{
    vector s = llGetSunDirection();
    llOwnerSay("albedo=" + llList2String(NAMES, gStep)
        + " " + (string)llList2Vector(ALBEDOS, gStep)
        + "  sun_dir=" + (string)s
        + "  elevation=" + (string)(llAsin(s.z) * RAD_TO_DEG) + " deg"
        + "  region=" + llGetRegionName()
        + "  pos=" + (string)llGetPos());
}

apply()
{
    vector a = llList2Vector(ALBEDOS, gStep);
    llSetPrimitiveParams([
        PRIM_TYPE, PRIM_TYPE_SPHERE, PRIM_HOLE_DEFAULT,
            <0.0, 1.0, 0.0>, 0.0, ZERO_VECTOR, <0.0, 1.0, 0.0>,
        PRIM_SIZE, <2.0, 2.0, 2.0>,
        PRIM_TEXTURE,    ALL_SIDES, TEXTURE_BLANK, <1.0, 1.0, 0.0>, ZERO_VECTOR, 0.0,
        PRIM_COLOR,      ALL_SIDES, a, 1.0,
        PRIM_FULLBRIGHT, ALL_SIDES, FALSE,
        PRIM_GLOW,       ALL_SIDES, 0.0,
        PRIM_BUMP_SHINY, ALL_SIDES, PRIM_SHINY_NONE, PRIM_BUMP_NONE,
        PRIM_ALPHA_MODE, ALL_SIDES, PRIM_ALPHA_MODE_NONE, 0,
        // Clear any legacy material -- a specular map would put a second, unknown highlight
        // on the sphere and make the falloff unreadable.
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
        llOwnerSay("SLNG lighting probe ready -- touch to print the sun and cycle albedo.");
        report();
    }

    touch_start(integer n)
    {
        gStep = (gStep + 1) % llGetListLength(ALBEDOS);
        apply();
        report();
    }
}
