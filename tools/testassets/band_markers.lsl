// Known-answer markers for the FEAT-RENDER-02 crossfade terrace.
//
// WHY A PROBE. Three rounds of side-by-side screenshots of that terrace could not settle whether
// the two viewers paint it the same way, for one dull reason: the two cameras never stand in the
// same place, so "green on the left, red on the right" is a statement about the crop and not about
// the region. This plants a pole at each of six known world coordinates instead. A pole is at the
// same place in both viewers by construction, and the tile it stands on is then directly readable.
//
// Each pole is COLOURED WITH ITS OWN EXPECTED ANSWER. Our port puts three of these points on the
// red side of the crossfade and three on the green side, so a pole that matches the ground under
// it is a point where we agree with the viewer, and a green pole standing on a red tile is a
// disagreement you cannot miss or misread. That is the whole design: no counting, no colour
// judgement, no crop.
//
// The coordinates come from replaying SlTerrainComposition over the 50 m terrace of
// terrain_bands.raw (see gen_terrain_map.py). They are only meaningful WITH that map imported and
// the estate set to low 20 / high 240 -- on any other terrain they are six arbitrary spots.
//
// HOW TO USE
//   1. Import out/terrain_bands.raw and set all four corners to low 20 / high 240.
//   2. Rez a plain box and drop this script in.
//   3. Take a COPY of that same box into inventory, rename the copy to exactly BandMarker, and
//      drop it back into the box's Contents. There is only ever one script and one prim to build:
//      the object carries a copy of itself, and each instance decides which role it is in from its
//      rez parameter -- 0 means "I was rezzed by hand, I am the rezzer", anything else means
//      "I am marker number N".
//   4. Touch the rezzer. Six poles fly to their coordinates and label themselves.
//   5. Photograph the terrace in both viewers. Compare pole colour against the tile it stands on.
//   6. Touch any pole to remove it, or touch the rezzer again to clear them all.
//
// llRezObject refuses to place anything further than 10 m from the rezzer and returns SILENTLY
// when you ask it to (OpenSim LSL_Api.cs, doObjectRez). So the poles are rezzed in a short fan
// beside the rezzer and then move themselves with llSetRegionPos, which is not distance-limited
// within the region -- unlike llSetPos, which caps at 10 m per call.
//
// Every step that can fail says so. llSetRegionPos returns 0 without any error when the target
// sits on a parcel the owner may not rez on, and the first version of this script ignored that
// return: the markers stayed at the rezzer, stacked on one spot, still labelled with coordinates
// they had never reached. Six poles on one spot look like exactly one pole -- which is how that
// run read as "only one cube got rezzed". Hence the fan, and hence the reporting.

// <x, y> in region metres, the expected band (0 = detail 1 red, 1 = detail 2 green), and the
// composition our port computes there. All six sit on the flat 50 m terrace, so height contributes
// a constant and the Perlin term alone decides them.
list POINTS = [
    // x    y    expected band   composition
    184,  82,   0,              37,
    150,  54,   0,              38,
    100,  66,   0,              38,
      8,  60,   1,              62,
     48,  78,   1,              65,
    162,  94,   1,              66
];

float TERRACE_Z   = 50.0;   // the crossfade terrace's height, from gen_terrain_map.py
float POLE_HEIGHT = 12.0;   // tall enough to find from the air, thin enough not to hide the tile
integer CLEAR_CHANNEL = -80244;

string  MARKER_NAME = "BandMarker";

// Deliberately the probe texture's own colours rather than pure red/green: the point of the pole
// is to be compared against the tile beneath it, and a colour that is not the tile's own colour
// makes that comparison harder than it needs to be.
vector RED   = <0.863, 0.118, 0.118>;   // terrain_probe_1.png, (220, 30, 30)
vector GREEN = <0.118, 0.784, 0.235>;   // terrain_probe_2.png, (30, 200, 60)

rezzer_place()
{
    if (llGetInventoryType(MARKER_NAME) != INVENTORY_OBJECT)
    {
        llOwnerSay("Put a prim named '" + MARKER_NAME + "' (with this same script inside) "
                   + "into my Contents first.");
        return;
    }

    // Clear anything from a previous run before placing, so touching twice is a reset rather than
    // a way to end up with two poles in the same spot.
    llRegionSay(CLEAR_CHANNEL, "clear");

    integer i;
    integer n = llGetListLength(POINTS) / 4;
    for (i = 0; i < n; ++i)
    {
        // Rezzed next to the rezzer because llRezObject refuses anything beyond 10 m (OpenSim
        // LSL_Api.cs doObjectRez, and it returns silently); the marker moves itself from there.
        // The index rides along as the rez parameter -- the marker looks its own row up from the
        // same list, so the two roles cannot disagree about what point i is.
        //
        // SPREAD OUT, not stacked. Six poles all rezzed on one spot look like ONE pole if any of
        // them then fails to move, which is exactly how the first run of this script read as
        // "only one cube got rezzed". Fanned out, a marker that stayed behind is visible as a
        // marker that stayed behind.
        vector spot = llGetPos() + <(float)(i - 3) * 1.2, 0.0, 1.5>;
        llRezObject(MARKER_NAME, spot, ZERO_VECTOR, ZERO_ROTATION, i + 1);
    }
    llOwnerSay("rezzed " + (string)n + " markers; each reports whether it reached its "
               + "coordinate. Red pole = we expect digit 1, "
               + "green pole = we expect digit 2. A pole whose colour differs from the tile "
               + "under it is a disagreement.");
}

