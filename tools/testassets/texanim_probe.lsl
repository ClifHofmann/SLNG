// Known-answer probe for llSetTextureAnim (FEAT-RENDER texture animation).
//
// WHY A PROBE. "The animation runs the wrong way" cannot be settled on grid content: the
// direction of an SL texture animation follows entirely from its mode bits, those are invisible
// from outside, and most inworld content is no-modify so the bits cannot be read in Firestorm
// either. This rezzes a case whose bits we chose ourselves, so SLNG and Firestorm can be compared
// on a statement that is already known to be true.
//
// HOW TO USE
//   1. Rez a plain box and put tools/testassets/out/uvprobe_1024.png on it (upload with lossless
//      OFF -- it is a diffuse map, not a sculpt map). That texture already labels every cell of a
//      grid, so a frame can be named ("B3 plays first") instead of described. Any texture with an
//      obvious asymmetric feature works too; a symmetric tile cannot answer the question at all.
//   2. Drop this script in. It announces each phase in local chat.
//   3. Watch the same box in SLNG and in Firestorm, side by side, for one full cycle.
//   4. Report, per phase, whether the two move the same way -- and if not, which way each goes.
//
// Each phase isolates one axis, because they fail independently: our meshes are built flipV, so
// U and V do not carry the same sign correction, and ROTATE goes through a third path again.

integer PHASE_SECONDS = 12;
integer phase = 0;

announce(string text)
{
    llSetText(text, <1.0, 1.0, 0.0>, 1.0);
    llOwnerSay(text);
}

start_phase(integer p)
{
    llSetTextureAnim(FALSE, ALL_SIDES, 0, 0, 0.0, 0.0, 0.0);

    if (p == 0)
    {
        // Horizontal slide. The texture should travel steadily along the face's U axis.
        announce("1/3 SMOOTH scroll (U)\nwhich way does the pattern travel?");
        llSetTextureAnim(ANIM_ON | LOOP | SMOOTH, ALL_SIDES, 1, 1, 0.0, 1.0, 0.25);
    }
    else if (p == 1)
    {
        // Same slide, reversed. Whatever phase 1 did, this must do the opposite -- in BOTH
        // viewers. If phase 1 disagrees between viewers but phase 2 also disagrees in the same
        // direction, the fault is a sign; if the two phases disagree with each other, it is the
        // REVERSE bit itself.
        announce("2/3 SMOOTH scroll REVERSED\nmust be the exact opposite of phase 1");
        llSetTextureAnim(ANIM_ON | LOOP | SMOOTH | REVERSE, ALL_SIDES, 1, 1, 0.0, 1.0, 0.25);
    }
    else
    {
        // Stepped 2x2 flipbook: four discrete frames, one per second. This is the only phase
        // that shows the ROW ORDER -- a frame grid walks left to right along the top row first,
        // then down. A viewer that starts at the bottom row has its V axis inverted, which a
        // sliding animation cannot reveal.
        announce("3/3 2x2 flipbook, 1 fps\nwatch which quarter shows first, and the row order");
        llSetTextureAnim(ANIM_ON | LOOP, ALL_SIDES, 2, 2, 0.0, 0.0, 1.0);
    }
}

default
{
    state_entry()
    {
        phase = 0;
        start_phase(phase);
        llSetTimerEvent((float)PHASE_SECONDS);
    }

    timer()
    {
        phase = (phase + 1) % 3;
        start_phase(phase);
    }

    on_rez(integer p)
    {
        llResetScript();
    }
}
