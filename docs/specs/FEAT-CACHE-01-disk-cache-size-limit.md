# [FEAT-CACHE-01] Disk Cache Size Limit and LRU Eviction

- **Feature ID:** `FEAT-CACHE-01`
- **Track:** `assets` | `ui`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Depends on:** [FEAT-PERF-11](FEAT-PERF-11-decoded-texture-cache.md), [FEAT-UI-79](FEAT-UI-79-preferences-rework.md)

## Overview & Goal

SLNG stores downloaded assets on disk in `user://cache/assets/`:
- Raw textures: `<textureId>_v5.j2c`
- Meshes: `<meshId>.mesh`
- Animations: `<animId>.anim`
- Materials: `<materialId>.mat`
- Decoded & block-compressed textures: `decoded/<textureId>_r*.dec` and `decoded/<textureId>_gpu_v1.dec` (via `DecodedTextureCache`, FEAT-PERF-11)
- Diagnostic artifacts: `*.baddecode`, `*.degraded`

Currently, **only the decoded texture cache** (`decoded/*.dec`) has an internal size cap (16 GB, `DecodedTextureCacheSizeMb`). The **raw asset cache** (`*.j2c`, `*.mesh`, `*.anim`, `*.mat`) has **no size limit and no eviction policy whatsoever**. Every asset ever downloaded stays on disk indefinitely.

Reported by the user on 2026-10-28:
> *"der Cache wird zu groß. Im Firestorm kann man den beschränken. In SLNG hat der aktuell 55GB. Bei FS ist aktuell 10GB eingestellt. Die Empfehlung liegt da bei 20GB. So eine Einstellung benötigen wir also auch."*

In comparison, Firestorm (and the Second Life reference viewer) allows users to configure a maximum disk cache size (typically 10 GB to 20 GB, with 20 GB / 20,480 MB being the modern recommendation). When the cache reaches the configured limit, an LRU (Least Recently Used) purge automatically removes older cached files.

### Goal
1. Add a user-facing **Cache Size Limit** setting in **Preferences → Network & cache → Cache** (Firestorm parity: recommended default 20 GB / 20,480 MB, configurable from 1 GB to 64 GB).
2. Implement a background **Disk Cache LRU Eviction Manager** in `SLNG.Assets` that bounds the total asset cache (`user://cache/assets/`, including raw assets and decoded textures) to the configured limit.
3. Automatically purge least recently accessed files down to a low watermark (e.g. 90% of the limit = 18 GB for a 20 GB limit) when the high watermark is reached, protecting assets active in the current session.

---

## Firestorm & Second Life Parity

In Firestorm / Linden Viewer:
- Setting: `CacheSize` (stored in megabytes).
- Recommended: 20,480 MB (20 GB). User-configurable range typically 1,024 MB to 65,536 MB (or up to free disk capacity).
- Location: Preferences → Network & Files → Connection → Cache size.
- Eviction Behavior:
  - When the total cache directory size exceeds `CacheSize`, oldest unaccessed files are pruned until usage drops below ~90% (low watermark) to prevent constant thrashing.
  - Active assets in memory or open file handles are spared.

---

## Technical Architecture & Design

### 1. Engine-Agnostic Core (`SLNG.Assets`)

Under `AGENTS.md` layering rules, `SLNG.Assets` must not reference Godot:
- Create `DiskCacheManager` (or integrate into `AssetService` / `DecodedTextureCache` coordinator):
  - Tracks total disk usage across `user://cache/assets/` (raw `.j2c`, `.mesh`, `.anim`, `.mat`, `.baddecode`, `.degraded`, and `decoded/*.dec`).
  - Scans file metadata (`FileInfo.Length`, `FileInfo.LastAccessTimeUtc` / `LastWriteTimeUtc`).
  - High watermark: 100% of configured `MaxCacheSizeBytes`.
  - Low watermark: 90% of configured `MaxCacheSizeBytes` (reclaims 10% headroom).
  - Eviction candidate ordering: ascending order by `LastAccessTimeUtc` (oldest accessed first).
  - Protected entries:
    - Files written or read within the active session (grace window, e.g. last 15–30 minutes).
    - In-flight fetch / decode targets.
  - Asynchronous background execution:
    - Eviction runs strictly on background thread pool workers (`Task.Run` / low-priority background timer).
    - Never blocks the Godot main thread or delays asset delivery to `GpuCache`.
    - Triggers:
      1. Initial check at startup / login.
      2. Periodic background sweep (e.g. every 5–10 minutes) or after a cumulative write threshold (e.g. every 256 MB of new raw assets written).

### 2. Coordination with `DecodedTextureCache`

