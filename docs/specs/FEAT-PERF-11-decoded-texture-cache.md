# [FEAT-PERF-11] Decoded-Texture Disk Cache

- **Feature ID:** `FEAT-PERF-11`
- **Track:** `assets` | `render` | `perf`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
SLNG caches raw texture codestreams on disk (`user://cache/assets/<uuid>_v5.j2c`). While this eliminates HTTP downloads on revisits, every teleport or region revisit pays a JPEG-2000 decode of ~93 ms per texture again across the worker pool.
On a typical busy region (~4,300 distinct textures, 4,800 requests):
- ~450 seconds of CPU decode time on the thread pool.
- ~30 seconds of wall-clock delay during which textures visibly trickle in.
- Severe ThreadPool contention and global locking in CoreJ2K (`lock (_coreJ2kLogLock)`).

The goal of this feature is to design and implement a persistent disk cache of decoded textures in `SLNG.Assets` so that returning to a previously visited region or teleporting back serves textures from pre-decoded storage in ~7 ms instead of ~93 ms, achieving an order-of-magnitude (~12.5x) speedup and near-instant texture appearance.

## Offline Benchmark & Measurements
Measured on 2026-10-08 against the user's real client cache (`user://cache/assets/*.j2c`, 18,835 cached assets, 5.19 GB):
- Sample size: 100 actual cached `.j2c` textures.

### 1. J2K Decode Times (Baseline)
| Decode Mode | Engine / Codec | Avg Time / Texture | Relative Cost |
|---|---|---|---|
| Reduce 0 (Full res) | OpenJPEG (Magick.NET) | **94.91 ms** | 100% |
| Reduce 1 (1/2 res) | CoreJ2K | **56.36 ms** | 59.4% |
| Reduce 2 (1/4 res) | CoreJ2K | **19.57 ms** | 20.6% |

### 2. Format & Compression Comparison (Decoded RGBA8)
| Format | Avg Size / Texture | 4,300 Tex Working Set | Compress Time (Worker) | Decompress Time | Total Restore Time (Disk Read + Decomp) | Speedup vs J2K |
|---|---|---|---|---|---|---|
| **Raw RGBA8** | 3,396.2 KB | 13.93 GB | 0 ms | 0 ms | 7.25 ms (Read 7.2 ms) | 13.1x |
| **LZ4 Fast** (Selected) | **1,754.5 KB** | **7.19 GB** | **7.10 ms** | **1.47 ms** | **7.60 ms** (Read 6.1 ms + 1.5 ms) | **12.5x** |
| **Zstandard (Level 1)** | 1,326.9 KB | 5.44 GB | 10.75 ms | 5.05 ms | 11.18 ms (Read 6.1 ms + 5.0 ms) | 8.5x |
| **Zstandard (Level 3)** | 1,215.3 KB | 4.98 GB | ~18 ms | 5.10 ms | ~11.2 ms | 8.5x |
| **GPU S3TC (BC1/3)** | 512 KB (1024²) | ~2.2 GB | 5.95 ms | N/A (GPU) | Engine-dependent | N/A |
| **GPU BPTC (BC7)** | 1,024 KB (1024²) | ~4.4 GB | 154.62 ms | N/A (GPU) | 155+ ms (Too slow) | Slower than J2K |

### Analysis & Design Decisions
1. **Format Decision: LZ4 Compressed `TextureData` (`.dec`)**
   - Raw RGBA8 requires ~14 GB for 4,300 textures.
   - LZ4 Fast cuts disk usage by nearly half (**51.7% of raw size**, saving ~7 GB), while decompression adds only **1.47 ms**.
   - Total restore time with LZ4 (7.60 ms) is virtually identical to uncompressed raw (7.25 ms) due to halved disk read I/O (6.1 ms vs 7.2 ms).
   - Compression (7.10 ms) runs once in the background on worker threads and never blocks display.
