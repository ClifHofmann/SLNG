# [BUG-NET-05/06] Texture-fetch and environment-poll spam, plus a regression found fixing it

- **Feature ID:** `BUG-NET-05` (texture-fetch 403 spam) / `BUG-NET-06` (environment-poll 503 spam)
- **Track:** `net`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/FEAT-SL-01-second-life-readiness`
- **Version:** `v0.20.0-alpha`

## Overview & Goal

Found live on Aditi (via HTTP Toolkit as a MITM proxy, in a parallel debugging session): the
console log showed two kinds of repeated warnings —
`EnvironmentSettings GET non-success: ServiceUnavailable` (dozens of times) and
`Failed to fetch texture <id> over HTTP: Forbidden` (repeated for the same texture id). Both are
real, unwanted load against the sim; this spec covers the original fix for both **and** a
regression that fix briefly introduced, found and corrected in the same session.

## Root causes

**BUG-NET-06 (environment 503 spam):** `RepollEnvironmentLoopAsync` re-polls the legacy
EnvironmentSettings capability on every `RegionInfo` packet. On Aditi that capability appears to
be a stub that always answers 503 (SL has fully moved to EEP/`ExtEnvironment`), and nothing was
distinguishing "the fetch failed, try again next RegionInfo" from "this will never succeed" — a
busy region sends `RegionInfo` often enough for this to become continuous.

**BUG-NET-05 (texture 403 spam):** `FetchTextureViaHttpRangeAsync` retries a failed texture fetch
up to `maxRetries` times (2 s apart) when the response is `ServiceUnavailable`, `NotFound`, **or**
`Forbidden`. A 403 is a deliberate, permanent access decision — retrying it can never succeed —
but it was marked retryable alongside two genuinely transient statuses, so one denied texture cost
up to 5 wasted attempts, each logged.

## The regression: fixing BUG-NET-05 by disabling `UseHttpTextures`

The first attempt at BUG-NET-05 set `_client.Settings.TexturePipeline.UseHttpTextures = false`.
This does not do what it looks like it does:

- `FetchTextureDataAsync` (SLNG's own world-object texture fetch, used by `AssetService`) never
  reads this flag at all — it always uses its own HTTP Range path, by design (see that method's
  own comment). So the flag change had **no effect** on the actual 403 spam, which came from this
  method's retry loop.
- What the flag **does** gate, verified in the pinned LibreMetaverse package: every avatar-bake
  texture fetch. `GridClientBakingTextureProvider.RequestTextureAsync` (LMV's
  `IBakingTextureProvider`, used throughout `AppearanceManager`) calls
  `Client.Assets.RequestImageAsync`, which calls `RequestImageInternal`, which chooses HTTP only
  `if (Settings.TexturePipeline.UseHttpTextures && ... GetTextureCapURI() != null)` — otherwise it
  falls through to the legacy UDP `Texture.RequestTexture` path. Setting the flag `false` forced
  **every bake texture fetch** onto that UDP path — which the very next line of the surrounding
  comment in `GridSession.cs` already documented as unreliable ("UDP transfers time out and hand
  back truncated JPEG2000 streams on busy grids... white untextured objects").

This was a real risk of making the already-reported blank-avatar problem (`BUG-AVATAR-01`) worse,
not better, while doing nothing for the texture it was meant to fix.

## Acceptance Criteria

- [x] `UseHttpTextures` restored to `true` — avatar-bake texture fetches use HTTP again.
- [x] `FetchTextureViaHttpRangeAsync` no longer retries a `Forbidden` response; `ServiceUnavailable`
      and `NotFound` remain retryable.
- [x] `RepollEnvironmentLoopAsync`'s 60-second backoff on a `ServiceUnavailable` environment
      response is kept — this part of the original fix was correct.
- [x] `dotnet format SLNG.sln` clean (the prior commit had accumulated whitespace violations in
      `GridSession.cs`, fixed alongside this change).
- [x] 541 tests still pass; no test coverage lost or needed changing (this is a status-code
      classification change and a flag flip, not new observable logic).

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `src/SLNG.Net/GridSession.cs` | `UseHttpTextures` reverted to `true` (comment explains why, and why it looked like a fix without being one); `FetchTextureViaHttpRangeAsync`'s retryable set drops `Forbidden`; incidental `dotnet format` cleanup of pre-existing whitespace violations |

### Design notes

- **A flag that "reduces log spam" needs to be traced to what it actually gates before trusting
  the correlation.** `UseHttpTextures` looked directly relevant to an HTTP-texture-fetch problem;
  it wasn't the code path producing the spam at all, and its real effect was on a completely
  different subsystem (avatar baking) that happened not to be under test at the time.
- **403 vs 503/404 is a meaningful distinction for retry logic, not just an HTTP-status detail.**
  A 5xx or a 404 can resolve on a later attempt (transient server trouble, an asset that hasn't
  propagated yet); a 403 is the server's final word for this requester and this resource. Treating
  them identically wastes both time (2 s × up to 5 attempts) and, more importantly, load against
  the grid — the kind of load TPV Policy §2.f asks a viewer not to impose.

## What the tests guarantee

Nothing new — this is a status-code classification change (retry vs. not) and a settings-flag
revert, neither of which SLNG.Net.Tests' local-OpenSim policy can exercise meaningfully (OpenSim
does not reproduce SL's specific 403-on-protected-content behaviour). Confidence comes from
tracing the exact code path both the flag and the status code go through, not a mock.

## Still open

- **Not yet re-verified in-world** that avatar bake fetches over HTTP succeed now that the flag
  is reverted, or that the 403 texture no longer gets retried.
- **`BUG-AVATAR-01`'s root cause is still open.** This spec removes one plausible aggravating
  factor (bake fetches forced onto UDP) but does not by itself explain the original blank avatar.

## ⚠️ Correction (same session, later)

Line 61's claim — "the 60-second backoff on a `ServiceUnavailable` environment response is kept —
this part of the original fix was correct" — **was wrong.** The backoff line was still in the
source, but it could never execute: `capture?.Error?.Contains("ServiceUnavailable")` reads a field
that `FetchRegionEnvironmentAsync` only ever populated from a caught **exception**, never from a
plain non-2xx HTTP response. LibreMetaverse's `EnvironmentManager.GetLegacyEnvironmentAsync` (and
its `ExtEnvironment` siblings) swallow the status code on a non-success response — `Logger.Warn`
and return `null` — so GridSession had no way to see that the failure was specifically a 503, and
the cooldown that depended on seeing that never fired. Confirmed live on Agni with HTTP Toolkit: a
sustained burst of "503 cap invocation rate exceeded" responses to the same `EnvironmentSettings`
cap URI, spaced by `RepollMinInterval` (2.5 s) forever, not the intended one-then-back-off-60s
pattern. This is what actually produced the repeated `EnvironmentSettings GET non-success:
ServiceUnavailable` warnings this spec originally described — the user independently reproduced
and correctly identified the underlying capability from a raw LLSD capture (its body decodes to
`ambient`/`blue_density`/`sun_angle`/`waterFogColor` — Windlight sky and water settings, not
EventQueueGet). See `BUG-NET-07` for the actual fix.
