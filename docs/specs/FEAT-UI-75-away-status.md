# [FEAT-UI-75] Away, Do Not Disturb and automatic IM responses

- **Feature ID:** `FEAT-UI-75`
- **Track:** `ui` / `net` / `core`
- **Status:** 🧪 Review
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md)

## Overview & Goal
Requested 2026-10-27: an online status with an away message, "the way Firestorm does it". SLNG had none: no
Away flag, no Do Not Disturb, and a first IM from a stranger was never answered.

## What Firestorm does (source: phoenix-firestorm `llimprocessing.cpp`, `llagent.cpp`, `settings_per_account.xml`)
- **Avatar menu** has four checks: Away, Do Not Disturb, Autorespond, Autorespond to non-friends.
- **Away** = `AGENT_CONTROL_AWAY` on every `AgentUpdate` plus the away animation (`LLAgent::setAFK`). Goes on by itself after
  `AFKTimeout` (default 300 s) and when the window loses focus while that timeout is on; any input more than
  `MIN_AFK_TIME` (10 s) later clears it (`LLViewerWindow::handleKey/handleMouseMove`).
- **Answers** go out for the message that **opens** a 1:1 conversation only (`!gIMMgr->hasSession`), only for a live
  message (`offline == IM_ONLINE`), never to a muted sender, as dialog `IM_DO_NOT_DISTURB_AUTO_RESPONSE` (20). Text by
  priority: Do Not Disturb, non-friends, autorespond, Away (only if `FSSendAwayAvatarResponse`).
- Texts are per account (`DoNotDisturbModeResponse`, `FSAutorespondModeResponse`, `FSAutorespondNonFriendsResponse`,
  `FSAwayAvatarResponse`); the two autorespond modes persist (`FSAutorespondMode`, `FSAutorespondNonFriendsMode`).

## Built
- `SLNG.Core.Presence`: `PresenceConfig` (flags + texts), `AutoresponsePolicy` (which text), `ImAutoresponder` (first
  message of a conversation only), `AwayTracker` (idle timeout, 10 s grace). 22 tests.
- `GridSession.Presence`: sets the Away flag/animation reliably, answers via the same packing as any session line, raises
  `AutoresponseSent`; another viewer's `BusyAutoResponse` is now shown instead of dropped (and never answered). 3 tests.
- App: Avatar menu entries, a readout in the top bar (click = available again), `PresenceController` (idle, focus, input),
  `AutoresponsePreferencesPage`, a Preferences tab "Away & Auto-response" (timeout, four texts, "answer while Away"), the sent answer appears in the chat tab, `PresenceSettings`
  in `preferences.cfg`. Selftest "autoresponse preferences page".

## Differences from Firestorm
- Settings are global, not per account.
- No inventory item attached to an answer (`FSAutoresponseItemUUID`), no per-friend overrides (contact sets), no
  "answer muted avatars" option, no "sit on away".
- Do Not Disturb only answers; the reference viewer also silences notifications and declines offers. Not done.
- Away and Do Not Disturb do not survive a restart (Firestorm keeps Do Not Disturb across sessions); the autorespond modes do.
- No "(Away)" tag on our own nametag yet.

## Not yet confirmed in-world
Needs a second avatar: DND answer to a first IM, no second answer, away flag visible to the other (nametag, away
animation), auto-away after the timeout, return on input.
