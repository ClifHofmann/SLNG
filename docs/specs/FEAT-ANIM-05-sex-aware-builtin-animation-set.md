# [FEAT-ANIM-05] Sex-aware built-in animation set

- **Feature ID:** `FEAT-ANIM-05`
- **Track:** `render` (+ `core`)
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

A female-shaped avatar in SLNG plays the **male / neutral** built-in animations
(`ANIM_AGENT_WALK`, `ANIM_AGENT_STAND`, `ANIM_AGENT_SIT`, …). The reference viewer
remaps the built-in locomotion / stand / sit state to a sex-specific asset when the
avatar's sex is female — `LLVOAvatar::remapMotionID`, keyed on `getSex()`.

"Sex" here is **not** a separate setting — it is visual param **`male` (id 80, group0 index 31, 0 =
female, 1 = male)** carried by the worn **shape** wearable. SLNG already reads it
(`AvatarShapeService`, verified against `avatar_lad.xml`) and sex-gates every visual
param off it. This task extends that same signal to animation selection. No new UI, no
preference — it follows the worn shape, exactly like the reference viewer.

## What the reference viewer does (confirmed with `viewer-parity`)

Verified against `scratch/slviewer/indra/newview/llvoavatar.cpp:6230-6283` (`LLVOAvatar::remapMotionID`) and `llanimationstates.cpp`:

```cpp
LLUUID LLVOAvatar::remapMotionID(const LLUUID& id)
{
    static LLCachedControl<bool> use_new_walk_run(gSavedSettings, "UseNewWalkRun");
    LLUUID result = id;

    if (getSex() == SEX_FEMALE)
    {
        if (id == ANIM_AGENT_WALK)
            result = use_new_walk_run ? ANIM_AGENT_FEMALE_WALK_NEW : ANIM_AGENT_FEMALE_WALK;
        else if (id == ANIM_AGENT_RUN)
            result = ANIM_AGENT_FEMALE_RUN_NEW;
        else if (id == ANIM_AGENT_SIT)
            result = ANIM_AGENT_SIT_FEMALE;
    }
    else
    {
        if (id == ANIM_AGENT_WALK)
            result = use_new_walk_run ? ANIM_AGENT_WALK_NEW : ANIM_AGENT_WALK;
        else if (id == ANIM_AGENT_RUN)
            result = use_new_walk_run ? ANIM_AGENT_RUN_NEW : ANIM_AGENT_RUN;
        else if (id == ANIM_AGENT_SIT_FEMALE)
            result = ANIM_AGENT_SIT;
    }
    return result;
}
```

Findings:
1. **Female stand:** `llanimationstates.cpp` has NO female-specific stands; all avatars use the standard 5 stands (`ANIM_AGENT_STAND`, `ANIM_AGENT_STAND_1..4`).
2. **Wire behavior for remotes & sit:** The simulator sends the generic/neutral IDs (`ANIM_AGENT_WALK`, `ANIM_AGENT_RUN`, `ANIM_AGENT_SIT`) on the wire for both sexes. The client remaps incoming motion IDs via `remapMotionID` on both local agent and remote avatars in `startMotion` / `stopMotion`.

## Scope

- **Self avatar (primary):** `SelfLocomotion.Predict` gets an `isMale` input (derived from the worn shape's param 80 / group0 index 31) and returns `FemaleWalk` / `FemaleRun` for female avatars.
- **Remote avatars & sit:** In `AvatarRenderer.ApplyActiveAnimations`, animations are remapped via `SelfLocomotion.RemapForSex(id, avatar.IsMale)`.
- **Asset fallback:** If a sex-specific asset is missing on a grid, `AvatarRenderer.LoadAndStartAnimationsAsync` falls back to the neutral equivalent via `SelfLocomotion.GetNeutralFallback(animId)`.

## Acceptance Criteria

- [x] Wearing a female shape (`male == 0`): the self avatar walks with `FEMALE_WALK`,
      runs with `FEMALE_RUN`, sits with `SIT_FEMALE`.
- [x] Wearing a male shape (`male == 1`): unchanged from today (neutral / male ids, male
      stand set).
- [x] Switching shape (female ↔ male) at runtime flips the animation set without a
      relog, on the next state change.
- [x] The FEMALE_RUN / female-stand ids resolve as assets on SL **and** OpenSim (they
      are built-ins; if a grid lacks one, fall back to the neutral id rather than
      T-pose — graceful fallback implemented in `AvatarRenderer`).
- [x] Remote avatars: remapped using each remote avatar's shape sex in `ApplyActiveAnimations`.
- [x] Unit tests on `SelfLocomotion.Predict` / `RemapForSex`: same `LocomotionState`
      with `isMale = true` vs `false` returns the expected pair.
- [x] No regression: FEAT-ANIM-01 prediction + boost, BUG-ANIM-01/02, FEAT-ANIM-03/04.

## Sub-tasks / Progress

- [x] `viewer-parity`: `LLVOAvatar::remapMotionID` exact set confirmed; observers remap incoming motion IDs for all avatars.
- [x] `SelfLocomotion`: female UUID constants + `RemapForSex` + `GetNeutralFallback` + `Predict(isMale)`.
- [x] Plumb `IsMale` from the worn shape to `AvatarController` for the self avatar.
- [x] Prefetch / strip-substitute both sets; grid-missing-asset fallback to neutral.
- [x] Remote remap in `ApplyActiveAnimations`.
- [x] Unit tests + regression testing.
- [x] Verified build (`dotnet build SLNG.sln`, `dotnet build app/SLNG.App.csproj`, `dotnet test`, 42/42 self-tests).
