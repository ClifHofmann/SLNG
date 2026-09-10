# [BUG-ANIM-02] Hands jitter/tremble when no AO HUD is worn

- **Feature ID:** `BUG-ANIM-02`
- **Track:** `render`
- **Status:** `⏸️ Pending`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

User-reported: with **no AO HUD worn**, the self avatar's **hands visibly jitter**
("zittern / tittern") — a high-frequency per-frame wobble, not a slow drift. Wearing any
AO HUD makes it stop.

That an AO masks it is the key clue: an AO supplies **one high-priority animation** that
owns the arm/hand bones deterministically. Without it, the hand bones are driven only by
the sim's default low-priority stand/idle set (`ANIM_AGENT_STAND` family, `express_*`
fidgets) plus, for the self avatar, the FEAT-ANIM-01 predicted `ANIM_AGENT_STAND`.

## Suspected causes (to confirm with a diagnostic pass first)

1. **Equal-priority tie has no deterministic tie-break.**
   [`AvatarAnimationPlayer.ApplyBonePoses`](file:///E:/Git/SLNG/app/scripts/AvatarAnimationPlayer.cs)
   resolves per bone with `existing.priority > effectivePriority` (strict `>`), so on an
   **equal** priority the animation that comes **later in `_active`** wins. Two
   stand/idle clips that both key the wrist/finger bones at the same authored priority
   will fight; anything that perturbs their evaluated values or order per frame produces
   oscillation. Same failure class as BUG-RENDER-16 (per-frame tie re-decision). An AO
   at authored priority ~4 removes the tie entirely.
2. **Hard animation swap, no ease-in/ease-out.** SL blends clips over
   `ease_in` / `ease_out` (`LLKeyframeMotion`); SLNG swaps instantly in
   `SetActiveAnimations`. The sim cycles STAND variants and injects short fidgets — each
   swap pops the hands. (Explains pops on a timer; less likely to explain a *constant*
   tremble.)
3. **No `LLHandMotion` / dedicated hand-pose state.** SL drives the finger bones via a
   separate hand-pose state machine, not keyframe data. If a partial clip keys only
   *some* hand bones and `ResetToRestPose` runs every frame for the rest, two clips that
   disagree on which finger bones they touch can alternate.
4. **FEAT-ANIM-01 interaction.** The predicted `ANIM_AGENT_STAND` is appended last in
   `desired` (wins ties by position). If its resolved clip's hand keys differ from the
   sim's echoed stand, and the substitution timing races the network set, the hands
   swap between the two stand poses.

## Acceptance Criteria

- [ ] Self avatar standing still, **no AO**, hands are steady — no per-frame wobble,
      over at least 60 s (covers the sim's ~30 s stand-variant cycle).
- [ ] Wearing / removing an AO does not change hand steadiness (both are steady).
- [ ] Remote avatar with no AO, standing: hands steady.
- [ ] No regression to FEAT-ANIM-01: walk cycle still starts on key-down, AO walk still
      overrides the built-in gait.
- [ ] Deterministic tie-break covered by a unit test on `AvatarAnimationPlayer`
      (two equal-priority clips keying one bone → stable winner across frames and across
      `_active` reorders).

## Technical Specs & Affected Files

- `app/scripts/AvatarAnimationPlayer.cs`
  - `ApplyBonePoses` — add a deterministic tie-break at equal priority (e.g. lower
    `AnimationId` wins, or "keep the existing winner" i.e. first-wins with stable
    `_active` order). Document the rule.
  - Consider a minimal ease-in/ease-out weight per `PlayingAnimation` (from
    `AnimationData` ease fields if present) so a clip swap cross-fades instead of pops.
- `app/scripts/AvatarRenderer.cs` — `ApplyActiveAnimations`: make `desired` ordering
  deterministic (sort the non-predicted ids) so tie resolution can't depend on sim wire
  order; keep the predicted gait's precedence explicit rather than positional.
- `src/SLNG.Assets/AnimationData.cs` / `AnimationDecodeService.cs` — check `ease_in` /
  `ease_out` / `hand_pose` fields are parsed from the SL binary anim; add if missing.
- `viewer-parity`: confirm against `scratch/slviewer` `LLKeyframeMotion::onUpdate`
  (weight ramp) and `LLHandMotion` — is the tremble the absence of a hand-pose state, or
  purely the missing blend weight? Decide before implementing.

## Sub-tasks / Progress

- [ ] Repro with `--diag`; capture `[AnimPlayer] +<id> pri= loop= len=` for the
      AO-less standing self and identify which clips key the hand bones and at what
      priority.
- [ ] `viewer-parity`: LLKeyframeMotion weight ramp + LLHandMotion — which one is
      missing here.
- [ ] Deterministic equal-priority tie-break + unit test.
- [ ] Optional: per-clip ease weight / cross-fade.
- [ ] In-world: steady hands with and without AO, self + remote; FEAT-ANIM-01 regression
      check.
