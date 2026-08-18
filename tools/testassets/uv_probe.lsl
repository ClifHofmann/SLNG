// FEAT-RENDER-01 -- texture placement probe
//
// Setup: rez a box, drop this script AND the uvprobe texture into it.
// The script sizes the prim and steps through one texture-placement case at a
// time, with the active case floating over the prim so a SLNG screenshot and a
// Firestorm screenshot can never be compared in the wrong state.
//
//   touch          -> next case
//   /42 next|prev  -> step
//   /42 7          -> jump to case 7
//   /42 list       -> print the whole table
//   /42 reset      -> back to case 0
//   /42 size 4     -> re-size the prim to <4,4,4>

integer CHANNEL   = 42;
vector  PRIM_DIM  = <2.0, 2.0, 2.0>;

// name | repeat u,v | offset u,v | rotation (degrees) | texgen (0=default 1=planar)
list CASES = [
    "identity",             1.0,  1.0,   0.0,  0.0,     0.0, 0,
    "rot 90",               1.0,  1.0,   0.0,  0.0,    90.0, 0,
    "rot 180",              1.0,  1.0,   0.0,  0.0,   180.0, 0,
    "rot 270",              1.0,  1.0,   0.0,  0.0,   270.0, 0,
    "rot 45",               1.0,  1.0,   0.0,  0.0,    45.0, 0,
    "offset u +0.25",       1.0,  1.0,   0.25, 0.0,     0.0, 0,
    "offset v +0.25",       1.0,  1.0,   0.0,  0.25,    0.0, 0,
    "repeat 2 x 1",         2.0,  1.0,   0.0,  0.0,     0.0, 0,
    "repeat 1 x 2",         1.0,  2.0,   0.0,  0.0,     0.0, 0,
    "flip u (-1,1)",       -1.0,  1.0,   0.0,  0.0,     0.0, 0,
    "flip v (1,-1)",        1.0, -1.0,   0.0,  0.0,     0.0, 0,
    "rot 90 + repeat 2x2",  2.0,  2.0,   0.0,  0.0,    90.0, 0,
    "rot 90 + offset u.25", 1.0,  1.0,   0.25, 0.0,    90.0, 0,
    "rot 45 + repeat 2x2",  2.0,  2.0,   0.0,  0.0,    45.0, 0,
    "planar identity",      1.0,  1.0,   0.0,  0.0,     0.0, 1,
    "planar rot 90",        1.0,  1.0,   0.0,  0.0,    90.0, 1,
    "planar repeat 2x2",    2.0,  2.0,   0.0,  0.0,     0.0, 1,
    "SPREAD per-face rot",  1.0,  1.0,   0.0,  0.0,     0.0, 0
];
integer STRIDE = 7;

// case SPREAD gives every box face its own rotation, so one screenshot covers
// six angles at once -- including the 45 deg that tells "rotation ignored"
// apart from "rotation snapped to a right angle".
list SPREAD_DEG = [0.0, 45.0, 90.0, 135.0, 180.0, 270.0];

integer gCase;
string  gTex;

integer caseCount()
{
    return llGetListLength(CASES) / STRIDE;
}

