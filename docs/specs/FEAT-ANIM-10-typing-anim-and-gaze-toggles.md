# [FEAT-ANIM-10] Typing-animation toggle + "eyes/head follow camera" toggle

- **Feature ID:** `FEAT-ANIM-10`
- **Track:** `net` (+ `render`, `ui`)
- **Status:** `⏸️ Pending`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Two viewer-controlled avatar behaviours reference viewers let you switch off — needed for
photos, roleplay and comfort:

1. **Typing animation** — the hands-on-keyboard pose (`ANIM_AGENT_TYPE`) other people see
   while you type in local chat.
2. **Head / eyes follow the camera** — the self avatar's head turning to look where the
   camera / mouse points. Wanted off for portraits (neutral forward gaze) and for people
   who find the constant head-swivel distracting.

## Part 1 — Typing animation

**Current state:** SLNG does **not** play `ANIM_AGENT_TYPE` at all — nothing starts it,
and the incoming `StartTyping` / `StopTyping` chat-indicator events are filtered out
(`GridSession.cs:1235`). So today other residents never see SLNG typing.

**Work:** add the parity behaviour *and its off switch in one go*:
- When the local-chat input has focus and the user is typing, start `ANIM_AGENT_TYPE`
  and send `ChatType.StartTyping`; stop both on blur / send / idle timeout.
- Gate the whole thing behind a preference **"Tipp-Animation zeigen"** (`preferences.cfg`,
  default **on**). Off = never start the animation and never send `StartTyping`.
- The typing animation is also suppressed by FEAT-ANIM-07 (T-pose) and FEAT-ANIM-09
  (freeze) like any other clip.

## Part 2 — Head / eyes follow camera

**Current state:** `GridSession.SetMovement` sends `Self.Movement.HeadRotation = slQuat`
every `AgentUpdate`, where `slQuat` is the **camera** rotation (`GridSession.cs:7844`).
That is what other viewers render as "head follows the look direction". There is no
procedural eye/head look-at on SLNG's own rendered skeleton yet, and no LookAt
`ViewerEffect` is sent.

**Work:**
- Preference **"Kopf folgt der Kamera"** (`preferences.cfg`, default **on**).
- Off → send `HeadRotation = BodyRotation` (head aligned with body, forward gaze)
  instead of the camera quat. `BodyRotation` still tracks movement/turn as today.
- If/when SLNG adds local procedural head/eye look-at rendering, the same flag gates it.
- If a LookAt/attention `ViewerEffect` send is ever added (paired with FEAT-RENDER-11's
  receive side), this flag also suppresses the mouse-driven `lookat_*` targets.
- Mouselook (first person) is unaffected — the body faces the view there by design.

## Acceptance Criteria

- [ ] With "Tipp-Animation zeigen" on: a second client sees SLNG play the typing pose
      and the "…" indicator while the user types, both stopping shortly after.
- [ ] With it off: no typing pose, no `StartTyping` sent, ever.
- [ ] With "Kopf folgt der Kamera" on: unchanged from today (head tracks the camera for
      observers).
- [ ] With it off: a second client sees the self avatar's head stay aligned with the
      body while the SLNG user swings the camera around in third person.
- [ ] Both settings persist across a relog and are reachable from Preferences; quick
      toggles in the Avatar / Snapshot menu.
- [ ] `--selftest` locale parity stays green; `AppVersion` bumped.
- [ ] No regression: movement/turn, mouselook, FEAT-ANIM-01/07/09.

## Technical Specs & Affected Files

- `src/SLNG.Net/GridSession.cs`
  - `SetMovement`: `Self.Movement.HeadRotation = _headFollowsCamera ? slQuat : Self.Movement.BodyRotation;`
  - typing: new `StartTyping()` / `StopTyping()` wrappers (`Self.AnimationStart(Animations.TYPE…)`
    + `Self.Chat("", 0, ChatType.StartTyping/StopTyping)`), no-ops when the preference is off.
  - a neutral setter for each flag (bool), no LMV type in the signature.
- `app/scripts/` chat input control — call `StartTyping`/`StopTyping` on focus/keypress/
  blur/send with a short idle debounce.
- `app/scripts/AvatarController.cs` — pass the head-follow flag through to `SetMovement`
  (or read it in `GridSession`).
- Preferences UI + `preferences.cfg` keys (same `ConfigFile` pattern as `CameraSettings`);
  locale strings `en-US` + `de-DE`.
- `app/scripts/Boot.cs` — `AppVersion` bump.

## Sub-tasks / Progress

- [ ] `GridSession` head-follow flag + `HeadRotation` branch.
- [ ] `GridSession` typing start/stop wrappers gated on the preference.
- [ ] Chat-input wiring for typing start/stop + idle debounce.
- [ ] Preferences rows + `preferences.cfg` + locale strings.
- [ ] `AppVersion` bump.
- [ ] In-world with a second client: typing on/off, head-follow on/off in third person,
      mouselook unaffected.
