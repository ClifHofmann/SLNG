// Known-answer probe for script permission requests (FEAT-NET-01, llRequestPermissions).
//
// WHY A PROBE. The feature is a prompt that appears when a script asks the agent for a
// permission -- and on a real grid you cannot make that happen on demand. Sitting on something
// grants animation permission implicitly, so the obvious test (a pose stand) exercises the one
// path that never shows a prompt at all; and no-modify content will not tell you WHICH bits it
// asked for, so a prompt naming the wrong permission would look exactly like a correct one.
// This asks for bits we chose ourselves and then says what it was actually granted, so the
// viewer's prompt and the script's answer can be compared against a statement already known to
// be true.
//
// It also tests the half that is easy to get wrong in the other direction: a REFUSAL is a real
// answer, not silence. The reference viewer always replies and zeroes the granted bits
// (llviewermessage.cpp:5600-5632); a viewer that simply closes the window leaves the script
// waiting forever, and from the outside that is indistinguishable from "the script is broken".
// Phase 2 below exists to be denied.
//
// HOW TO USE
//   1. Rez a plain box on the local OpenSim test grid (full-perm; a probe like this does not
//      belong on live SL) and drop this script in.
//   2. Touch it. Each touch runs the NEXT phase and announces beforehand, in local chat, exactly
//      which permissions it is about to ask for. Compare that against what the prompt names.
//   3. Phase 1 -- ANIMATION + TRACK_CAMERA. Harmless. GRANT it.
//      Expect: the prompt names both, and chat reports both as granted.
//   4. Phase 2 -- DEBIT ("spend your money"). DENY it, or just close the window without
//      answering. This script never calls llGiveMoney and cannot spend anything either way.
//      Expect: the prompt names Debit in plain words rather than as a bare number, Deny is the
//      focused button -- and chat reports "granted: (none)" within a second or two. If chat
//      stays silent, the refusal never reached the simulator and this is the bug the feature
//      exists to fix.
//   5. Phase 3 -- an UNNAMED bit (0x40000000) alongside ANIMATION. Grant or deny as you like.
//      Expect: the prompt still lists the unknown bit, as a raw number, instead of hiding it.
//      Granting something the prompt never mentioned is the one outcome it must prevent.
//
//      MEASURED 2026-09-21: this phase CANNOT be tested on OpenSim. The script asked for
//      1073741840 and the viewer received 0x10 -- animation alone. The simulator strips bits it
//      does not recognise before it ever sends the ScriptQuestion, so the prompt has nothing
//      unknown to show and a correct viewer and a broken one look identical here. Left in place
//      because the phase costs nothing and a grid that passes the bit through would make it
//      meaningful again; the viewer-side behaviour is pinned by DescribePermissions instead.
//
// The phases cycle, so touching again starts over at phase 1.

integer PHASE = 0;

// A bit no LSL constant claims. Deliberate: the interesting question is not what the viewer does
// with the flags it knows, it is what it does with one it does not.
integer UNKNOWN_BIT = 0x40000000;

// Turns the granted mask back into words, so the report can be read against the prompt without
// anyone converting hex in their head. Unknown bits are reported as a number rather than dropped.
string describe(integer perm)
{
    if (perm == 0) return "(none)";

    string out = "";
    integer known = 0;

    if (perm & PERMISSION_DEBIT)                { out += "Debit "; known = known | PERMISSION_DEBIT; }
    if (perm & PERMISSION_TAKE_CONTROLS)        { out += "TakeControls "; known = known | PERMISSION_TAKE_CONTROLS; }
    if (perm & PERMISSION_TRIGGER_ANIMATION)    { out += "Animation "; known = known | PERMISSION_TRIGGER_ANIMATION; }
    if (perm & PERMISSION_ATTACH)               { out += "Attach "; known = known | PERMISSION_ATTACH; }
    if (perm & PERMISSION_TRACK_CAMERA)         { out += "TrackCamera "; known = known | PERMISSION_TRACK_CAMERA; }
    if (perm & PERMISSION_CONTROL_CAMERA)       { out += "ControlCamera "; known = known | PERMISSION_CONTROL_CAMERA; }
    if (perm & PERMISSION_TELEPORT)             { out += "Teleport "; known = known | PERMISSION_TELEPORT; }
    if (perm & PERMISSION_OVERRIDE_ANIMATIONS)  { out += "OverrideAnimations "; known = known | PERMISSION_OVERRIDE_ANIMATIONS; }

    integer rest = perm & ~known;
    if (rest != 0) out += "+unnamed(" + (string)rest + ") ";

    return out;
}

ask(key who, integer perm, string what)
{
    llOwnerSay("Permission probe, phase " + (string)PHASE + ": asking for " + what
        + "  [mask " + (string)perm + " = " + describe(perm) + "]");
    llRequestPermissions(who, perm);
}

default
{
    state_entry()
    {
        PHASE = 0;
        llSetText("Permission probe\ntouch me", <1.0, 1.0, 1.0>, 1.0);
        llOwnerSay("Permission probe ready. Touch to run phase 1 (grant), 2 (DENY this one), 3 (unknown bit).");
    }

    touch_start(integer n)
    {
        key who = llDetectedKey(0);

        PHASE = PHASE + 1;
        if (PHASE > 3) PHASE = 1;

        if (PHASE == 1)
        {
            ask(who, PERMISSION_TRIGGER_ANIMATION | PERMISSION_TRACK_CAMERA,
                "Animation + TrackCamera -- harmless, GRANT this one");
        }
        else if (PHASE == 2)
        {
            ask(who, PERMISSION_DEBIT,
                "Debit -- the expensive one. DENY it, or close the window. "
                + "This script never calls llGiveMoney; the point is whether the refusal comes back");
        }
        else
        {
            ask(who, PERMISSION_TRIGGER_ANIMATION | UNKNOWN_BIT,
                "Animation + an UNNAMED bit -- the prompt must still show the unknown one");
        }
    }

    // The answer, whichever way it went. A refusal arrives here too, as a zeroed mask -- which is
    // exactly what must NOT be silence.
    run_time_permissions(integer perm)
    {
        llOwnerSay("Permission probe, phase " + (string)PHASE + ": granted " + (string)perm
            + " = " + describe(perm));

        if (PHASE == 2 && perm == 0)
        {
            llOwnerSay("  ^ correct: the refusal came back. A viewer that just closed the window "
                + "would leave this script waiting and nothing would be printed here.");
        }
    }

    on_rez(integer p)
    {
        llResetScript();
    }
}
