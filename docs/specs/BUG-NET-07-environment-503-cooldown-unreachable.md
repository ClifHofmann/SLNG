# [BUG-NET-07] Environment 503 backoff was dead code — status swallowed by LibreMetaverse

- **Feature ID:** `BUG-NET-07`
- **Track:** `net`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/FEAT-SL-01-second-life-readiness`
- **Version:** `v0.20.1-alpha`

## Overview & Goal

Follow-up to `BUG-NET-06`. The user re-tested on Agni with HTTP Toolkit as a MITM proxy and still
saw a sustained burst of HTTP 503 responses, one every couple of seconds, all `cap invocation rate
exceeded` for the same `/cap/<uuid>` capability URI, with a `Retry-After: 4` header. The user first
read this as "Windlight stuff" and was asked to confirm; a captured 200-OK body for the identical
cap UUID settled it beyond doubt — the LLSD decodes to `ambient`, `blue_density`, `sun_angle`,
`waterFogColor`, `wave1Dir`, all keys `EnvironmentLlsdParser.cs` itself already parses from the
legacy `EnvironmentSettings` capability. So: correct call, the cap really is Windlight/EEP, and the
prior write-up in `BUG-NET-05-06` was wrong to call this fixed.

## Root cause

`BUG-NET-06`'s fix added a cooldown to `RepollEnvironmentLoopAsync`: on a `ServiceUnavailable`
capture, wait 60 seconds before trying again instead of retrying at the loop's normal
`RepollMinInterval` (2.5 s). The check is `capture?.Error?.Contains("ServiceUnavailable") == true`.

That check can never be true. `FetchRegionEnvironmentAsync` calls three LibreMetaverse wrapper
methods — `EnvironmentManager.GetRegionEnvironmentAsync`, `GetParcelEnvironmentAsync`,
`GetLegacyEnvironmentAsync` — and all three handle a non-2xx HTTP response identically:
`Logger.Warn($"... non-success: {response.StatusCode}", Client)` and `return null`. The status code
exists for exactly one statement, inside the wrapper, then is gone. `FetchRegionEnvironmentAsync`
only ever populates its own `error` string from a **caught exception**
(`catch (Exception ex) { error += $"[Exception: {ex.Message}]"; }`); a plain non-throwing 503
response leaves `error` untouched — `capture.Error` stays `null`. So the cooldown branch this
session's earlier fix added was reachable in source but never in practice, and the loop kept
re-requesting the same capability every `RepollMinInterval`, walking straight back into the sim's
own rate limiter every time — which is exactly the `EnvironmentSettings GET non-success:
ServiceUnavailable` warning already logged (dozens of times) and now, with HTTP Toolkit in the
path, visible as a literal HTTP capture instead of an inferred cause.

This is not a LibreMetaverse defect to report upstream — the pinned package
(`libremetaverse` 3.1.3) already ships its own client-side rate limiter
(`LibreMetaverse.CapsRateLimiter` / `RateLimitingCapsHandler`, confirmed present in the pinned
assembly by reflection, not just the newer `scratch/libremetaverse_src` checkout) wrapping every
`HttpCapsClient` request. That is a *generic* per-category token bucket; it has no way to know
SLNG's own `RepollEnvironment` logic is re-asking for the same capability on every `RegionInfo`
packet at a fixed 2.5 s cadence regardless of whether the last attempt succeeded, and it does not
by itself prevent a client from re-triggering the *server's* rate limit. The gap is entirely in
GridSession's own error-signal plumbing losing the one bit of information (which HTTP status came
back) it needed to behave correctly. **No LibreMetaverse version change was needed or attempted**;
3.1.4 was checked (`gh api repos/cinderblocks/libremetaverse/tags`, nuspec diff) purely to rule out
"maybe a newer version added the missing rate limiter" — it didn't need to, 3.1.3 already has one.

## Acceptance Criteria

- [x] `FetchRegionEnvironmentAsync` sees the real `HttpStatusCode` for all three environment
      capability fetches (region EEP, parcel EEP, legacy Windlight), not just "null or not".
- [x] A non-success status is recorded into `capture.Error` so
      `RepollEnvironmentLoopAsync`'s existing `Contains("ServiceUnavailable")` cooldown actually
      fires on a real 503.
- [x] No extra HTTP request added — the fix reads the status from the *same* GET that already
      happens, not an additional probe.
- [x] `dotnet build SLNG.sln`, `dotnet build app/SLNG.App.csproj`, `dotnet test` (563/563),
      `dotnet format SLNG.sln` clean, shader-globals clean, `--selftest` 26/26.

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `src/SLNG.Net/GridSession.cs` | New private `GetCapabilityMapAsync(Uri, CancellationToken)` — calls `_client.HttpCapsClient.GetAsync` directly and returns `(OSDMap? Map, HttpStatusCode? Status)`, bypassing the three LMV wrapper methods that discard the status. `FetchRegionEnvironmentAsync`'s three fetch sites (region `ExtEnvironment`, parcel `ExtEnvironment`, legacy `EnvironmentSettings`) rebuilt on top of it, each appending `[... HTTP <status>]` to `error` on a non-success response. `ExtEnvironmentMessage`/`LegacyEnvironmentMessage` (both public LibreMetaverse message types, already used by value before, now constructed directly) parse the returned `OSDMap` exactly as the bypassed wrapper did. |

### Design notes

- **Bypass the wrapper, don't wrap the wrapper.** The alternative — catching the `Logger.Warn`
  output, or wrapping LMV's call in a `try` hoping the status leaks out some other way — doesn't
  work; the status genuinely never leaves the wrapper method. `HttpCapsClient` (the thing that
  actually holds the response) is a public property on `GridClient`
  (`Client?.HttpCapsClient` — the exact same object LMV's own wrapper uses), so calling it
  directly costs nothing extra: same client, same rate-limiting handler, same one HTTP request,
  just without LMV's wrapper discarding the one field this fix needs.
- **Reuses LMV's own message types for parsing.** `ExtEnvironmentMessage.Deserialize` and
  `LegacyEnvironmentMessage.Deserialize` are public; calling them directly on the `OSDMap` this
  method already has to fetch keeps the actual LLSD-shape knowledge in the one place that already
  had it (LibreMetaverse), rather than duplicating it in SLNG.
- **`RepollEnvironmentLoopAsync` itself is untouched.** Its cooldown logic was correct in
  intent — it just never received a true input. This is a "the sensor was disconnected" fix, not a
  "the logic was wrong" fix.

## What the tests guarantee

Nothing new is unit-testable here in the way `tests-rules` scopes things — this is specifically
about a real HTTP 503 from a real Second Life simulator's capability rate limiter, which local
OpenSim does not reproduce (no EEP/legacy-Windlight capability rate limiting exists there to test
against) and which would require faking an `HttpMessageHandler` inside `GridSession`'s internals to
simulate without a network round-trip. The full 563-test suite continues to pass unmodified,
confirming the change is a status-plumbing fix with no change to any code path a passing region
capture (the only shape the current tests construct) goes through.

## Still open

- **Not yet re-verified in-world.** The fix makes the existing 60-second cooldown reachable; it has
  not yet been watched actually kick in against a live 503 burst on Agni.
- **The 60-second flat cooldown ignores the server's own `Retry-After` header** (the captured 503
  carried `Retry-After: 4`). Honoring it precisely would need `capture.Error` (a plain
  `SLNG.Core` string, by design — see `RegionEnvironmentCapture`'s doc comment on why the LLSD
  stays a string across that layer boundary) to carry a duration, not just a status name. Left as
  a documented simplification: 60 s is a conservative superset of "wait as long as the server
  asked", not a tighter fit to it, and is enough to stop the sustained storm.
- A second, unrelated finding from the same screenshots — a 404 `Hash mismatch` on the
  `bake-texture` "head" channel, and a `70d4f143-...` UUID mentioned in chat that does not appear
  in any local log or screenshot from this thread — is **not** covered by this spec; see
  `HANDOVER.md` for where that stands.