// Both state_entry and on_rez route through here, and it is idempotent on purpose. Their firing
// order at rez time is not something to rely on -- guarding one against the other was the first
// attempt and it made the outcome depend on how the object had been taken and re-rezzed. Whichever
// fires, or both, the object ends up configured the same way.
marker_setup()
{
    integer index = llGetStartParameter();
    if (index <= 0) return;

    integer row = (index - 1) * 4;
    integer x    = llList2Integer(POINTS, row);
    integer y    = llList2Integer(POINTS, row + 1);
    integer band = llList2Integer(POINTS, row + 2);
    integer comp = llList2Integer(POINTS, row + 3);

    vector colour = GREEN;
    integer digit = 2;
    if (band == 0)
    {
        colour = RED;
        digit = 1;
    }

    llSetLinkPrimitiveParamsFast(LINK_THIS, [
        PRIM_SIZE, <0.25, 0.25, POLE_HEIGHT>,
        PRIM_COLOR, ALL_SIDES, colour, 1.0,
        PRIM_FULLBRIGHT, ALL_SIDES, TRUE,   // so the pole reads the same under either viewer's sun
        PRIM_PHANTOM, TRUE,
        PRIM_TEXTURE, ALL_SIDES, TEXTURE_BLANK, <1.0, 1.0, 0.0>, ZERO_VECTOR, 0.0
    ]);

    llListen(CLEAR_CHANNEL, "", NULL_KEY, "");

    // Base sits on the terrace, so the pole marks the tile it stands on rather than hovering over
    // an ambiguous seam.
    vector target = <(float)x, (float)y, TERRACE_Z + POLE_HEIGHT * 0.5>;

    // The return value is the whole point. llSetRegionPos answers 0 and says nothing else when the
    // destination lies on a parcel the owner may not rez on (OpenSim LSL_Api.cs: the !sameParcel
    // CanRezObject check), and a marker that quietly stayed behind while still LABELLED with the
    // coordinate it never reached is worse than no marker at all -- it is a measuring instrument
    // reporting a position it is not at.
    integer ok = llSetRegionPos(target);

    if (ok)
    {
        llSetText("<" + (string)x + ", " + (string)y + ">
expect digit " + (string)digit
                  + "
composition 0." + (string)comp, colour, 1.0);
        llOwnerSay("marker " + (string)index + " -> <" + (string)x + ", " + (string)y + ">  ok");
    }
    else
    {
        // Stays visible and says what it is, rather than pretending.
        llSetText("MARKER " + (string)index + " COULD NOT MOVE
wanted <" + (string)x + ", "
                  + (string)y + ">", <1.0, 1.0, 0.0>, 1.0);
        llOwnerSay("marker " + (string)index + " FAILED to reach <" + (string)x + ", " + (string)y
                   + ">. llSetRegionPos returned 0 -- most likely that spot is on another parcel "
                   + "you cannot rez on. It is still sitting at the rezzer.");
    }
}

default
{
    on_rez(integer param)
    {
        marker_setup();
    }

    state_entry()
    {
        if (llGetStartParameter() > 0)
        {
            marker_setup();
            return;
        }

        llSetText("", ZERO_VECTOR, 0.0);
        if (llGetInventoryType(MARKER_NAME) != INVENTORY_OBJECT)
        {
            llOwnerSay("I am the rezzer, but I have no '" + MARKER_NAME + "' in Contents yet. "
                       + "Put a plain prim of that name, with this same script inside, into my "
                       + "Contents -- then touch me.");
        }
        else
        {
            llOwnerSay("Ready. Touch me to place " + (string)(llGetListLength(POINTS) / 4)
                       + " markers on the 50 m terrace.");
        }
    }

    touch_start(integer total)
    {
        // A marker removes itself; the rezzer places (and first clears) the set.
        if (llGetStartParameter() > 0) llDie();
        else rezzer_place();
    }

    listen(integer chan, string name, key id, string msg)
    {
        if (msg == "clear" && llGetStartParameter() > 0) llDie();
    }
}
