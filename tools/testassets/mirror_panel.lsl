// SLNG mirror panel -- the other half of the known-answer mirror rig.
//
// Pair this with mirror_probe.lsl. Together they remove every unknown from the comparison that
// BUG-RENDER-23..30 kept running into: a shop mirror whose construction nobody controls, in a room
// whose dimensions nobody measured, reflecting furniture at an unknown distance. Here the mirror,
// the target, the distances and the recipe are all known in advance, so the only variable left is
// the renderer.
//
// WHAT THIS BUILDS
//   A 1.00 x 2.00 m panel, 0.05 m thick, using SL's CLASSIC fake-mirror recipe: blank texture,
//   mid-grey tint, FULLBRIGHT, and Shiny stepped by touch. That combination is what the reference
//   viewer routes to fullbrightShinyF.glsl, where the unlit colour and the environment reflection
//   are mixed by the Shiny level (see BUG-RENDER-25):
//
//       color = mix(color, reflected_color * 0.5, envIntensity)   // envIntensity = Shiny level
//
//   Touch cycles NONE -> LOW -> MEDIUM -> HIGH, i.e. envIntensity 0 -> 0.25 -> 0.5 -> 0.75. That
//   ladder is the known answer: each step should replace a quarter more of the grey with
//   reflection, in BOTH viewers, and the panel says which step it is on in floating text.
//
// HOW TO USE
//   1. Rez a box against a wall, drop this in. It resizes and re-textures itself.
//      It does NOT move or rotate itself -- put it where you want it and leave it there, so the
//      geometry stays identical between the two viewers.
//   2. Rez a second box in front of it, drop mirror_probe.lsl in, point AWAY into the room.
//   3. Step the cube through its distances, and the panel through its Shiny levels.
//   4. Same screenshot in both viewers each time.
//
// WHAT TO READ OFF IT
//   Two known lengths in one frame -- the panel is 2.00 m tall, the cube 1.00 m -- so a pixel
//   measurement gives both the reflection's SCALE and its position without trusting the camera.
//     * cube too big in the mirror      -> the reflection is a cubemap, not a mirror
//     * cube stretched, not just scaled -> parallax against a box that is not the room
//     * reflection strength wrong       -> the Shiny -> envIntensity mapping, not the geometry
//
//   Grey on purpose: a mid-grey panel makes the mix visible at every step. A dark mirror hides a
//   weak reflection and a white one hides a strong one, and SL mirrors are usually dark -- which
//   is exactly how BUG-RENDER-23's first attempt went unnoticed for two days.
//
// NOT set here: SL's Reflection Probe flag (FLAG_MIRROR), which is what makes a REAL-time mirror
// (BUG-RENDER-24). SLNG reads that flag but has never met content carrying it. If your viewer's
// LSL has PRIM_REFLECTION_PROBE, a second rig with it would exercise that path -- deliberately
// left out of this script rather than risk it failing to compile.
//
// Written using only constructs that already compile in this folder (probe_lighting.lsl,
// compass_probe.lsl).

// Width, thickness, height in metres. 2 m tall so it is a full-length mirror and its own height is
// a second measuring stick in frame.
vector  PANEL_SIZE = <0.05, 1.0, 2.0>;

// The viewer's SHININESS_TO_ALPHA ladder (llface.cpp:1420) -- what each step means as the
// environment intensity the shader actually receives.
list    SHINY  = [PRIM_SHINY_NONE, PRIM_SHINY_LOW, PRIM_SHINY_MEDIUM, PRIM_SHINY_HIGH];
list    ENV    = [0.0, 0.25, 0.5, 0.75];
list    NAMES  = ["NONE", "LOW", "MEDIUM", "HIGH"];

integer gStep = 0;

report()
{
    llOwnerSay("[MirrorPanel] shiny=" + llList2String(NAMES, gStep)
        + "  envIntensity=" + (string)llList2Float(ENV, gStep)
        + "  size=" + (string)PANEL_SIZE + " m"
        + "  pos=" + (string)llGetPos()
        + "  region=" + llGetRegionName());
    llSetText("SLNG mirror panel\n1.0 x 2.0 m\nfullbright + shiny "
        + llList2String(NAMES, gStep)
        + "\nenv=" + (string)llList2Float(ENV, gStep),
        <0.0, 1.0, 1.0>, 1.0);
}

apply()
{
    integer shiny = llList2Integer(SHINY, gStep);
    llSetPrimitiveParams([
        PRIM_TYPE, PRIM_TYPE_BOX, 0, <0.0, 1.0, 0.0>, 0.0, <0.0, 0.0, 0.0>, <1.0, 1.0, 0.0>, <0.0, 0.0, 0.0>,
        PRIM_SIZE, PANEL_SIZE,
        PRIM_TEXTURE, ALL_SIDES, TEXTURE_BLANK, <1.0, 1.0, 0.0>, <0.0, 0.0, 0.0>, 0.0,
        // Mid grey, so the mix between surface and reflection is readable at every Shiny step.
        PRIM_COLOR, ALL_SIDES, <0.5, 0.5, 0.5>, 1.0,
        // The two halves of the classic recipe. Bump stays NONE: a bump map would perturb the
        // normal and make the reflection's shape a second variable.
        PRIM_FULLBRIGHT, ALL_SIDES, TRUE,
        PRIM_BUMP_SHINY, ALL_SIDES, shiny, PRIM_BUMP_NONE,
        PRIM_GLOW, ALL_SIDES, 0.0,
        PRIM_ALPHA_MODE, ALL_SIDES, PRIM_ALPHA_MODE_NONE, 0
    ]);
}

default
{
    state_entry()
    {
        gStep = 3;
        apply();
        llOwnerSay("SLNG mirror panel ready -- 1.0 x 2.0 m, fullbright, touch to step Shiny "
            + "NONE / LOW / MEDIUM / HIGH. Pair with mirror_probe.lsl in front of it.");
        report();
    }

    touch_start(integer n)
    {
        gStep = (gStep + 1) % llGetListLength(SHINY);
        apply();
        report();
    }

    on_rez(integer param)
    {
        llResetScript();
    }
}
