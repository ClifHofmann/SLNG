# [FEAT-ANIM-01] Client-side locomotion animation for the self avatar

- **Feature ID:** `FEAT-ANIM-01`
- **Track:** `render` / `net`
- **Status:** `🧪 Review` — implemented v0.21.8, not yet verified in-world
- **Owner:** `claude`
- **Reported:** live, 2026-09-03. *"Rumlaufen → Animation zum Laufen kommt nicht / zu spät wenn es
  etwas laggt."*

## Problem

The self avatar's animations are driven **entirely** by `avatar.ActiveAnimations`
(`AvatarRenderer.UpdateVisual` step 4), which is populated only from the network
`AvatarAnimation` event. There is no local prediction. So when the user starts walking:

1. `AvatarController` moves the avatar locally (position is responsive).
2. `AgentUpdate` movement flags go to the sim.
3. The **sim** decides "this agent is walking" and broadcasts `AvatarAnimation` with the walk id
   — network RTT + sim tick.
4. `LoadAndStartAnimationsAsync` → `GetAnimationAsync(walkId)` → **a live asset fetch + decode**
   the first time that anim is needed this session (built-in locomotion anims are fetched as grid
   assets by UUID like any other — `AssetService.FetchAndDecodeAnimationAsync`).
5. `SetActiveAnimations` on the main thread.

Under any lag, 3 and 4 both add latency, so the avatar **slides forward in the stand pose** until
the echo arrives.

The reference viewer plays the built-in locomotion motions **locally and immediately** from
`LLAgent`'s own movement state (`gAgent.getVelocity()`, walk/run/fly flags →
`LLVOAvatarSelf::startMotion(ANIM_AGENT_WALK)` etc.), compiled-in, no fetch, and separately tells
the server. It never waits for its own echo.

## Scope

1. **Bundle / pre-fetch the built-in locomotion anims.** Warm `AssetService`'s animation cache at
   login for the standard set so step 4 is never a live fetch: `ANIM_AGENT_STAND`,
   `WALK`, `RUN`, `TURNLEFT`, `TURNRIGHT`, `FLY`, `FLYSLOW`, `HOVER`, `HOVER_UP`, `HOVER_DOWN`,
   `FALLDOWN`, `PREJUMP`, `JUMP`, `LAND`, `CROUCH`, `CROUCHWALK`, `SIT`, `SIT_GROUND`,
   `STANDUP` (UUIDs from `llanimationstates.cpp`). ~19 small BVH assets, one-time. If some don't
   resolve as assets on a given grid, ship a bundled `res://` copy.
2. **Local locomotion state → anim, for the self avatar only.** `AvatarController` already has
   velocity, `_flying`, grounded-ness, sit state and the control inputs. Derive the desired
   locomotion anim each frame (stand / walk / run / turn-L / turn-R / fly / hover / fall /
   prejump / jump / crouch / crouchwalk) and drive it into the self `AvatarVisual` immediately.
3. **Merge, don't fight, the server set.** Many users wear an AO whose walk/stand/etc. arrive via
   `AvatarAnimation` (the AO script calls `llStartAnimation`, server-routed). Rule: the local
   prediction only *fills a gap* — if the server's `ActiveAnimations` already carries a
   locomotion-category anim (built-in or AO), that wins and the prediction stands down for that
   category. So the sequence under lag is "instant built-in walk → AO walk takes over when it
   arrives", never "stand-slide → walk".
4. Keep it self-only. Remote avatars have no local movement state and must stay echo-driven.

## Acceptance

- Start walking with artificial lag / a busy sim: the walk cycle starts within a frame or two,
  not after the round-trip.
- An AO still overrides the built-in gait once its anims arrive; no permanent double-play or
  fighting.
- Turning in place plays the turn animation locally.
- No regression to remote-avatar animation, sit/stand, or worn non-locomotion animations.
- Tests for the locomotion-state → anim-id decision (pure function, `AvatarController` or a small
  helper in `SLNG.Core`).

## Implementation (v0.21.8)

| File | Change |
|---|---|
| `src/SLNG.Core/Avatars/SelfLocomotion.cs` (new) | `LocomotionState` record struct + pure `Predict(in LocomotionState) → Guid?`; the 18 built-in `ANIM_AGENT_*` UUIDs as `Guid` constants (from `llanimationstates.cpp`); `All` (the ids the renderer strips from the sim echo — no `SIT`/`SIT_GROUND`/`STANDUP`) and `Prefetch` (cache-warm set) |
| `tests/SLNG.Core.Tests/SelfLocomotionTests.cs` (new) | 17 tests pinning the decision (stand / walk vs run / turn / fly / hover-up-down / fall / crouch / sitting → null / every emitted id ∈ `All`) |
| `app/scripts/AvatarController.cs` | builds a `LocomotionState` from **input keys** (`isFwd/isBack/isLeft/isRight/isDown`), `_flying`, `isSitting` and a new `_grounded` flag (set by the ground clamp), plus network velocity for walk-vs-run / hover-up-down only; pushes `SelfLocomotion.Predict(...)` to the renderer every frame |
| `app/scripts/AvatarRenderer.cs` | `SetSelfPredictedLocomotion(Guid?)`; `ApplyActiveAnimations` (extracted from step 4) — for the self **with a non-null prediction**, strip `SelfLocomotion.All` from `avatar.ActiveAnimations` and append the prediction; remote avatars and the sitting self are unchanged (sim set authoritative). Prefetches `SelfLocomotion.Prefetch` on first self-visual creation. Tracks `_selfEntityId` (cleared in `RemoveVisual`) |
| `app/scripts/Boot.cs` | `AppVersion` v0.21.7 → v0.21.8 |

### Notes / follow-ups

- **Merge rule is priority-based, not gap-based.** The prediction always emits a built-in gait;
  a custom AO walk is not a `SelfLocomotion.All` id so it stays in the set and wins per bone via
  its authored priority — exactly what the reference viewer does (`gAgent` plays the built-in
  gait locally, the AO overrides it). Only the sim's echo of the *built-in* ids is stripped, so
  there is no built-in-vs-built-in fight.
- **No `res://` bundled fallback yet.** The prefetch warms the cache from grid assets; the 18
  ids are standard SL library animations present on SL and OSGrid. If one fails to resolve on a
  given grid the prediction still emits it and `LoadAndStartAnimationsAsync` no-ops that entry
  (same as today) — a bundled copy is a follow-up if a grid is found missing one.
- **Jump / prejump not predicted.** Needs a jump-input edge `AvatarController` doesn't expose;
  `FallDown` covers airborne. Follow-up.
- **Run is speed-thresholded** (`SpeedHoriz > 4.6 m/s`), not tied to an always-run / fast flag —
  `AvatarController` has no run input. Good enough for the reported problem; revisit if a run
  toggle lands.
