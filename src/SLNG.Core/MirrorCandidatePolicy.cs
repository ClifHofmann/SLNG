using System;

namespace SLNG.Core;

/// <summary>
/// FEAT-PERF-15: which surfaces may become the planar mirror's subject.
///
/// <para>The reference viewer has exactly one rule: an object becomes a mirror (hero-probe subject)
/// when the sim flagged it so -- <c>getReflectionProbeIsMirror()</c>, registered in
/// llvovolume.cpp:4536 and consumed in llheroprobemanager.cpp:141 -- and only while
/// <c>RenderMirrors</c> is on. SLNG additionally guesses from two surface recipes that predate that
/// flag (the fullbright + Shiny HIGH fake mirror, and a glTF material rough enough to be glass).
/// Both recipes are just as true of a polished floor, a beach or a water-like plane as they are of
/// a mirror, and a floor is never one: this is the gate that keeps those out. A sim-flagged mirror
/// is the creator's statement and is never second-guessed.</para>
/// </summary>
public static class MirrorCandidatePolicy
{
    /// <summary>|normal.Y| above this reads as "faces up or down": ground, sand, a tabletop, a
    /// ceiling. 0.7 is roughly 45 degrees off the vertical, so a tilted mirror still qualifies and a
    /// gentle slope does not.</summary>
    public const float MaxVerticalNormalY = 0.7f;

    /// <summary>Whether a surface whose front face points along <paramref name="normalY"/> (the Y
    /// component of the unit normal, Y up) is upright enough for a heuristic mirror. Only the
    /// heuristic sources are tested with this; a sim-flagged mirror qualifies in any orientation.</summary>
    public static bool IsUpright(float normalY) => MathF.Abs(normalY) <= MaxVerticalNormalY;
}
