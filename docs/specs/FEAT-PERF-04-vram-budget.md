# [FEAT-PERF-04] A VRAM budget that is actually a budget

- **Feature ID:** `FEAT-PERF-04`
- **Track:** `render` / `perf`
- **Status:** `🧪 Review` — implemented v0.21.11, not yet verified in-world
- **Owner:** `claude`
- **Depends on:** `FEAT-PERF-02` (texture LOD), `BUG-NET-11` (`TextureLod`, reduce-level decode)
- **Requested:** live, 2026-09-03 — *"plane mal ein, dass wir den VRAM begrenzen (setting)"*

## What is wrong today

`GpuCache` is constructed with a hardcoded budget (`Boot.cs:1764`, `1536 MB`) and **the budget does
not bind**. Measured live, steady state on a populated Agni region:

```
[GpuCache] entries=6488 pinned=6488 pinnedMB=2099 sizeMB=2099/1536
[Perf]     vramMB=5242
```

- **100 % of entries are un-evictable.** `EvictIfNeeded` only reclaims `RefCount <= 0`, and every
  entry has a live owner.
- The cache is **37 % over** its stated limit and simply keeps growing.
- Two settings the user already has (`draw_distance=176`, default 96) multiply it, and there is no
  ceiling anywhere.

**Why eviction alone cannot fix this, and why that is not a bug.** `ObjectRenderer.ReleaseResources`
*does* `ReleaseRef` every texture of an object that leaves the release radius — the machinery is
correct and it works. It just cannot help here: at 176 m draw distance the whole visible scene is
legitimately referenced, so there is nothing left to reclaim. A cache whose entire contents are in
use is not misbehaving; it is being asked to hold more than the budget allows. **Enforcement has to
act on what goes IN, not only on what comes out.**

Also worth stating plainly so the setting is not oversold: `GpuCache` holds **textures and meshes**
(`ObjectRenderer.cs:2414` puts meshes with `initialRefCount: 1`), and `vramMB=5242` is Godot's
*total* video memory — shadow maps, render targets, MSAA buffers and the sky are in there too. The
cache is roughly 40 % of that number. A texture/mesh budget cannot control the rest, and the setting
must not pretend otherwise.

## Design

### 1. The budget becomes a setting

`GraphicsSettings.TextureMemoryMb`, persisted in `preferences.cfg` next to `draw_distance` and
`msaa`, with a slider on the graphics page. `GpuCache` gains `SetBudget(long bytes)` so it applies
at runtime rather than only at construction — a VRAM slider that needs a restart is a slider people
set once, wrongly.

Default: keep 1536 MB. This feature is about making the number mean something, not about changing it.

### 2. Back-pressure on upload resolution — the part that makes it bind

When the cache is over budget and eviction reclaimed nothing, raise a global **LOD bias** that
`TextureLod.DiscardLevelFor` adds to its result. One extra discard level is 4× less VRAM per
texture, two is 16×.

`TextureLod.DiscardLevelFor` (added `v0.20.55`) is the single place both the decoder and the
renderer take that decision, so this is one hook, not a change scattered across call sites — and
because the decoder already honours the level, a raised bias also makes decoding *cheaper*, not just
smaller.

- **Hysteresis is required**, not optional: raise the bias above ~100 % of budget, lower it only
  below ~85 %. Without it the bias oscillates and the scene visibly pumps between sharp and soft.
- **Avatar and bake textures are exempt by construction** — they pass `screenPixelArea: 0`, which
  `DiscardLevelFor` already returns 0 for. Faces at conversation distance must not go soft because
  a parcel full of scenery filled the cache. Keep it that way, and say so in the code.

### 3. Shrink what is already resident

A bias only affects the next upload; the 6488 textures already in memory stay exactly as large.
`GpuCache` today documents the gap itself: *"The reverse does NOT happen; nothing ever
re-downsamples a texture once it has been sharpened"* (`GpuCache.cs:195`).

The mechanism already exists in the other direction. `TryUpgradeCachedTexture` re-decodes and pushes
a sharper image into the **same** `ImageTexture` via `SetImage`, so every material referencing it
picks the change up with no re-wiring. Shrinking is that same path with a larger discard level.

