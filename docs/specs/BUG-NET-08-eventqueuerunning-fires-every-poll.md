# [BUG-NET-08] `EventQueueRunning` fires on every poll, not once — the actual "every second" cause

- **Feature ID:** `BUG-NET-08`
- **Track:** `net`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/FEAT-SL-01-second-life-readiness`
- **Version:** `v0.20.2-alpha`

## Overview & Goal

Immediately after `BUG-NET-07` shipped, the user re-tested on Agni: **"die 503 kommt immernoch im
sekundentakt"** (the 503 is still coming once a second). `BUG-NET-07` was a real fix — it made
`RepollEnvironmentLoopAsync`'s 60-second cooldown reachable — but that loop is throttled to at most
one fetch per `RepollMinInterval` (2.5 s) regardless, so it could never have produced a once-a-
second cadence even before that fix. Something else was the dominant traffic source. This spec is
that something else.

## Root cause

`GridSession.OnEventQueueRunning` — subscribed to LibreMetaverse's `NetworkManager.EventQueueRunning`
event — calls `FetchRegionEnvironmentAsync()` (which always includes the legacy `EnvironmentSettings`
fetch) with **no throttle, no dedup, no "already done" guard**. Its own doc comment describes it as
firing "once its capabilities are live," and the event's name (`EventQueueRunning`) reads like a
one-time "the long-poll started" signal.

It is not one-time. Tracing where LibreMetaverse raises it:

- `Caps.EventQueueConnectedHandler()` calls `Simulator.Client.Network.RaiseConnectedEvent(Simulator)`.
- That is wired to `EventQueueClient.OnConnected`, invoked from `ConnectedResponseHandler`.
- `ConnectedResponseHandler` runs after **every** successful `EventQueueGet` POST inside the
  client's polling loop (`Create()`'s `ack()` closure), not just the first one — despite its own
  source comment claiming otherwise ("the event queue is starting up for the first time"). There is
  no first-time gate anywhere in `EventQueueClient.cs`; every successful long-poll response raises
  `EventQueueRunning` again, unconditionally.

On a live SL region, `EventQueueGet` typically resolves roughly once a second under normal event
traffic (avatar updates, chat, etc. flowing through it). Every one of those resolutions re-fired
`OnEventQueueRunning`, and every firing spawned a brand new `FetchRegionEnvironmentAsync()` —
including the legacy Windlight GET — via its own unguarded `Task.Run`, completely bypassing
`RepollEnvironmentLoopAsync`'s throttle (a different call site with its own, separate cooldown that
this path never goes through). That is what was actually producing a request once a second, and
walking straight into the sim's own capability rate limiter every time. `BUG-NET-07` fixed a real
dead branch in the *other* call site; this was the dominant source of the traffic the whole time,
on both sides of that fix.

## Acceptance Criteria

- [x] `OnEventQueueRunning`'s environment capture (and the appearance-readiness log call sitting
      next to it) runs at most once per live `Simulator` instance, matching what its own doc
      comment already claimed was happening.
- [x] Legitimate live environment changes are unaffected — those already flow through
      `RepollEnvironmentLoopAsync` via `OnRegionInfoPacket`, a completely separate, already-
      throttled path untouched by this fix.
- [x] No LibreMetaverse behavior depended upon here that isn't already true elsewhere in this
      file — `Simulator` instances are per-connection (a fresh connect after a real disconnect gets
      a new instance; `BUG-NET-04`'s `OnSimChanged` handling relies on the same lifecycle).
- [x] `dotnet build SLNG.sln`, `dotnet build app/SLNG.App.csproj`, `dotnet test` (563/563),
      `dotnet format SLNG.sln` clean, shader-globals clean, `--selftest` 26/26.

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `src/SLNG.Net/GridSession.cs` | New `private Simulator? _environmentCapturedForSim;`. `OnEventQueueRunning` gains `if (ReferenceEquals(e.Simulator, _environmentCapturedForSim)) return;` right after the existing `CurrentSim` check, and sets the field before doing any work. |

### Design notes

- **Guard on the `Simulator` object, not the region handle.** A handle-based guard would also
  incorrectly suppress a genuine second visit to the same region after leaving and coming back
  (same handle, new connection). LibreMetaverse hands out a new `Simulator` instance per connection
  (confirmed by the existing `AddSimulator`/`RemoveSimulator` lifecycle this file already depends
  on for `BUG-NET-04`), so reference equality is "same live connection," which is exactly the
  scope this needs to be once-per.
- **This is a guard on SLNG's side, not a LibreMetaverse fix.** `EventQueueClient`'s comment
  ("starting up for the first time") suggests the *intent* upstream was a one-shot signal too —
  this looks like a genuine LMV bug, not a documented "fires repeatedly by design" behavior — but
  nothing in SLNG can safely rely on an upstream fix landing, so the guard belongs here regardless
  of whether it's also worth reporting to `cinderblocks/libremetaverse`.
- **`LogAppearanceEditReadiness` already had its own separate one-shot latch**
  (`_appearanceReadinessLogged`, process-lifetime), so it was not itself spamming — but it was still
  being *called* once a second for no purpose, which this fix also stops as a side effect of gating
  the whole handler body.

## What the tests guarantee

Nothing new is unit-testable here for the same reason as `BUG-NET-07` — this is about the actual
firing cadence of a real LibreMetaverse event against a real `EventQueueGet` long-poll, which local
OpenSim's much lower event volume would not reliably reproduce even if a test tried. The full
563-test suite passes unmodified, confirming no other code path depends on `OnEventQueueRunning`
firing more than once per simulator.

## Still open

- **Not yet re-verified in-world.** This is the fix that should actually stop the "im Sekundentakt"
  pattern the user is currently seeing; it has not yet been watched doing so live.
- Combined with `BUG-NET-07`, both known causes of the environment-capability spam are now
  addressed: the loop that re-polls on `RegionInfo` can now back off correctly on a real 503
  (`BUG-NET-07`), and the call site that was firing far more often than intended now fires once
  (`BUG-NET-08`). If a 503 burst is still observed after both, the next suspect is
  `ResolveAgentParcelIdAsync`'s own `RequestParcelProperties` call inside the still-unthrottled
  `hasExt` branch of `FetchRegionEnvironmentAsync`, or a source outside `GridSession` entirely
  (not yet searched).
