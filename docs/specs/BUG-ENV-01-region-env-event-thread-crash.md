# Bug: BUG-ENV-01 — Region-environment event crashes the client on login

- **Feature ID:** `BUG-ENV-01`
- **Track:** `render` / `net`
- **Status:** `✅ Done` — confirmed in-world 2026-08-27
- **Owner:** `claude`

## Symptom
Hard crash a few seconds after login (process terminates, not catchable):

```
Fatal error. System.AccessViolationException: Attempted to read or write protected memory.
   at Godot.NativeInterop.NativeFuncs.godotsharp_callable_call_deferred(...)
   at Godot.Callable.CallDeferred(Godot.Variant[])
   at Boot.<OnLoginPressed>b__103_3(System.Object, SLNG.Core.RegionEnvironmentEvent)
   at SLNG.Net.GridSession+<<OnEventQueueRunning>b__105_0>d.MoveNext()
   ...
   at SLNG.Net.GridSession+<FetchRegionEnvironmentAsync>d__108.MoveNext()
   at LibreMetaverse.EnvironmentManager+<GetParcelEnvironmentAsync>d__30.MoveNext()
   at LibreMetaverse.HttpCapsClient+<GetAsync>d__7.MoveNext()
   ... System.Net socket read on a threadpool IO-completion thread
```

## Root cause
`Boot.OnLoginPressed` wired two `GridSession` events with
`Godot.Callable.From(() => …).CallDeferred()`:

- `RegionConnected` → `Callable.From(() => RenderConfig.SetRegionOrigin(handle)).CallDeferred()`
- `RegionEnvironmentReceived` → `Callable.From(() => _environmentDriver.SetCycle(…)).CallDeferred()`

Both events are raised off the main thread — `RegionConnected` on a LibreMetaverse network
thread, `RegionEnvironmentReceived` from `GridSession.OnEventQueueRunning`'s
`Task.Run(async … ConfigureAwait(false))`, whose continuation resumes on a **.NET threadpool
IO-completion thread** after the CAPS `GetParcelEnvironmentAsync` HTTP await.

A **custom (delegate-backed) `Callable`** — what `Callable.From(Action)` produces — routes its
deferred dispatch through the Godot↔.NET custom-callable bridge, which is **main-thread-only**.
Invoking `.CallDeferred()` on it from a foreign thread corrupts memory →
`AccessViolationException` inside `godotsharp_callable_call_deferred`. (`GodotObject.CallDeferred`
on a real native object with Variant args is genuinely thread-safe — it only locks the
MessageQueue — which is why every other cross-thread handler in `Boot` that uses
`CallDeferred(nameof(Method), …)` is fine.)

Latent since FEAT-ENV-01 Phase D (`v0.7.0-alpha`); surfaced reliably once the environment fetch
started resuming on a threadpool thread.

## Fix (`app/scripts/Boot.cs`)
- `RegionConnected` → `CallDeferred(nameof(ApplyRegionOrigin), regionHandle.ToString())`; new
  `ApplyRegionOrigin(string)` parses it back and calls `RenderConfig.SetRegionOrigin`. The
  handle travels as a string because a region handle can exceed `long.MaxValue` and `ulong`
  isn't a Variant-safe `CallDeferred` argument (same reasoning as the existing `Guid`→string
  IM handler).
- `RegionEnvironmentReceived` → the handler just parks the event in a
  `RegionEnvironmentEvent? _pendingRegionEnvironment` field (payload is `DayCycle` /
  `EnvironmentSource`, neither Variant-safe). `_Process` drains it once per frame via
  `Interlocked.Exchange` and calls `_environmentDriver.SetCycle` on the main thread — exactly
  AGENTS.md's "buffer incoming events, drain once per frame" rule.

## Acceptance criteria
- [x] No `Godot.Callable.From(lambda).CallDeferred()` is invoked from a non-main thread.
- [x] `dotnet build app/SLNG.App.csproj` clean; `--selftest` 24/24.
- [x] Confirmed in-world 2026-08-27 — login + region crossing with no `AccessViolationException`;
      environment still applies.
