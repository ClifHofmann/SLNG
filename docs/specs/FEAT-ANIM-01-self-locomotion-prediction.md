# [FEAT-ANIM-01] Client-side locomotion animation for the self avatar

- **Feature ID:** `FEAT-ANIM-01`
- **Track:** `render` / `net`
- **Status:** `⏸️ Pending`
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