`DecodedTextureCache` (FEAT-PERF-11) currently maintains its own 16 GB budget for `decoded/*.dec`:
- When a global cache limit is configured (e.g. 20 GB), `DecodedTextureCache` must not hoard 16 GB independently if raw assets also need space.
- The eviction manager treats the asset directory holistically:
  - Option A: Decoded cache files (`*.dec`) and raw asset files (`*.j2c`, `*.mesh`, etc.) compete fairly under the same LRU timestamp ordering. Since `.dec` files can be re-decoded from `.j2c`, `.dec` files of inactive textures are natural early eviction candidates.
  - Option B: Proportional split (e.g. up to 50% for decoded cache, 50% for raw assets).
  - Recommended: Single holistic LRU sweep with preference to prune `.dec` before deleting the underlying `.j2c` (deleting `.dec` frees space without losing the ability to reload offline; deleting `.j2c` requires re-downloading from the grid).

### 3. Preferences UI (`app/scripts/UI/`)

Following the `FEAT-UI-79` preferences rework:
- Category: **Network & cache**
- Section: **Cache**
- Existing rows:
  - Cache size readout & "Clear cache" button (`ui.preferences.clear_cache`)
  - Decoded texture cache toggle (`ui.preferences.decoded_cache_enabled`, Advanced)
- New row:
  - **Max Cache Size** (`ui.preferences.cache_size_limit`):
    - Control: `HSlider` with value display, or slider + spinbox.
    - Range: 1 GB (1,024 MB) to 64 GB (65,536 MB), step 1 GB (1,024 MB).
    - Default: 20 GB (20,480 MB).
    - Hint: `ui.preferences.cache_size_limit_hint` ("Recommended: 20 GB. Controls total disk cache for textures, meshes, and decoded assets (Firestorm parity).")
- Readout update:
  - Display current usage vs configured limit, e.g. `12.4 GB / 20.0 GB (Asset Cache: 11.8 GB, Object Cache: 600 MB)`.
- Storage:
  - Persisted in `preferences.cfg` under `[network]` as `cache_size_limit_mb` (default 20480).

---

## Acceptance Criteria

- [x] `NetworkSettings` has `CacheSizeLimitMb` (default `20480`, range `1024` to `65536`) persisted to `preferences.cfg [network]`.
- [x] Preferences window (Network & cache → Cache) displays a "Max Cache Size" slider with value readout and recommended hint (20 GB).
- [x] Cache size readout in Preferences reflects current total usage vs the configured limit.
- [x] `SLNG.Assets` implements disk cache size monitoring and LRU eviction across raw assets (`.j2c`, `.mesh`, `.anim`, `.mat`) and decoded cache files.
- [x] Eviction triggers when total asset cache exceeds 100% of the limit, pruning oldest unaccessed files down to 90% (low watermark).
- [x] Active/recent session assets are protected from eviction.
- [x] Eviction runs asynchronously in the background and never hitches or blocks the Godot main thread.
- [x] Layering rule respected: `SLNG.Assets` remains 100% engine-agnostic (no `using Godot;`).
- [x] Localization added to both `app/i18n/en-US.json` and `app/i18n/de-DE.json`.
- [x] `FEAT-UI-79-preferences-rework.md` table updated to include the new row under Network & cache › Cache.
- [x] Unit tests for `DiskCacheManager` (size calculation, LRU sorting, watermark eviction, active file protection).
- [x] Selftest / verify clean (`dotnet test`, `dotnet format`).

---

## Technical Specs & Affected Files

- `src/SLNG.Assets/Cache/DiskCacheManager.cs` (New)
- `src/SLNG.Assets/AssetService.cs` (Wire eviction manager, report file access)
- `src/SLNG.Assets/DecodedTextureCache.cs` (Coordinate budget with global limit)
- `tests/SLNG.Assets.Tests/DiskCacheManagerTests.cs` (New unit tests)
- `app/scripts/UI/NetworkSettings.cs` (Add `CacheSizeLimitMb`)
- `app/scripts/UI/NetworkPreferencesPage.cs` (Add max cache size slider & updated readout)
- `app/i18n/en-US.json` / `app/i18n/de-DE.json` (L10n strings)
- `docs/specs/FEAT-UI-79-preferences-rework.md` (Update settings table)
- `docs/ROADMAP.md` (Task entry)

---

## Sub-tasks / Progress

- [x] Create spec and roadmap entry
- [x] Implement `DiskCacheManager` in `SLNG.Assets` with LRU eviction algorithm
- [x] Integrate into `AssetService` and coordinate with `DecodedTextureCache`
- [x] Add `CacheSizeLimitMb` to `NetworkSettings` and `preferences.cfg`
- [x] Add UI slider to `NetworkPreferencesPage` and localize strings
- [x] Unit tests and verification
