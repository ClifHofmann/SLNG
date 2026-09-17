// Known-answer probe for MOAP (Media On A Prim) -- MVP3-3 Phase 1.
//
// WHY A PROBE. Phase 1 only fetches per-face MediaEntry data over the ObjectMedia capability
// and logs it -- there is no in-world visual change yet and no inspector UI, so there is
// nothing to look at, and no-modify vendor content on the grid cannot tell you what MediaEntry
// values it was actually authored with. This rezzes a box whose per-face media we chose
// ourselves, so SLNG's own [Media] diagnostic log line can be checked against a known answer
// instead of guessed at.
//
// HOW TO USE
//   1. Rez a plain box (full-perm, on the local OpenSim test grid -- never live SL for a
//      probe like this) and drop this script in.
//   2. state_entry sets media on exactly TWO faces (FACE_A and FACE_B below) out of the box's
//      six and reports what it set in local chat, including llSetPrimMediaParams' own status
//      code for each call (0 = success; a nonzero status means the sim rejected it and
//      SLNG's fetch has nothing to find).
//   3. Log in with SLNG and get the box into view (--diag is NOT required -- the [Media] line
//      is unconditional). Watch the console/log for:
//          [Media] object <localid> (<uuid-prefix>) version=x-mv:.../<agent> faces=2/6
//      "2/6" must match step 2's "2 faces set out of 6" -- a different count means either the
//      doorbell/face-index mapping is wrong, or SLNG merged/miscounted faces.
//   4. Touch the box. This bumps FACE_A's CurrentURL (appends a click counter) and re-sends
//      both faces' params, which the sim answers with a NEW x-mv: version even though nothing
//      else changed -- exactly the case GridSession's version-gated re-fetch
//      (_lastMediaVersionByLocalId) exists to catch. A second [Media] line with a NEW version
//      and the same faces=2/6 count means the change was detected; no second line at all means
//      the gate is stuck on the old version.
//
// Both URLs are REAL, live, currently-reachable addresses (checked 2026-09-17), not
// placeholder domains that 404 the moment anyone actually loads them -- useful now for
// sanity-checking by hand, and later without editing this script once Phase 2 (click ->
// open in system browser) or Phase 3 (direct-image face rendering) exist to load them for
// real: FACE_A is a real PNG (Wikimedia Commons, stable/CDN-hosted -- a genuine image
// response, the shape Phase 3 needs), FACE_B is a real ordinary webpage (secondlife.com --
// also thematically on-brand for an SL/OpenSim viewer's own test probe).
//
// Deliberately does NOT exercise PRIM_MEDIA_WHITELIST: that field's list-valued LSL syntax
// isn't nailed down by any known-compiling example in this repo, and the whitelist MATCHING
// algorithm itself is already covered end-to-end by MediaWhitelistTests against the real
// viewer source -- this probe's job is the wire fetch and face indexing, not re-proving that.
//
// FACE_A and FACE_B are SL face numbers -- the same numbering SLNG's own FaceTexture[] array
// must land the fetched media on. Which physical side of the box that is doesn't matter for
// this test; only the INDEX and the fact that the other four faces carry none does.

integer FACE_A = 0;
integer FACE_B = 2;
integer gClicks = 0;

setMedia()
{
    // A real Wikimedia Commons PNG, with a harmless query-string counter appended on each
    // touch -- real CDNs and static-file servers ignore an unrecognized query string and
    // still serve the same file, so this stays a genuinely loadable image on every click
    // while still forcing OpenSim to bump the x-mv: version (see step 4 above).
    string urlA = "https://upload.wikimedia.org/wikipedia/commons/4/47/PNG_transparency_demonstration_1.png"
        + "?slng_probe_click=" + (string)gClicks;

    integer statusA = llSetPrimMediaParams(FACE_A, [
        PRIM_MEDIA_HOME_URL, urlA,
        PRIM_MEDIA_CURRENT_URL, urlA,
        PRIM_MEDIA_AUTO_PLAY, TRUE,
        PRIM_MEDIA_AUTO_LOOP, FALSE,
        PRIM_MEDIA_AUTO_SCALE, TRUE,
        PRIM_MEDIA_AUTO_ZOOM, FALSE,
        PRIM_MEDIA_FIRST_CLICK_INTERACT, TRUE,
        PRIM_MEDIA_WIDTH_PIXELS, 640,
        PRIM_MEDIA_HEIGHT_PIXELS, 480,
        PRIM_MEDIA_CONTROLS, PRIM_MEDIA_CONTROLS_STANDARD,
        PRIM_MEDIA_PERMS_INTERACT, PRIM_MEDIA_PERM_ANYONE,
        PRIM_MEDIA_PERMS_CONTROL, PRIM_MEDIA_PERM_OWNER
    ]);

    string urlB = "https://secondlife.com/";
    integer statusB = llSetPrimMediaParams(FACE_B, [
        PRIM_MEDIA_HOME_URL, urlB,
        PRIM_MEDIA_CURRENT_URL, urlB,
        PRIM_MEDIA_AUTO_PLAY, FALSE,
        PRIM_MEDIA_AUTO_LOOP, FALSE,
        PRIM_MEDIA_AUTO_SCALE, FALSE,
        PRIM_MEDIA_AUTO_ZOOM, FALSE,
        PRIM_MEDIA_FIRST_CLICK_INTERACT, FALSE,
        PRIM_MEDIA_WIDTH_PIXELS, 320,
        PRIM_MEDIA_HEIGHT_PIXELS, 240,
        PRIM_MEDIA_CONTROLS, PRIM_MEDIA_CONTROLS_MINI,
        PRIM_MEDIA_PERMS_INTERACT, (PRIM_MEDIA_PERM_OWNER | PRIM_MEDIA_PERM_GROUP),
        PRIM_MEDIA_PERMS_CONTROL, PRIM_MEDIA_PERM_OWNER
    ]);

    llOwnerSay("MOAP probe (click " + (string)gClicks + "):\n"
        + "face " + (string)FACE_A + " status=" + (string)statusA + " -> " + urlA
        + " (auto_play, standard controls, ANYONE interact / OWNER control)\n"
        + "face " + (string)FACE_B + " status=" + (string)statusB + " -> " + urlB
        + " (mini controls, OWNER+GROUP interact / OWNER control)\n"
        + "faces 1,3,4,5 carry no media -- expect SLNG's [Media] line to read faces=2/6");
}

default
{
    state_entry()
    {
        gClicks = 0;
        setMedia();
    }

    touch_start(integer n)
    {
        gClicks++;
        setMedia();
    }

    on_rez(integer p)
    {
        llResetScript();
    }
}
