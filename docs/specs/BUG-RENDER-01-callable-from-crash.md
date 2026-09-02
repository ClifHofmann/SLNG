# [BUG-RENDER-01] Fatal crash: `Callable.From(lambda).CallDeferred()` off the main thread

- **Feature ID:** `BUG-RENDER-01`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/FEAT-SL-01-second-life-readiness` (found and fixed mid-session, same branch)
- **Version:** `v0.20.0-alpha`

## Overview & Goal

Reported live, mid-session, while testing on Aditi (the SL beta grid): the client crashed with a
fatal, unhandled `System.AccessViolationException` inside
`godotsharp_callable_call_deferred`, taking the whole process down. The reported stack traced
through `GpuCache.FetchAndUploadTextureAsync` → `Godot.Callable.CallDeferred`.

This is [[godot-callable-from-not-threadsafe]] (a trap this project has hit and documented
before): `Callable.From(lambda).CallDeferred()` is **not** safe to call off the Godot main
thread — only `Node.CallDeferred(nameof(...))` or a buffer-and-drain queue are. `GpuCache` is a
plain C# class (not a `Node`), so it had no `Node.CallDeferred` to fall back to, and used the
unsafe form directly. `FetchAndUploadTextureAsync` is reached from asset-decode worker threads
via `GetOrUploadTextureAsync`, never the main thread — every call was a live crash waiting for
enough texture traffic to trigger it, which a real SL region supplies far more of than the local
OpenSim test grid this project develops against day to day.

## Scope: this was not an isolated bug

Once the pattern was found, a full-repo audit (`grep -rn "Callable.From(" app/scripts/*.cs`)
turned up the same anti-pattern at **13 more call sites** across `AvatarRenderer.cs` (7) and
`ObjectRenderer.cs` (6) — every one of them a post-`await` continuation on a background thread,
building avatar bakes, rigged-mesh materials, HUD geometry/materials, avatar animations, legacy
material normal/specular maps, glTF PBR textures, and the legacy default-face texture path. Only
one had actually crashed by the time this was reported; the other 13 were the same fuse,
unlit.

**The fix already existed in the codebase and was simply never applied everywhere.**
`MainThreadWorkQueue` (`app/scripts/MainThreadWorkQueue.cs`) was built for exactly this — a
budgeted, thread-safe queue drained once per frame on the main thread by `MainThreadWorkPump` —
and `GpuCache.TryUpgradeCachedTexture` (a few dozen lines above the crashing method, in the same
file) already used it correctly. The other 14 sites were never migrated.

## Acceptance Criteria

- [x] `GpuCache.FetchAndUploadTextureAsync` no longer calls `Callable.From(...).CallDeferred()`.
- [x] All 13 additional sites in `AvatarRenderer.cs` / `ObjectRenderer.cs` converted to
      `MainThreadWorkQueue.Enqueue(...)`.
- [x] `grep -rn "Callable.From(" app/scripts/*.cs` shows only the pre-existing safe cases: a
      comment, `Boot.cs`'s `_Ready`-time (main-thread) self-test call, and
      `MainThreadWorkQueue`'s own internal fallback (only reachable before `MainThreadWorkPump`
      exists, i.e. before any session/asset pipeline can be running).
- [x] Both projects build clean; 522 tests still pass (this bug had no test coverage of its own
      to add — it is a threading-safety fix with no observable-from-a-unit-test behaviour change,
      see "What the tests guarantee").

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `app/scripts/GpuCache.cs` | `FetchAndUploadTextureAsync`'s upload callback: `Callable.From(...).CallDeferred()` → `MainThreadWorkQueue.Enqueue(Lane.Visual, ..., label: "texture.upload")` |
| `app/scripts/AvatarRenderer.cs` | 7 sites converted: bake apply, rigged-mesh skin, static-mesh attach, per-surface face material, HUD mesh geometry, HUD per-surface material, animation apply |
| `app/scripts/ObjectRenderer.cs` | 6 sites converted: legacy normal map, legacy specular map, glTF PBR baseColor/normal/metallicRoughness/emissive (nested in `.ContinueWith`), legacy default-face texture |

### Design notes

- **Lane.Visual, not Lane.Refine, for all of these.** Every converted site is either a first
  upload/build (the user is waiting to see it) or a bake/animation apply (already-visible state
  changing), not the "already visible, make it sharper" case `Lane.Refine` exists for — matching
  `MainThreadWorkQueue`'s own documented lane semantics.
- **No behaviour change beyond thread safety.** Each edit only swaps the dispatch mechanism
  (`Callable.From(...).CallDeferred()` → `MainThreadWorkQueue.Enqueue(...)`); every lambda body
  is untouched, byte-for-byte. `MainThreadWorkQueue.Enqueue` is safe to call from ANY thread
  (that is its whole purpose), so converting a call site that happened to already be on the main
  thread is a no-op in practice — worst case one extra frame of latency — which is why every site
  found in the audit was converted regardless of whether its specific race window was ever
  observed to crash.
- **Applied via line-indexed scripted edits, not the Edit tool**, because several of the affected
  blocks share identical opening/closing text (`Godot.Callable.From(() => {` /
  `}).CallDeferred();`) and are not otherwise unique enough for a string-anchored diff. Each edit
  asserted the exact expected line content before writing, so a mismatch would abort loudly
  rather than silently corrupt a neighbouring block.

## What the tests guarantee

Nothing new — this is a pure thread-safety fix with no new observable behaviour to pin in a
unit test (the crash requires the real Godot engine's threading model, which the `SLNG.*.Tests`
projects don't run against). Confidence comes from: the fixed call site now matches an
already-working, adjacent pattern in the same file (`GpuCache.TryUpgradeCachedTexture`); every
other converted site follows the identical mechanical transform; and both projects build clean
with all 522 existing tests still green. The real test is the same one that found the bug —
sustained texture/mesh traffic on a live SL region.

## Still open

- **Not yet re-verified in-world.** The crash was reported once; this fix has not yet been
  confirmed to survive the same conditions that produced it (heavy texture traffic on Aditi).
- **`MainThreadWorkQueue.Enqueue`'s own startup fallback** (`!_pumpActive` branch) still uses raw
  `Callable.From(...).CallDeferred()`. Left alone: it is only reachable before
  `MainThreadWorkPump` exists in the tree, which is before `Boot._Ready` has gotten far enough to
  create a session or asset pipeline — nothing that could call `Enqueue` from a background thread
  exists yet at that point. Noted here so it isn't mistaken for an oversight.
