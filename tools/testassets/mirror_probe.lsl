// SLNG mirror probe -- turns "der Spiegel zoomt" into a number.
//
// Rez a plain BOX in front of a mirror, drop this in, and it becomes a 1.00 m measuring cube that
// steps itself through known distances. Every round of the mirror investigation
// (BUG-RENDER-23..30) was argued from screenshots of shop furniture at unknown distances, and
// "the vase looks too big" cannot be compared between two viewers whose cameras and FOVs differ.
// This can.
//
// HOW TO USE
//   1. Rez a box on the floor, roughly on the mirror's centre line, and drop this script in.
//   2. It steps AWAY along region +X by default. If your mirror faces another way, edit AWAY
//      below to <-1,0,0>, <0,1,0> or <0,-1,0> -- whichever points from the mirror into the room.
//   3. Touch it. Each touch moves it to the next distance and says where it is in chat.
//   4. At each step take the SAME screenshot in both viewers -- stand still, same camera -- and
//      measure the cube's height in PIXELS twice: in the mirror, and directly.
//
// WHAT THE NUMBERS MEAN
//   real mirror    h_mirror / h_direct  =  d_direct / (a + c)    <- Firestorm is the control
//   cubemap at the mirror               =  d_direct / c          <- too big by (a + c) / c
//   with a = your distance to the glass and c = the cube's. Divide, compare, and the quotient
//   says which model the renderer is using instead of leaving it to the eye.
//
// One metre on purpose: the pixel measurement IS the scale, no arithmetic. A cube on purpose: its
// silhouette shows whether the reflection is STRETCHED or merely scaled, which a vase does not.
//
// Written using only the constructs that already compile in this folder's other probes
// (probe_lighting.lsl, compass_probe.lsl) -- two earlier drafts were rejected by the compiler,
// and a measuring tool that costs a debugging session of its own is worthless.

// Direction from the mirror into the room, in REGION axes. See step 2 above.
vector  AWAY = <1.0, 0.0, 0.0>;

// Metres in front of the mirror. Chosen to straddle the range where the error is worst: at 0.5 m
// a cubemap-at-the-mirror is ~4x too big for a viewer 1.5 m away, at 6 m it is ~1.25x.
list    DISTANCES = [0.5, 1.0, 2.0, 4.0, 6.0];

integer gStep = 0;
vector  gOrigin;

report()
{
    float d = llList2Float(DISTANCES, gStep);
    llOwnerSay("[MirrorProbe] step " + (string)gStep + "/" + (string)(llGetListLength(DISTANCES) - 1)
        + "  distance=" + (string)d + " m"
        + "  size=1.0 m cube"
        + "  pos=" + (string)llGetPos()
        + "  region=" + llGetRegionName());
    llSetText("SLNG mirror probe\n1.0 m cube\n" + (string)d + " m from start",
        <1.0, 1.0, 0.0>, 1.0);
}

apply()
{
    float d = llList2Float(DISTANCES, gStep);
    vector target = gOrigin + AWAY * d;
    llSetPrimitiveParams([
        PRIM_TYPE, PRIM_TYPE_BOX, 0, <0.0, 1.0, 0.0>, 0.0, <0.0, 0.0, 0.0>, <1.0, 1.0, 0.0>, <0.0, 0.0, 0.0>,
        PRIM_SIZE, <1.0, 1.0, 1.0>,
        // Matte white and untextured: this cube is the thing being LOOKED AT in the mirror, never
        // a second reflective surface competing with it, and a blank face makes the pixel-height
        // measurement clean -- the cube's EDGES already show whether the reflection is stretched.
        PRIM_TEXTURE, ALL_SIDES, TEXTURE_BLANK, <1.0, 1.0, 0.0>, <0.0, 0.0, 0.0>, 0.0,
        PRIM_COLOR, ALL_SIDES, <1.0, 1.0, 1.0>, 1.0,
        PRIM_FULLBRIGHT, ALL_SIDES, FALSE,
        PRIM_GLOW, ALL_SIDES, 0.0,
        PRIM_BUMP_SHINY, ALL_SIDES, PRIM_SHINY_NONE, PRIM_BUMP_NONE,
        PRIM_POSITION, target
    ]);
}

default
{
    state_entry()
    {
        gStep = 0;
        gOrigin = llGetPos();
        apply();
        llOwnerSay("SLNG mirror probe ready -- touch to step through "
            + (string)llGetListLength(DISTANCES) + " distances from here.");
        report();
    }

    touch_start(integer n)
    {
        gStep = (gStep + 1) % llGetListLength(DISTANCES);
        apply();
        report();
    }

    on_rez(integer param)
    {
        llResetScript();
    }
}
