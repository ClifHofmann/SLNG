// FEAT-RENDER-01 -- sculpt placement probe
//
// Companion to uv_probe.lsl, for the half of the problem that lives in the SCULPT grid rather
// than in the texture transform. Same idea: a known-answer object, so "it looks wrong" becomes
// "band 5 sits at the wrong height in SLNG".
//
// Setup: rez ANY prim, then drop in
//   * sculptprobe_16x256.png   -- uploaded LOSSLESS (it is geometry, not a picture)
//   * uvprobe_1024.png         -- uploaded normally
//   * this script
// The script turns the prim into the sculpt itself; no manual building needed.
//
//   touch          -> next case
//   /43 next|prev|<n>|list
//   /43 size 2 2 4 -> resize

integer CHANNEL = 43;
vector  PRIM_DIM = <2.0, 2.0, 4.0>;

// The real OSGrid stone that started this (object 38399801) is a cylinder-stitched sculpt with
// a 16x256 map and repeat <2.25, 6>. Case 1 reproduces exactly that; the others bracket it so a
// disagreement can be attributed to one axis.
list CASES = [
    "baseline 1 x 1",       1.0,  1.0,
    "REAL CASE 2.25 x 6",   2.25, 6.0,
    "1 x 6  (V only)",      1.0,  6.0,
    "2.25 x 1  (U only)",   2.25, 1.0,
    "4 x 4",                4.0,  4.0,
    "1 x 24 (V amplified)", 1.0,  24.0
];
integer STRIDE = 3;

integer gCase;
string  gMap;
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

    llSetLinkPrimitiveParamsFast(LINK_THIS, [
        PRIM_TEXTURE, ALL_SIDES, gTex, <ru, rv, 0.0>, <0.0, 0.0, 0.0>, 0.0
    ]);

    string head = "case " + (string)gCase + "/" + (string)(total - 1) + " : " + name;
    string detail = "repeat <" + (string)ru + ", " + (string)rv + ">   sculpt 16x256 cylinder";
    llSetText(head + "\n" + detail, <0.4, 1.0, 0.4>, 1.0);
    llOwnerSay(head + "  |  " + detail);
}

setup()
{
    // Two textures in inventory: the sculpt MAP is the one named like one. Guessing by name
    // rather than by slot because inventory order is not guaranteed.
    integer n = llGetInventoryNumber(INVENTORY_TEXTURE);
    integer i;
    gMap = "";
    gTex = "";
    for (i = 0; i < n; ++i)
    {
        string nm = llGetInventoryName(INVENTORY_TEXTURE, i);
        if (llSubStringIndex(llToLower(nm), "sculpt") != -1) gMap = nm;
        else                                                gTex = nm;
    }

    if (gMap == "" || gTex == "")
    {
        llSetText("need BOTH textures in inventory:\nsculptprobe_16x256 (lossless) + uvprobe_1024",
                  <1.0, 0.3, 0.3>, 1.0);
        llOwnerSay("Missing texture. sculpt map found: '" + gMap + "', diffuse found: '" + gTex + "'");
        return;
    }

    llSetLinkPrimitiveParamsFast(LINK_THIS, [
        PRIM_TYPE, PRIM_TYPE_SCULPT, gMap, PRIM_SCULPT_TYPE_CYLINDER,
        PRIM_SIZE, PRIM_DIM,
        PRIM_COLOR, ALL_SIDES, <1.0, 1.0, 1.0>, 1.0,
        PRIM_FULLBRIGHT, ALL_SIDES, TRUE
    ]);

    llListen(CHANNEL, "", llGetOwner(), "");
    llOwnerSay("Sculpt probe ready. map='" + gMap + "' diffuse='" + gTex + "'. Touch to step.");
    apply(0);
}

default
{
    state_entry()  { setup(); }
    on_rez(integer p) { llResetScript(); }
    changed(integer c) { if (c & CHANGED_INVENTORY) llResetScript(); }
    touch_start(integer n) { apply(gCase + 1); }

    listen(integer chan, string nm, key id, string msg)
    {
        msg = llToLower(llStringTrim(msg, STRING_TRIM));

        if (msg == "next")       apply(gCase + 1);
        else if (msg == "prev")  apply(gCase - 1);
        else if (msg == "list")
        {
            integer i;
            integer total = caseCount();
            for (i = 0; i < total; ++i)
                llOwnerSay((string)i + " : " + llList2String(CASES, i * STRIDE));
        }
        else if (llGetSubString(msg, 0, 4) == "size ")
        {
            list p = llParseString2List(llGetSubString(msg, 5, -1), [" "], []);
            if (llGetListLength(p) == 3)
            {
                PRIM_DIM = <(float)llList2String(p, 0),
                            (float)llList2String(p, 1),
                            (float)llList2String(p, 2)>;
                llSetLinkPrimitiveParamsFast(LINK_THIS, [PRIM_SIZE, PRIM_DIM]);
                llOwnerSay("size -> " + (string)PRIM_DIM);
            }
        }
        else if ((string)((integer)msg) == msg) apply((integer)msg);
    }
}