**Hazard, already paid for once:** `MainThreadWorkQueue.Pump` caps the `Refine` lane at 2 items per
frame with the comment *"to prevent Godot 4 Vulkan backend from crashing (Signal 11) when freeing
and recreating too many in-use ImageTextures"*. A shrink pass walks the LRU and must respect that
same cap. Shrink the largest, least-recently-used entries first, a couple per frame, until back
under the low-water mark.

## Not in scope

- Meshes. They are in the same cache and pinned at `Put`, but a mesh cannot be "downsampled" the way
  a texture can — that is LOD selection, a separate and much larger piece of work.
- Everything outside the cache (shadow maps, render targets, MSAA). If the goal is ever "cap total
  VRAM at N", those have to be part of it, and shadow resolution / MSAA are already their own
  settings.

## Acceptance

- `sizeMB` tracks at or below the configured budget in steady state on a dense region, instead of
  sitting 37 % over it.
- Moving the slider down visibly reduces `[GpuCache] sizeMB` within seconds, without a restart.
- No visible pumping between sharp and soft while standing still (hysteresis works).
- Avatar and bake textures are unaffected at any bias level.
- No `Signal 11` / RenderingServer crash during a sustained shrink pass.
- `[GpuCache]` reports the active bias so a "why is everything blurry" report is answerable from the
  log rather than by guessing.

## Implementation (v0.21.11)

| File | Change |
|---|---|
| `src/SLNG.Assets/TextureLod.cs` | `public static volatile int GlobalLodBias`; `DiscardLevelFor` adds it **after** the `screenPixelArea <= 0` guard (avatar/bake exempt) and re-clamps to `MaxDiscardLevel` |
| `app/scripts/GpuCache.cs` | `_maxSize` no longer readonly; `SetBudget(long)` (clamped ≥ 64 MB, runs `EvictIfNeeded`). New `Tick()` (per-frame, main thread): raises `GlobalLodBias` above budget / lowers below `LowWater` 0.85, with a 3 s cooldown, capped at `MaxLodBias` 2; while over budget, enqueues up to `ShrinkPerTick` 2 `ShrinkOne` jobs on the `Refine` lane. `ShrinkOne` reads the texture back, halves it, `SetImage`s it in place, fixes `entry.Size`/`_currentSize`, and marks it re-sharpenable only on a genuine close-up. `_noShrink` set fed by every `rejectDegraded`/`bakeChannel` caller. `[GpuCache]` line gained `lodBias=`. |
| `app/scripts/UI/GraphicsSettings.cs` | `TextureMemoryMb` (default 1536), persisted as `graphics/texture_memory_mb` |
| `app/scripts/UI/QualityPreferencesPage.cs` | "Texturspeicher / Texture memory" slider (256–6144 MB, step 128) under Draw distance |
| `app/scripts/Boot.cs` | `GpuCache` constructed from the setting; `ApplyGraphicsSettings` → `SetBudget` (live, no restart); `_Process` → `_gpuCache.Tick()` |
| `app/i18n/{de-DE,en-US}.json` | `ui.preferences.texture_memory_heading` |
| tests | `ReduceLevelDecodeTests`: bias adds levels for a real area, never for area 0, still clamps (3) |

### Deviations from the sketch

- **Shrink re-reads from the GPU (`ImageTexture.GetImage()` + `Resize`)**, it does not re-decode
  from `AssetService`. Simpler, needs no asset-service handle in `GpuCache`, and shrinking only
  discards data so a readback is enough. Re-sharpening still goes through the asset path
  (`TryUpgradeCachedTexture`).
- **`MaxLodBias` capped at 2**, not 3 — the shrink pass is the primary lever; the bias is
  secondary pressure and 3 levels (64x) on SL content reads as broken.
- **No detected-VRAM default** — kept the fixed 1536 MB (open question below).

## Open questions

- Should the default be derived from detected VRAM rather than a fixed 1536 MB? Godot exposes total
  video memory; a 12 GB card and a 4 GB card should probably not get the same default.
- Is a single global bias enough, or does it need to be per-category (world prims vs. terrain vs.
  HUD)? Start global; split only if measurement says so.
