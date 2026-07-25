# [FEAT-PERF-02] Texture Loading Speed

- **Feature ID:** `FEAT-PERF-02`
- **Track:** `assets` / `net` / `render`
- **Status:** `🚧 In Progress`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Speed up how fast textures visually populate a scene. This is a separate concern from
[FEAT-PERF-01](file:///E:/Git/SLNG/docs/ROADMAP.md) (login-to-usable network/world-model
latency, driven by a missing `AgentThrottle` send) — this task is about the texture
fetch/decode/GPU-upload pipeline itself, once object/interest-list data is already arriving.

Current-state audit (see file:line references below) found:

1. **No texture LOD/progressive fetch.** `GridSession.FetchTextureDataAsync` always requests
   `discardLevel: 0, progressive: false` — every texture is fetched at full resolution
   regardless of the object's on-screen size or distance. Mesh LOD exists
   (`AssetService.GetPrimMeshAsync` / `MeshDetailLevel`); texture LOD does not.
2. **GPU-side texture build is not deduplicated, only fetch/decode is.**
   `GetOrCreateGpuTextureAsync` (triplicated in `ObjectRenderer`/`AvatarRenderer`/
   `TerrainRenderer`) only checks `GpuCache` *before* calling `AssetService`; if N faces/objects
   miss the cache for the same never-before-seen texture at once, all N independently run
   `Image.CreateFromData` + `GenerateMipmaps()` before only one wins the `GpuCache.Put` race.
3. **`ConcurrentDictionary.GetOrAdd`'s factory isn't strictly single-execution** under a true
   first-touch race (`AssetService.cs`, several call sites) — concurrent misses can each start
   a real fetch/decode before the dictionary settles on one winner.
4. **Sculpt maps share the same 4-slot fetch throttle as decorative textures**, even though a
   sculpt map blocks the object's *shape*, not just its appearance.
5. Secondary/hygiene items (lower priority for raw speed): unbounded disk cache growth, soft
   256MB RAM-cache limit, compounding worst-case retry/timeout latency, shared
   mesh+texture GPU budget with no sub-split.

## Acceptance Criteria

**Phase 1 (do first, low risk):** ✅ done
- [x] `AssetService` fetch+decode in-flight dedup is a true single-execution guarantee (e.g.
      `Lazy<Task<T>>`), not just single-*storage*.
- [x] GPU-texture-upload path (`GetOrCreateGpuTextureAsync` equivalent) is centralized into one
      shared implementation used by `ObjectRenderer`, `AvatarRenderer`, and `TerrainRenderer`,
      and the CPU-side `Image`+mipmap build is deduplicated across concurrent callers for the
      same texture id (not just the network fetch).
- [x] Sculpt-map fetches use a separate throttle from decorative-texture fetches.
- [x] `dotnet build` + `dotnet test` clean; no behavior regression in existing texture/material
      tests.

