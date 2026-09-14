namespace SLNG.Core;

/// <summary>
/// FEAT-ANIM-03: decides which of an avatar's playing animations the local blender should see while
/// that avatar is sitting on something.
///
/// <para><b>This is a viewer-local decision, not parity.</b> Checked against the reference viewer
/// first: it keeps the same source map we now carry (<c>LLVOAvatar::mAnimationSources</c>,
/// filled in llviewermessage.cpp:4051-4077) but uses it only to stop an object's animations when
/// that object goes away (<c>stopMotionFromSource</c>, llvoavatarself.cpp:853-860) — never for
/// precedence. There, a furniture pose and an AO's sit animation are blended purely by priority,
/// which is exactly why AO HUDs ship a "disable while seated" patch script. SLNG owns its own
/// per-bone blender, so it can settle it at render time instead, for this viewer only. Nothing is
/// suppressed on the wire and no object's script is interfered with — the same class of local
/// choice as Firestorm's client-side AO options.</para>
///
/// <para>Why it is needed at all: <c>AvatarAnimationPlayer</c> resolves equal priorities as
/// last-one-wins, and a furniture pose and an AO sit are commonly both priority 4. Whichever
/// started most recently takes the bones — so re-triggering the AO steals the pose back, and that
/// alternation is the "fight" this removes.</para>
/// </summary>
public static class SeatPoseResolver
{
    /// <summary>
    /// Filters <paramref name="animationIds"/> down to what should actually be rendered.
    ///
    /// <para>The rule fires only when the avatar is seated <b>and</b> the seat is itself the source
    /// of a playing animation. Then the surviving set is: everything sourced by the seat, plus
    /// everything sourced by the agent itself. Anything sourced by another object — an AO HUD, a
    /// worn collar, a dance machine across the room — is dropped for as long as the seat is posing.
    /// </para>
    ///
    /// <para>Deliberately phrased as "not the seat and not the agent" rather than the spec's "one
    /// of this avatar's own attachments": it needs no attachment scan, and while a seat is posing
    /// you, a third object posing you too is the same problem wearing a different hat. The
    /// difference is documented rather than hidden — if a case turns up where a non-worn object
    /// legitimately animates a seated avatar, this is the line to narrow.</para>
    ///
    /// <para>Everything else falls through unchanged and returns the input list itself: standing,
    /// walking, a ground sit (no seat object, so no seat-sourced animation), or a seat that only
    /// seats you without posing you. FEAT-ANIM-01's locomotion prediction is untouched, because it
    /// only runs when not seated.</para>
    /// </summary>
    /// <param name="animationIds">The playing ids, in simulator order. Returned as-is when the rule
    /// does not fire, so the common path allocates nothing.</param>
    /// <param name="sources">Animation id → source object id. Null or empty disables the rule: with
    /// no sources there is nothing to tell apart.</param>
    /// <param name="seatObjectId">The object this avatar is sitting on, or <see cref="Guid.Empty"/>
    /// when it is not seated on one.</param>
    /// <param name="isBodyPose">Whether an animation actually poses the BODY — the pelvis, spine or
    /// legs — as opposed to keying only the hands or the face. Return false for anything not yet
    /// known: an unclassified animation is always kept.
    ///
    /// <para>This parameter is the difference between the rule working and the rule being
    /// actively harmful, and it was added after a live report. A pose stand sources a small
    /// <b>hand</b> animation while the body pose comes from a HUD. Without this test "the seat is
    /// sourcing something" was true, the HUD's body pose was dropped as a rival, and the avatar
    /// was left in the skeleton's bind pose — which in SL is a T-pose — with only the fingers
    /// changing between poses. Both halves of the rule now ask about the body: the seat only
    /// counts as posing you if it poses your body, and only a rival body pose is dropped, so a
    /// worn hand pose or facial expression survives either way.</para></param>
    public static IReadOnlyList<Guid> Resolve(
        IReadOnlyList<Guid> animationIds,
        IReadOnlyList<AnimationSignal>? sources,
        Guid seatObjectId,
        Func<Guid, bool>? isBodyPose = null)
    {
        if (seatObjectId == Guid.Empty) return animationIds;
        if (animationIds.Count == 0) return animationIds;
        if (sources == null || sources.Count == 0) return animationIds;

        // With no classifier at all the rule cannot tell a body pose from a hand pose, and the
        // live failure showed that guessing is worse than doing nothing.
        if (isBodyPose == null) return animationIds;

        bool seatIsPosing = false;
        foreach (var signal in sources)
        {
            if (signal.SourceObjectId != seatObjectId) continue;
            if (!isBodyPose(signal.AnimId)) continue;
            seatIsPosing = true;
            break;
        }

        // Seated on something that is not posing us -- a plain chair, a vehicle. Whatever the AO is
        // playing is the only pose there is, so leave it alone.
        if (!seatIsPosing) return animationIds;

        var kept = new List<Guid>(animationIds.Count);
        foreach (var id in animationIds)
        {
            if (Keep(id)) kept.Add(id);
        }

        // Never hand the blender an empty set because of this rule: a missing frame is a T-pose,
        // which is worse than the fight it was meant to fix. Cannot normally happen (the seat's own
        // animation always survives), so this is a guard, not a path.
        return kept.Count > 0 ? kept : animationIds;

        bool Keep(Guid id)
        {
            // Only a rival BODY pose is dropped. A hand pose or a facial expression from an
            // attachment does not compete with the seat's pose for the bones that matter, and
            // taking it away is how the avatar ended up with nothing but fingers moving.
            if (!isBodyPose(id)) return true;

            // An id the simulator listed with no source entry at all: treat as agent-sourced, i.e.
            // keep it. Dropping something we cannot classify would make the rule silently subtract
            // animations it was never meant to touch.
            bool classified = false;
            foreach (var signal in sources)
            {
                if (signal.AnimId != id) continue;
                classified = true;
                if (signal.SourceObjectId == seatObjectId || signal.SourceObjectId == Guid.Empty)
                    return true;
            }
            return !classified;
        }
    }
}
