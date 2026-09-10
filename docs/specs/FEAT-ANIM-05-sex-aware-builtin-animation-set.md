# [FEAT-ANIM-05] Sex-aware built-in animation set

- **Feature ID:** `FEAT-ANIM-05`
- **Track:** `render` (+ `core`)
- **Status:** `⏸️ Pending`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

A female-shaped avatar in SLNG plays the **male / neutral** built-in animations
(`ANIM_AGENT_WALK`, `ANIM_AGENT_STAND`, `ANIM_AGENT_SIT`, …). The reference viewer
remaps the built-in locomotion / stand / sit state to a sex-specific asset when the
avatar's sex is female — `LLVOAvatar::remapMotionID`, keyed on `getSex()`.

"Sex" here is **not** a separate setting — it is visual param **`male` (id 31, 0 =
female, 1 = male)** carried by the worn **shape** wearable. SLNG already reads it
(`AvatarShapeService`, verified against `avatar_lad.xml`) and sex-gates every visual
param off it. This task extends that same signal to animation selection. No new UI, no
preference — it follows the worn shape, exactly like the reference viewer.

## What the reference viewer does (confirm with `viewer-parity` before implementing)

`LLVOAvatar::remapMotionID(id)` — rough shape, exact set to be pinned against
`indra/newview/llvoavatar.cpp` + `indra/llcommon/llanimationstates.cpp`:

- **Female sex:**
  - `ANIM_AGENT_WALK` → `ANIM_AGENT_FEMALE_WALK`
  - `ANIM_AGENT_RUN` → `ANIM_AGENT_FEMALE_RUN`
  - `ANIM_AGENT_SIT` → `ANIM_AGENT_SIT_FEMALE`
  - `ANIM_AGENT_STAND` → a female stand from the stand set
- **Male sex:**
  - `ANIM_AGENT_STAND` → one of `ANIM_AGENT_STAND_1..4`
  - `ANIM_AGENT_SIT` → generic / male sit
- Stand cycling ("next stand" every ~N s) also runs through this remap.

Note: LibreMetaverse's `Animations` table only has `FEMALE_WALK` and `SIT_FEMALE`
constants — `FEMALE_RUN` and the female stand ids are **not** in it. SLNG must carry its
own UUID constants for the missing ones, sourced from `llanimationstates.cpp`, added
alongside the existing ids in `SelfLocomotion`.

## Scope

- **Self avatar (primary):** `SelfLocomotion.Predict` (FEAT-ANIM-01) currently returns
  fixed neutral ids. It gets an `isMale` input (derived from the worn shape's param 31)
  and returns the remapped id.
- **Remote avatars:** `viewer-parity` question — does the agent's own viewer send the
  base state (each observer remaps using the target's sex) or the already-remapped id?
  If observers must remap, `AvatarRenderer.ApplyActiveAnimations` does the same remap for
  a remote avatar using **that** avatar's sex (SLNG already has each avatar's appearance
  / shape params). If the sim/agent already sends the remapped id, remote needs nothing.

## Acceptance Criteria

- [ ] Wearing a female shape (`male == 0`): the self avatar walks with `FEMALE_WALK`,
      runs with `FEMALE_RUN`, sits with `SIT_FEMALE`, and uses a female stand — verified
      against Firestorm side-by-side with the same shape.
- [ ] Wearing a male shape (`male == 1`): unchanged from today (neutral / male ids, male
      stand set).
- [ ] Switching shape (female ↔ male) at runtime flips the animation set without a
      relog, on the next state change.
- [ ] The FEMALE_RUN / female-stand ids resolve as assets on SL **and** OpenSim (they
      are built-ins; if a grid lacks one, fall back to the neutral id rather than
      T-pose — same pattern as the FEAT-ANIM-01 `res://` fallback note).
- [ ] Remote avatars: per the `viewer-parity` finding — either they already render
      correctly (no change) or they get the sex-aware remap and a female-shaped remote
      avatar walks female for the observer.
- [ ] Unit tests on `SelfLocomotion.Predict` / the remap function: same `LocomotionState`
      with `isMale = true` vs `false` returns the expected pair.
- [ ] No regression: FEAT-ANIM-01 prediction + boost, BUG-ANIM-01/02, FEAT-ANIM-03/04.

## Technical Specs & Affected Files

- `src/SLNG.Core/Avatars/SelfLocomotion.cs`
  - add the missing UUID constants (`FemaleWalk`, `FemaleRun`, female stand id(s),
    `SitFemale`) from `llanimationstates.cpp`.
  - `Predict(in LocomotionState s, bool isMale)` — or a separate
    `Guid RemapForSex(Guid neutralId, bool isMale)` applied to the `Predict` result and
    to the sit/stand ids. Keep `All` / `Prefetch` covering both sets.
- `src/SLNG.Core/Components/AvatarComponent.cs` / appearance plumbing — expose a
  resolved `IsMale` (from shape param 31) per avatar if not already surfaced; the self
  avatar's value feeds `AvatarController` → `SetSelfPredictedLocomotion`.
- `app/scripts/AvatarController.cs` — pass `isMale` into the `SelfLocomotion.Predict`
  call at `AvatarController.cs:750`.
- `app/scripts/AvatarRenderer.cs`
  - `Prefetch` list / `SelfLocomotion.All` strip-and-substitute logic must account for
    both sex sets.
  - remote remap in `ApplyActiveAnimations` **iff** `viewer-parity` says observers remap.
- `viewer-parity`: pin `remapMotionID` exact mapping + the send-side question for
  remotes, before coding.

## Sub-tasks / Progress

- [ ] `viewer-parity`: `LLVOAvatar::remapMotionID` exact set; do observers remap remote
      avatars or is the id already sex-specific on the wire.
- [ ] `SelfLocomotion`: female UUID constants + `RemapForSex` + `Predict(isMale)`.
- [ ] Plumb `IsMale` from the worn shape to `AvatarController` for the self avatar.
- [ ] Prefetch / strip-substitute both sets; grid-missing-asset fallback to neutral.
- [ ] Remote remap (conditional on the parity finding).
- [ ] Unit tests + FEAT-ANIM-01/03/04 + BUG-ANIM-01/02 regression.
- [ ] In-world: female shape vs Firestorm (walk/run/stand/sit), male shape unchanged,
      runtime shape swap.
