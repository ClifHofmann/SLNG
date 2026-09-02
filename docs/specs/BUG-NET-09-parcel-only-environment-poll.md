# [BUG-NET-09] Parcel-only Windlight edits have no reachable push — poll for them instead

- **Feature ID:** `BUG-NET-09`
- **Track:** `net`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/FEAT-SL-01-second-life-readiness`
- **Version:** `v0.20.3-alpha`

## Overview & Goal

Follow-up question from the user after `BUG-NET-07`/`BUG-NET-08`: "was passiert wenn jemand das WL
auf der sim wechselt" (what happens when someone changes the Windlight on the sim). Answer split in
two, verified against the real reference viewer's own source (`llenvironment.cpp`,
`llviewerparcelmgr.cpp`):

1. **A region-wide change** (an estate/region owner edits the whole region's environment) is
   already handled live, and unaffected by today's other fixes — it arrives as a `RegionInfo`
   packet, which `GridSession` already turns into a throttled re-poll (`OnRegionInfoPacket` →
   `RepollEnvironment()`), matching exactly how the real viewer wires
   `LLRegionInfoModel`'s update callback to `LLEnvironment::requestRegion()`.
2. **A parcel-only change** (someone edits just the parcel the agent is standing on, not the whole
   region) had **no equivalent in SLNG at all**, and — this is the part worth a spec — cannot be
   made to work the way the real viewer does it, because the signal it depends on is not reachable
   through the pinned LibreMetaverse package's public API. This spec is the workaround.

## Root cause (why a real push isn't possible here)

The real viewer detects a parcel-only edit from a `ParcelEnvironmentVersion` integer inside an
unsolicited `ParcelProperties` push (`llviewerparcelmgr.cpp`: `parcel_environment_version` is
compared against the parcel's previously known value; a mismatch calls
`LLEnvironment::requestParcel`).

LibreMetaverse cannot supply this signal:

- `ParcelManager` registers `ParcelPropertiesReplyHandler` via
  `Client.Network.RegisterEventCallback("ParcelProperties", ...)` — the EventQueue/capability
  message channel. That handler receives an already-typed `ParcelPropertiesMessage`, not the raw
  LLSD.
- `ParcelPropertiesMessage` (in `LindenMessages.cs`) never parses a `ParcelEnvironmentVersion`
  field out of the incoming map at all — the property does not exist on the class.
- The one API shape that would have exposed the raw `OSDMap` instead of a typed message —
  `Caps.EventQueueCallback` as `(string message, OSD body, Simulator simulator)` — exists in the
  pinned source only as a **commented-out** delegate signature; the active one is
  `(string capsKey, IMessage message, Simulator simulator)`.

So the field is discarded before it ever reaches consumer code, and there is no lower-level public
hook to intercept the raw message first. `libremetaverse` is a compiled NuGet dependency here, not
vendored source SLNG builds from, so this cannot be patched locally either. A real, immediate push
for this specific case is not achievable without an upstream LibreMetaverse change.

## The workaround

Poll. Specifically: **reuse the exact same throttled re-poll path `BUG-NET-07`/`BUG-NET-08` just
finished making well-behaved**, adding one more trigger source alongside `RegionInfo` packets — a
periodic timer. `RepollEnvironment()` already does everything a new poll trigger needs for free:
single-flight (concurrent triggers collapse to one fetch), a minimum 2.5s gap
(`RepollMinInterval`), and "publish nothing unless the payload changed" (the existing fingerprint
check). Critically, `FetchRegionEnvironmentAsync`'s `hasExt` branch already re-resolves the agent's
*current* parcel and re-fetches *that parcel's* EEP settings on every single call, unconditionally
— that was already true before this session, just never triggered by anything except a login or a
`RegionInfo` packet. A periodic tick calling the same `RepollEnvironment()` entry point is exactly
enough to catch a parcel-only edit within one poll interval, using a call shape that already
exists and is already correctly throttled.

## Acceptance Criteria

- [x] A periodic loop (`ParcelEnvironmentPollLoopAsync`, `PeriodicTimer`) calls `RepollEnvironment()`
      every `ParcelEnvironmentPollInterval`, started in the constructor and cancelled in `Dispose`.
- [x] No new HTTP request shape — the loop only triggers the *existing* throttled repoll path; a
      tick that finds nothing changed costs exactly what a `RegionInfo`-triggered repoll already
      costs, and a tick during an active cooldown (from `BUG-NET-07`) is absorbed by the same
      single-flight/min-interval logic, not doubled.
- [x] Interval is a single named constant, deliberately short (30s) for the first live
      verification round, per the user: *"lass uns mal auf 30 sekunden gehen und wir gehen dann
      runter"* (let's start at 30 seconds and then we'll dial it back). Meant to be relaxed once
      confirmed working — see "Still open".
- [x] `dotnet build SLNG.sln`, `dotnet build app/SLNG.App.csproj`, `dotnet test` (563/563),
      `dotnet format SLNG.sln` clean, shader-globals clean, `--selftest` 26/26.

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `src/SLNG.Net/GridSession.cs` | New `_parcelEnvironmentPollCts` (session-lifetime `CancellationTokenSource`), `ParcelEnvironmentPollInterval` constant, `ParcelEnvironmentPollLoopAsync` (a `PeriodicTimer` loop that calls the existing `RepollEnvironment()`). Started at the end of the constructor; cancelled and disposed at the top of `Dispose()`. |

### Design notes

- **Deliberately not a new fetch method.** Everything this needs — parcel resolution, EEP fetch,
  change detection, publish — already exists in `FetchRegionEnvironmentAsync` and
  `RepollEnvironmentLoopAsync`. Adding a *second* trigger for an *existing, already-correct* path
  is much lower risk than writing new fetch/dedup logic that would have to independently prove it
  doesn't reintroduce the kind of spam `BUG-NET-07`/`BUG-NET-08` just fixed.
- **The interval is explicitly a starting point, not a considered final value.** 30s is tight
  enough to make the very next test session's verification fast (an edit made and observed inside
  half a minute) at the acceptable cost of more background traffic than a stationary session
  usually needs. The doc comment on `ParcelEnvironmentPollInterval` says so directly and names it
  as the one place to change.
- **This is the honest limit of what's achievable without touching LibreMetaverse itself.** A real
  push would need `ParcelPropertiesMessage.Deserialize` to parse `ParcelEnvironmentVersion` and a
  public way to react to it — both upstream changes. Worth keeping in mind if this project ever
  vendors or forks LibreMetaverse instead of consuming it as a NuGet package.

## What the tests guarantee

Nothing new is meaningfully unit-testable — this is a timer wired to an already-tested code path
(`RepollEnvironment`'s throttling and dedup are pre-existing, unit-testable behaviour this change
does not alter). The full 563-test suite passing unmodified confirms the new loop introduces no
regression to anything it touches.

## Still open

- **Not yet re-verified in-world** — specifically, this needs an actual parcel-only Windlight edit
  made by a second party while SLNG is connected and stationary on that parcel, to confirm the
  change becomes visible within ~30s.
- **The interval should come down once verified**, per the user's own plan. 30s is a testing value,
  not a considered steady-state one — a session standing still on one parcel for an hour would
  otherwise generate ~120 mostly-wasted HTTP GETs against the sim's `ExtEnvironment` capability for
  a case (someone else editing your current parcel while you watch) that is genuinely rare.
