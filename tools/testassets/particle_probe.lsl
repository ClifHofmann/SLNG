// Particle probe -- known-answer rig for the particle path.
//
// The problem this exists for: a particle system can go missing at three places between
// the simulator and the screen (no PSBlock in the ObjectUpdate, a CRC of 0, an update
// that is not full), and all three look identical in-world -- no particles. SLNG logs
// [ParticleWire] when one arrives at the protocol boundary and [Particles] when the
// renderer configures it, so the two lines together say which half is broken. What they
// cannot do is make a system ARRIVE: scratch/TestParticles.lsl sets it once in
// state_entry, so if you were not already logged in and standing there, nothing is ever
// sent while you are watching.
//
// This script re-applies the system on a timer and on touch. Every re-apply makes the
// simulator schedule a full update carrying the block, so the wire log gets a fresh
// event on demand rather than only at rez time.
//
// Use: drop into a full-perm prim, watch client-output.log with the client on --diag.
//   - no [ParticleWire] within ~20 s  -> nothing is reaching the protocol layer
//   - [ParticleWire] but no [Particles] -> lost between the world model and the renderer
//   - both, but nothing visible       -> a rendering bug, and now a narrow one
//
// The parameters below are deliberately the plain fountain from the roadmap's first test,
// so the numbers in the log are predictable: 10 particles per 0.1 s burst, 3 s each,
// which is ~300 alive in steady state.

integer emitting = FALSE;
integer REAPPLY_SECONDS = 20;

start_particles()
{
    llParticleSystem([
        PSYS_PART_FLAGS, PSYS_PART_INTERP_COLOR_MASK
                       | PSYS_PART_INTERP_SCALE_MASK
                       | PSYS_PART_EMISSIVE_MASK,
        PSYS_SRC_PATTERN, PSYS_SRC_PATTERN_EXPLODE,
        PSYS_PART_MAX_AGE, 3.0,
        PSYS_PART_START_COLOR, <1.0, 0.5, 0.0>,
        PSYS_PART_END_COLOR, <1.0, 0.0, 0.0>,
        PSYS_PART_START_ALPHA, 1.0,
        PSYS_PART_END_ALPHA, 1.0,
        PSYS_PART_START_SCALE, <0.2, 0.2, 0.0>,
        PSYS_PART_END_SCALE, <1.0, 1.0, 0.0>,
        PSYS_SRC_BURST_RATE, 0.1,
        PSYS_SRC_BURST_PART_COUNT, 10,
        PSYS_SRC_BURST_RADIUS, 0.5,
        PSYS_SRC_BURST_SPEED_MIN, 1.0,
        PSYS_SRC_BURST_SPEED_MAX, 3.0,
        PSYS_SRC_ACCEL, <0.0, 0.0, -1.0>,
        PSYS_SRC_MAX_AGE, 0.0,
        PSYS_SRC_TEXTURE, "",
        PSYS_SRC_ANGLE_BEGIN, 0.0,
        PSYS_SRC_ANGLE_END, 0.0
    ]);
    emitting = TRUE;
    llSetText("particle probe: ON (re-sent every " + (string)REAPPLY_SECONDS + "s)\ntouch to stop",
        <0.0, 1.0, 0.0>, 1.0);
}

stop_particles()
{
    llParticleSystem([]);
    emitting = FALSE;
    llSetText("particle probe: OFF\ntouch to start", <1.0, 0.5, 0.0>, 1.0);
}

default
{
    state_entry()
    {
        // Announced in chat, because "the script is not running" and "the viewer drops the
        // block" are the two answers this rig has to tell apart, and only one of them can
        // say anything.
        llOwnerSay("particle probe ready -- re-sending every " + (string)REAPPLY_SECONDS + "s, touch to toggle");
        start_particles();
        llSetTimerEvent((float)REAPPLY_SECONDS);
    }

    touch_start(integer total_number)
    {
        if (emitting)
        {
            stop_particles();
            llOwnerSay("particle probe OFF -- expect the system to disappear from the viewer");
        }
        else
        {
            start_particles();
            llOwnerSay("particle probe ON -- a full update with the particle block is on its way now");
        }
    }

    timer()
    {
        if (emitting)
        {
            // Re-applying identical parameters still schedules a full update, which is the
            // whole point: it puts a particle block on the wire while you are watching.
            start_particles();
            llOwnerSay("particle probe re-sent");
        }
    }

    on_rez(integer start_param)
    {
        llResetScript();
    }
}