apply(integer n)
{
    integer total = caseCount();
    gCase = ((n % total) + total) % total;

    integer i    = gCase * STRIDE;
    string  name = llList2String(CASES, i);
    float   ru   = llList2Float(CASES, i + 1);
    float   rv   = llList2Float(CASES, i + 2);
    float   ou   = llList2Float(CASES, i + 3);
    float   ov   = llList2Float(CASES, i + 4);
    float   deg  = llList2Float(CASES, i + 5);
    integer tg   = llList2Integer(CASES, i + 6);

    integer texgen = PRIM_TEXGEN_DEFAULT;
    if (tg) texgen = PRIM_TEXGEN_PLANAR;

    string detail;

    if (name == "SPREAD per-face rot")
    {
        list params = [PRIM_TEXGEN, ALL_SIDES, PRIM_TEXGEN_DEFAULT];
        integer f;
        integer faces = llGetNumberOfSides();
        for (f = 0; f < faces; ++f)
        {
            float fdeg = llList2Float(SPREAD_DEG, f % llGetListLength(SPREAD_DEG));
            params += [PRIM_TEXTURE, f, gTex, <1.0, 1.0, 0.0>, <0.0, 0.0, 0.0>,
                       fdeg * DEG_TO_RAD];
        }
        llSetLinkPrimitiveParamsFast(LINK_THIS, params);
        detail = "face0..5 rot = " + llDumpList2String(SPREAD_DEG, ", ") + " deg";
    }
    else
    {
        llSetLinkPrimitiveParamsFast(LINK_THIS, [
            PRIM_TEXGEN, ALL_SIDES, texgen,
            PRIM_TEXTURE, ALL_SIDES, gTex,
            <ru, rv, 0.0>, <ou, ov, 0.0>, deg * DEG_TO_RAD
        ]);
        detail = "repeat <" + (string)ru + ", " + (string)rv + ">  "
               + "offset <" + (string)ou + ", " + (string)ov + ">  "
               + "rot " + (string)deg + " deg  "
               + "texgen " + llList2String(["default", "planar"], tg);
    }

    string head = "case " + (string)gCase + "/" + (string)(total - 1) + " : " + name;
    llSetText(head + "\n" + detail, <1.0, 1.0, 0.0>, 1.0);
    llOwnerSay(head + "  |  " + detail);
}

setup()
{
    gTex = llGetInventoryName(INVENTORY_TEXTURE, 0);
    if (gTex == "")
    {
        llSetText("no texture in inventory\ndrop uvprobe_1024 into this prim",
                  <1.0, 0.2, 0.2>, 1.0);
        llOwnerSay("Drop the uvprobe texture into this prim, then reset the script.");
        return;
    }

    // a plain full-bright white box: no taper, no tint, no lighting, so the only
    // variable left in the frame is where the texture lands
    llSetLinkPrimitiveParamsFast(LINK_THIS, [
        PRIM_TYPE, PRIM_TYPE_BOX, PRIM_HOLE_DEFAULT,
                   <0.0, 1.0, 0.0>, 0.0, ZERO_VECTOR, <1.0, 1.0, 0.0>, ZERO_VECTOR,
        PRIM_SIZE, PRIM_DIM,
        PRIM_COLOR, ALL_SIDES, <1.0, 1.0, 1.0>, 1.0,
        PRIM_FULLBRIGHT, ALL_SIDES, TRUE,
        PRIM_GLOW, ALL_SIDES, 0.0
    ]);

    llListen(CHANNEL, "", llGetOwner(), "");
    llOwnerSay("UV probe ready on texture '" + gTex + "'. Touch to step, /"
               + (string)CHANNEL + " list for the table.");
    apply(0);
}

default
{
    state_entry()
    {
        setup();
    }

    on_rez(integer p)
    {
        llResetScript();
    }

    changed(integer c)
    {
        if (c & CHANGED_INVENTORY) llResetScript();
    }

    touch_start(integer n)
    {
        apply(gCase + 1);
    }

    listen(integer chan, string nm, key id, string msg)
    {
        msg = llToLower(llStringTrim(msg, STRING_TRIM));

        if (msg == "next")       apply(gCase + 1);
        else if (msg == "prev")  apply(gCase - 1);
        else if (msg == "reset") apply(0);
        else if (msg == "list")
        {
            integer i;
            integer total = caseCount();
            for (i = 0; i < total; ++i)
                llOwnerSay((string)i + " : " + llList2String(CASES, i * STRIDE));
        }
        else if (llGetSubString(msg, 0, 4) == "size ")
        {
            float m = (float)llGetSubString(msg, 5, -1);
            if (m > 0.01)
            {
                PRIM_DIM = <m, m, m>;
                llSetLinkPrimitiveParamsFast(LINK_THIS, [PRIM_SIZE, PRIM_DIM]);
                llOwnerSay("size -> " + (string)PRIM_DIM);
            }
        }
        else if ((string)((integer)msg) == msg)
        {
            apply((integer)msg);
        }
    }
}