2. **GPU Formats Rejected for Asset Cache:**
   - Godot CPU BC7 compression takes **154.6 ms** per 1024x1024 texture (slower than J2K decode itself).
   - BC1/BC3 is engine-specific and hardware-dependent (desktop S3TC vs mobile ASTC/ETC2).
   - Storing pre-compressed GPU blocks prevents `FixAlphaEdges` post-processing and mipmap generation.
   - AGENTS.md layering rule strictly prohibits Godot dependencies in `SLNG.Assets`.
3. **Decoded Cache Key & Resolution Upgrades:**
   - Filename key: `{textureId}_r{reduceLevel}_v1.dec` (stored in `user://cache/assets/decoded/`).
   - If a request comes for `reduce = 1` and `_r0` exists on disk: `_r0` has higher resolution and immediately satisfies the request.
   - If a request comes for `reduce = 0` and only `_r1` or `_r2` exists: cache miss, decode `_r0` from `.j2c`, and save `_r0` while pruning lower-res `.dec` files for that UUID.
4. **Invalidation & Versioning:**
   - File extension `_v1.dec` and 28-byte binary header with magic bytes `SLDC` (0x43444C53).
   - If the underlying `.j2c` file is deleted or has a newer timestamp than `.dec`, the `.dec` file is treated as invalid and re-decoded.
   - If a decoded result is flagged as degraded, it is not cached (or deleted).
5. **Write Lifecycle:**
   - Display is never blocked: `AssetService.FetchAndDecodeTextureAsync` immediately returns `TextureData` to `GpuCache.PrepareImageAsync`.
   - The write to disk occurs on a background `Task.Run` worker, writing to a `.tmp` file and renaming atomically.
6. **Size Budget & Eviction:**
   - Configurable budget: default 4096 MB (4 GB), setting `DecodedTextureCacheSizeMb` in `preferences.cfg`.
   - Toggle: `DecodedTextureCacheEnabled` (default: true).
   - Eviction: LRU based on `LastAccessTimeUtc`, pruning to 85% of budget when exceeded.
   - Eviction is completely non-destructive: `.j2c` codestreams remain untouched, so evicted textures can always be reconstructed if needed.
7. **Alpha Detection & Mipmaps:**
   - `SLNG.Assets` stays engine-agnostic and returns `TextureData`.
   - `GpuCache.PrepareImageAsync` runs on worker threads (`_imagePrepGate`), performing `FixAlphaEdges`, `DetectAlpha`, `AnalyzeAlphaMaskable`, and `GenerateMipmaps`.
   - Because `TextureData` arrives in 7.6 ms instead of 94.9 ms, `GpuCache` worker lanes process uploads without stall.

## Acceptance Criteria
- [x] Offline benchmark completed and documented in spec.
- [x] `SLNG.Assets.DecodedTextureCache` engine-agnostic implementation with LZ4 compression, header validation, atomic writes, and LRU eviction.
- [x] `AssetService` checks decoded cache first and stores new decodes in background.
- [x] `[TexPipe]` diagnostic reports `decCacheHit` alongside `diskCacheHit`.
- [x] Setting in Preferences and `preferences.cfg` to toggle decoded texture cache and adjust size budget.
- [x] Unit tests for cache format, reading/writing, key upgrades, invalidation, and LRU eviction.
- [x] `AppVersion` bumped in `app/scripts/Boot.cs` (`v0.26.113-alpha`).
- [x] All unit tests passing (`dotnet test`), `dotnet format` clean, and `/slng-verify` green.

## Technical Specs & Affected Files
- `src/SLNG.Assets/DecodedTextureCache.cs` (New)
- `src/SLNG.Assets/AssetService.cs`
- `src/SLNG.Assets/SLNG.Assets.csproj` (Add `K4os.Compression.LZ4`)
- `tests/SLNG.Assets.Tests/DecodedTextureCacheTests.cs` (New)
- `app/scripts/Boot.cs` (`AppVersion` bump, wiring)
- `app/scripts/UI/NetworkPreferencesPage.cs` / Preferences
- `docs/ROADMAP.md`