**Phase 2 (needs protocol verification before implementing):**
- [x] `protocol-re` verified J2K discard-level semantics and the real viewer's distance/screen-
      size formula against actual OpenSim + `secondlife/viewer` source (`gh api`, not general
      knowledge). Headline finding: SLNG's texture fetch was always going through UDP regardless
      of `UseHttpTextures`; LibreMetaverse's own HTTP path ignores discard/priority/range entirely
      (always full download), while UDP *and* a hand-built HTTP Range request both make the
      SIMULATOR send fewer bytes for a higher discard level (~4x fewer bytes per level, confirmed
      against OpenSim's `GetTextureHandler`/`J2KImage` source) — this is real bandwidth savings,
      not just a client-side decode/display hint.
- [x] Distance/size-driven discard **implemented, then live-tested and found to cause a serious
      regression, then disabled** (`ObjectRenderer.ComputeTextureLod` computes a discard level but
      forces it to 0 / full resolution right before returning — one-line re-enable once fixed).
      One test session: 6752 of 6786 total client-output.log lines (99.5%) were
      `[WARNING]: Codestream truncated in tile 0` from CoreJ2K. Magick.NET (the primary decoder)
      does not tolerate a deliberately-Range-truncated J2C stream the way the design assumed,
      falls back to CoreJ2K en masse, and at the more aggressive discard levels that fallback
      frequently fails outright instead of degrading gracefully. Reported live as three symptoms
      tracing to this one cause: a wall of startup warnings, slower loading (CPU burned on
      repeated failed/fallback decodes instead of one clean full fetch), and ~80% of textures
      never appearing (vs. ~100% in Firestorm on the same region). **Root cause of the decode-side
      intolerance is not yet understood** — the byte-size math is verified against the real
      viewer's own `calcDataSizeJ2C`/OpenSim's `GetTextureHandler` source (Phase 2.1), so either
      the estimate is wrong in practice, or Magick.NET/CoreJ2K need different handling for a
      known-partial stream than for an accidentally-truncated one. Needs its own investigation
      before re-enabling, not attempted under live-testing pressure.
      **Fetch order is priority-aware regardless of the above, and unaffected by it:**
      `PriorityGate` (`src/SLNG.Assets/PriorityGate.cs`) replaced the plain `SemaphoreSlim`
      throttles — admits the highest-priority (most on-screen-prominent) queued texture next
      instead of strict arrival order, so what the camera is pointed at resolves before background
      scenery that merely happened to be requested first. Priority is computed the same way
      discard was going to be (apparent size from the active **camera**, not the avatar — a
      separate user-reported gap after live-testing: zooming/orbiting away from your own body kept
      prioritizing detail around it instead of what's actually on screen) and is captured at
      enqueue time, not re-evaluated if the camera later moves.
      **Also found and fixed while investigating:** `ObjectRenderer`'s 5 texture-fetch
      continuations failed completely silently (no log at all) — unlike `AvatarRenderer`'s
      existing `[FaceTex] ... fetch/decode returned null` log. Added matching failure logging.
      **Not yet covered:** `AvatarRenderer` (own-avatar bake should likely stay full-res like
      sculpts; other avatars' worn attachments are a real candidate), `TerrainRenderer` (detail
      textures are shared/tiled across a whole region, no single "distance to the texture") — both
      moot until discard is actually re-enabled.
      **No runtime upgrade path:** once a texture is GPU-resident it stays that way for the
      session even if later needed sharper (documented limitation, see
      `GpuCache.GetOrUploadTextureAsync`'s doc comment) — relevant again once discard is
      re-enabled, deferred until then.
      **Negative cache added:** genuinely dead/missing texture assets (confirmed via one session:
      5 distinct ids, 4 of them retried 173-408 times each) now get a 45s cooldown after
      exhausting all retry attempts instead of immediately re-entering the full retry cycle every
      time something asks for them again — this is independent of the discard-level regression
      and stays enabled.
- [ ] Re-evaluate the 4-concurrent-fetch cap (`a14229d`, originally a UDP-packet-drop fix) now
      that HTTP CAPS texture fetch is preferred; tune upward only with `protocol-re` sign-off
      that the original truncation risk doesn't reapply over HTTP.
- [ ] Baseline before/after comparison (using `tools/SLNG.StartupBaseline` or a real client run)
      showing measurable improvement in time-to-textures-visible for a representative scene.

## Technical Specs & Affected Files

- `src/SLNG.Assets/AssetService.cs` — fetch/decode dedup, throttle split, disk/RAM cache
- `src/SLNG.Net/GridSession.cs` — `FetchTextureDataAsync` priority/discard-level (Phase 2)
- `app/scripts/ObjectRenderer.cs`, `AvatarRenderer.cs`, `TerrainRenderer.cs` — GPU-upload path
  centralization
- `app/scripts/GpuCache.cs` — no change expected in Phase 1; Phase 2 may touch budget split

## Sub-tasks / Progress

- [x] Phase 1.1 — Single-flight fetch+decode dedup (`Lazy<Task<T>>`)
- [x] Phase 1.2 — Centralize + dedup GPU-upload path across the three renderers
- [x] Phase 1.3 — Separate sculpt-map fetch throttle from decorative-texture throttle
- [x] Phase 2.1 — `protocol-re`/`viewer-parity` verification of discard-level/priority semantics
- [x] Phase 2.2 — Implement distance/screen-size-driven texture LOD (`ObjectRenderer` only —
      see acceptance criteria above for what's deferred)
- [ ] Phase 2.3 — Re-evaluate concurrent-fetch cap now that HTTP CAPS is preferred
- [ ] Phase 2.4 — Before/after baseline comparison
