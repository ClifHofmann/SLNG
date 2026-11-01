# [FEAT-PERF-29] Progressive texture streaming

- **Feature ID:** `FEAT-PERF-29`
- **Track:** `net` + `assets` (perf)
- **Status:** `🧪 Review` (PR 1 spike merged as this document; PR 2 built in v0.28.83-alpha, in-world A/B open)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md),
  [ADR 0005](../adr/0005-region-arrival-texture-streaming.md) task 3

## Overview & Goal

Every texture is fetched in full: `GpuCache` always asks for discard 0 (`app/scripts/GpuCache.cs:1910-1928`).
On a cold arrival at Sirens that is 1.7-1.9 GB at about 18 MB/s, and the line is now the limit (ADR 0005,
after FEAT-PERF-31). The reference viewer streams instead: a small first request, then the bytes for the
discard the pixel area needs.

FEAT-PERF-02 Phase 2 built network-side discard in July and switched it off after a live regression, with
the root cause never found (`docs/specs/FEAT-PERF-02-texture-loading-speed.md`, Phase 2). This spike finds
that cause offline, from the asset cache, and measures whether a cut codestream reaches a usable texture on
the path the pipeline takes. PR 2 builds streaming on that answer.

## Spike result (PR 1)

### Answer: why FEAT-PERF-02 Phase 2 failed

**The CoreJ2K fallback did not exist in the client when Phase 2 ran.** Phase 2 went live and was switched
off on 2026-07-25 (`c3257932`, `6d7ebf48`, `ad0915d9`). CoreJ2K's SkiaSharp image creator was registered
only on 2026-08-02 (`4840221c`). Until then `J2kImage.DecodeToImage<SKBitmap>` threw "No image creator
registered for target type SkiaSharp.SKBitmap" inside the Godot client, behind a bare `catch { }`. So every
texture whose Range response was cut went:

1. Magick.NET (OpenJPEG, strict) threw "Tile part length size inconsistent with stream length". It does
   that for every cut, because an SL asset is one tile-part whose `Psot` declares the whole asset (see
   "Codestream structure" below).
2. CoreJ2K read the stream, logging "Codestream truncated in tile 0" (the 6,752 of 6,786 log lines), and
   then threw when it tried to create the bitmap.
3. `DecodeTexture` returned null. The retry loop fetched the same Range twice more and failed the same way,
   which is the "slower loading" symptom.

Only textures that fit inside the Range limit came back whole and decoded through Magick. Phase 2 sized the
Range with `CalcDataSizeJ2C(0, 0, d)`. With today's cache (10,215 textures), that limit cuts 68.5 % of
textures at discard 3, 91.1 % at discard 4 and 95.6 % at discard 5. That matches "about 80 % of textures
never appeared". "Fails outright at higher discard" was the same thing: more textures were cut.

It was not the byte estimate, and it was not CoreJ2K's ability to read a partial stream. Both work (below).

### Would it work today? Not as built

Today the fallback exists, but the pipeline still treats an intended partial stream as a broken one:

| Path for a cut codestream | Result on every cut measured |
|---|---|
| Magick first (`DecodeTexture`, `AssetService.cs:1890-2023`) | Throws on 100 % of cuts ("Tile part length size inconsistent with stream length"). Magick.NET has no switch for OpenJPEG's strict mode (`AssetService.cs:1273-1281`). |
| CoreJ2K at full resolution (the fallback, `:2047-2117`) | Decodes, but is always flagged `IsDegraded` (`:2106`). `FetchAndDecodeTextureAsync` then marks HTTP undecodable, so the next attempt goes over UDP. It also writes a `.degraded` copy, retries up to 3 times, returns null to `rejectDegraded` callers (bakes) and never caches the result (`:1188-1265`). |
| CoreJ2K reduced (`TryDecodeReduced`, `:1731-1788`) at the discard the bytes were cut for | Clean, not degraded, and within 1.2/255 (p95) of the complete stream at that resolution. **This is the path streaming needs.** |

The pipeline reaches the reduced path only by accident. It takes the reduce level from the screen area
(`ReduceFactorFromHeader`, `:1157`), not from the discard the bytes cover. With no area (0), or with an area
that wants more detail than the bytes hold, it decodes at full resolution and lands in the degraded branch.

The three suspects from the ADR, answered:

- **Magick first:** rejects every cut. It must never see an intended partial stream.
- **Degraded / `J2cCodestream.IsTruncated`:** `IsTruncated` is not on today's path. It was used only from
  2026-08-23 (`78669354`) to 2026-08-26 (`ade9b2f7`), and today only tests call it. It would flag every
  intended cut, because the single tile-part's `Psot` covers the whole asset. So it must never judge a
  Range response. The degraded handling described above is the real blocker today.
- **The byte estimate:** not a cause. `J2kByteSizeEstimator` is a faithful port of `calcDataSizeJ2C`
  (`scratch/slviewer/indra/llimage/llimagej2c.cpp:269-300`). It over-asks by a wide margin (table below).
  Phase 2's `(0, 0)` call assumes 2048x2048. For smaller textures that asks for a few KB more, which does
  no harm.

### Measurement

Method: a throwaway console harness (kept in the session scratchpad, not in `tools/`) references
`SLNG.Assets` and calls `AssetService.DecodeTexture` and `TryDecodeReduced` by reflection. Those are the
exact methods `FetchAndDecodeTextureAsync` uses.
- **Inputs:** 71 cached codestreams, copied read-only from `%APPDATA%\Godot\app_userdata\Puris Viewer\cache\assets`.
  They are stratified by size (64-2048 px) and components (3/4): 6 per class.
- **Cuts:** each one is cut at the viewer's 600-byte first request, and at `CalcDataSizeJ2C(w, h, d)` for
  d = 5..0. That is what the viewer asks for once the header gives the size.
- **Error:** mean absolute error per channel (0-255, RGBA). It compares the cut's reduced decode with the
  reduced decode of the complete stream at the same resolution, so it measures what the missing bytes cost
  and not the decoder.

| Request | Textures actually cut | Bytes asked | Share of the file (p50) | Whole cache at this discard | Decodes (reduced path) | Error p50 / p95 / max | Magick on the cut | Full-res path on the cut | Decode ms p50 |
|---|---|---|---|---|---|---|---|---|---|
| First 600 B, decoded at discard 5 | 68 of 71 | 600 | 2 % | 0.1 % | 62 / 68 (6 null) | 1.08 / 32.4 / 90.6 | 0 / 68 | degraded 62, null 6 | 0.2 |
| Discard 5 | 42 of 71 | 16,792 (≤ 1024²) / 18,840 (2048²) | 11 % | 3.1 % | 42 / 42 | 0.09 / 1.19 / 2.16 | 0 / 42 | degraded 42 | 0.6 |
| Discard 4 | 32 of 71 | 49,560 / 59,800 | 13 % | 9.1 % | 31 / 32 (1 null †) | 0.05 / 0.95 / 2.38 | 0 / 32 | degraded 31, null 1 | 1.9 |
| Discard 3 | 21 of 71 | 180,632 / 223,640 | 29 % | 29.6 % | 20 / 21 (1 null †) | 0.01 / 0.60 / 1.77 | 0 / 21 | degraded 20, null 1 | 11.2 |
| Discard 2 | 10 of 71 | 704,920 / 879,000 | 56 % | 66.5 % | 10 / 10 | 0.00 / 0.10 / 0.18 | 0 / 10 | degraded 10 | 59.3 |
| Discard 1 | 1 of 71 | 2,802,072 / 3,500,440 | 58 % | 89.0 % | 1 / 1 | 0.00 | 0 / 1 | degraded 1 | 290 |
| Discard 0 | 0 of 71 | full | 100 % | 100 % | - | - | - | - | - |

- "Textures actually cut": the rest are smaller than the request and arrive whole.
- "Whole cache at this discard": the bytes every one of the 10,215 cached textures (5.41 GB) would need at
  that discard, as a share of the complete assets. Phase 2's `(0, 0)` sizes give 3.4 / 10.6 / 34.3 / 67.1 %
  for discard 5 / 4 / 3 / 2.
- "Full-res path": `DecodeTexture(cut, reduce: 0)`. Every cut is degraded. Its error against the
  complete stream at full resolution is p50 3.0 / 2.5 / 2.4 / 2.3 for cuts at discard 5 / 4 / 3 / 2
  (p95 11-15): a blur, not noise, but a blur at full size.
- The estimate is dimension-blind up to 1024²: `max_layers` is clamped to at least 4 (`llimagej2c.cpp:284`), so
  64² and 1024² get the same sizes. A 1024² texture (70 % of the cache, ~390 KB) therefore has three real
  cut points (discard 5, 4, 3); discard 2 asks for more than the file holds.
- The CoreJ2K decoder that streaming relies on agrees with Magick on complete streams within 1/255 (max 0.75 with 3
  components, 0.97 with 4).

**† A CoreJ2K bug, not a byte problem.** At some cut points CoreJ2K throws `IndexOutOfRangeException` in
`FileBitstreamReaderAgent.readTilePkts`. One texture (2048², 4 components) fails between 59,790 and
59,810 bytes and decodes at 59,700 and at 59,900. Over 2,320 random cut points in the 71 samples, 33 failed
(1.4 %). Cutting the same bytes 256 B earlier rescued 27 of the 33, and 1 KB earlier rescued 25. Appending an
EOC marker rescued 3. Each of the four estimate cut points that failed in the table decodes when the
prefix is cut at most 1 KB shorter.

**A cut decode is not always deterministic.** The table was measured twice, and 779 of 780 rows matched.
The one that did not was the 600-byte cut of a 32x128 texture with a single quality layer, so the cut falls
inside its only layer. Isolated runs give an error of 8.4 every time. In the second full run, two decodes of
the same bytes, one call apart, gave 32.0 and 65.8. The cause is not established. Some state survives
between decodes in one process.

**The 600-byte first request** is header plus quality layer 0. The encoder's first layer target is exactly
`FIRST_PACKET_SIZE` (`llkdu/llimagej2ckdu.cpp:755`). For small and medium textures it decodes at discard 5
within 1/255. For 2048² and layer-rich 1024² textures it is far off: 8-90/255 against the complete stream's
discard-5 decode. 6 of 68 give nothing at all.

**How much the estimate over-asks.** For each texture and discard, a bisection over the prefix length found
the shortest prefix whose reduced decode is within 1/255 of the complete stream's. The estimate is that
length times 25 (discard 5), 52 (4), 78 (3), 194 (2) and 426 (1), median. It falls short only for 2048²
textures at discard 5 and 4: 2 of 71, at most 2.4/255 at the estimate. Mean error is a lenient measure of
blur, so this says the estimate is never too small. It does not say how much less could be asked. PR 2
keeps the viewer's sizes.

### Codestream structure (what makes a prefix decodable)

Survey of 6,000 cached assets (header only):
- 5,999 are LRCP (layer-resolution-component-position).
- All 6,000 are a single tile with one tile-part, and all have 5 decomposition levels.
- Most have 5-7 quality layers.
- 83 % have 3 components and 17 % have 4.

The encoder sets the quality layers at cumulative byte targets 600, 2,000, 8,000, 32,000, ... (×4 each;
`llkdu/llimagej2ckdu.cpp:752-767`). With LRCP, a prefix therefore holds every resolution at a lower quality.
Decoded at a reduced resolution, only the low bands count, and they fill first. That is why a cut decodes
cleanly at its discard and only blurs at full resolution.

### What the reference viewer does (verified in `scratch/slviewer`, `develop` 4ef9f8f14b, 2026-08-25)

- **Unknown size:** the desired discard is the max discard. A new GL image starts at `MAX_DISCARD_LEVEL`
  (`llrender/llimagegl.cpp:563`, `newview/llviewertexture.cpp:3058-3061`). So the first request is
  `TEXTURE_CACHE_ENTRY_SIZE` = 600 bytes at discard 5 (`newview/lltexturefetch.cpp:2587-2593`,
  `newview/lltexturecache.cpp:51`, `llimage/llimage.h:64`).
- **Desired discard 0:** the full asset in one request, with no probe (`lltexturefetch.cpp:2572-2579`).
- **Known size:** `calcDataSizeJ2C(w, h, c, desired_discard)` (`lltexturefetch.cpp:2580-2586`).
- **Desired discard from the pixel area:** `floor(log4(texels / virtual size))`, clamped to 0..5
  (`llviewertexture.cpp:3063-3100`). `TextureLod.DiscardLevelFor` already does the same.
- **Refinement continues where the data ends.** The Range starts at the current size minus one byte, so a
  cache such as Varnish never answers with a 200 and the whole asset (`lltexturefetch.cpp:1425-1441`,
  `:1483-1490`).
- **Decode at the discard that was fetched:** `mLoadedDiscard = mRequestedDiscard` (`:1713`) is passed to
  the decoder (`:1781`, `:1808`). OpenJPEG gets that reduce factor with strict mode off
  (`llimagej2coj/llimagej2coj.cpp:418-421`). It never decodes a partial stream at full resolution.
- **Firestorm's own cache on this machine** (`texture.entries`, 33,479 entries) shows the result:
  - 28 % are whole.
  - 34 % are partial bodies, clustered at 4.7 / 12.9 / 45.7 / 176.7 KB.
  - 38 % hold only the first 600 bytes. Their image size is stored as data + 1, which is the "not fully
    loaded" flag (`lltexturefetch.cpp:1883-1888`).

  So for most textures it never fetches the whole asset.

### Bakes: fetch in full

The ADR left open whether SL's bake host serves J2C (stream it) or not (fetch in full). It serves J2C: an
HTTP capture of Firestorm's bake requests showed raw J2C bodies (BUG-AVATAR-02). But the reference viewer
that most SL users run still fetches bakes in full:

- A server bake gets a full request when its URL has an extension that is not `j2c`
  (`lltexturefetch.cpp:2552-2564`, "Do full requests for baked textures to reduce interim blurring").
- The bake URL is `<appearance service>texture/<agent>/<slot>/<id>`, with no extension
  (`newview/llvoavatar.cpp:5939-5958`).
- `LLDir::getExtension` takes the base name after the last directory delimiter (`llfilesystem/lldir.cpp:603-637`).
  On Windows that delimiter is `\` (`lldir_win32.cpp:98`), so a URL has no base name, and the "extension" is
  everything after the host name's last dot. That is not `j2c`, so the request is full.
- On Linux and macOS (`/`) the extension is empty, and bakes stream like any texture.

**Decision: PR 2 fetches bakes in full**, which is what the Windows viewer does.
- Bakes are few: at most 11 per avatar, against thousands of scenery textures.
- A blurry interim bake is the visible case the viewer's own comment avoids.
- SLNG has never sent the bake host a Range request (`FetchBakeTextureDataAsync` is always a full fetch,
  `GridSession.Assets.cs:379`), so a 206 from it is unverified.

Streaming bakes is a later option, if the `[Arrival]` trace puts them on the critical path after PR 2.

## Build plan (PR 2)

Behind `--tex-stream` (`Diagnostics.cs`, like `--tex-slots`), off by default, with `run-client.ps1
-TexStream` for the A/B. No Preferences entry in this PR: that needs the maintainer's OK and the Preferences
plan. (Since v0.28.84 streaming is on by default; see "Default on" below.)

1. **First request: the estimator's discard-5 size for an unknown size** (`CalcDataSizeJ2C(0, 0, 5)` =
   18,840 B), not the viewer's 600 B.
   - It decodes cleanly in 41 of the 42 cut cases (error p95 1.2/255). The one failure is the CoreJ2K bug,
     and it decodes 256 B shorter.
   - It costs 3.4 % of the complete bytes, against 0.1 %. At 18 MB/s that is about 5 s of line for a
     4,800-texture arrival.
   - The 600 B request is 9 % blank and badly wrong on large textures.

   The first-request size is a knob (`--tex-stream-first=<bytes>`) so the A/B can try 600.
2. **Skip the probe where the viewer skips it.** A texture whose desired discard is already 0 is fetched in
   full in one request (`lltexturefetch.cpp:2572-2579`). That covers your own avatar, the HUD, sculpt maps
   (`AssetService.cs:708`) and bakes.
3. **Refine to the needed discard directly.** The target is the discard `TextureLod.DiscardLevelFor` gives
   for the pixel area, at the same priority. The bytes asked are `CalcDataSizeJ2C(w, h, d)` with the
   header's real size, never `(0, 0)` once the header is known.
   - The Range starts at the bytes already held (minus one, as the viewer does), and they are appended.
   - Intermediate discards are not fetched one by one.
   - A refinement runs only when the needed discard is below the loaded one.
4. **`GridSession`: one Range fetch that reports completeness.** It returns the bytes, whether the asset is
   complete, and the total size when known.
   - A 200, a 416, or a body shorter than asked means complete.
   - A 206 with `Content-Range` gives the total.

   The EOC check (`GridSession.Assets.cs:767-768`) and `J2cCodestream.IsTruncated` must not judge a Range
   response. Also fix the stale MeshPipe comment at `GridSession.Assets.cs:275-277` in passing.
5. **`AssetService`: an intended partial decodes only on the reduced path.** The reduce level is
   `max(loaded discard, the screen's reduce)`, clamped as today.
   - Never Magick, never CoreJ2K at full resolution.
   - The result is not degraded. It carries its loaded discard (`TextureData`), so the renderer knows it
     can be sharpened.
   - A null decode of a partial is first retried with the prefix cut 256 B and then 1 KB shorter (the
     CoreJ2K bug, 27 of 33 rescued). If that fails too, fetch the next discard.
   - A partial never sets `httpUndecodable`, never goes to UDP and never writes `.degraded`.
6. **Caches keep partial bytes with their discard.**
   - The raw disk cache gets a `<id>_v5.part` file with the prefix and its discard and total. It becomes
     `<id>_v5.j2c` once complete. `DiskCache` accounts for it like any entry.
   - A refinement reads the `.part` first and fetches only the missing tail, so a warm start never
     downloads again what is cached.
   - `DecodedCache` may store a decode from a prefix under reduce `r` only when the prefix covers
     `CalcDataSizeJ2C(w, h, r)`.
   - `_memCache` keeps storing full decodes only (`AssetService.cs:900-910`).
7. **`GpuCache` asks for the coarse first request, then refines in place** through the sharpen path that
   exists (`TryUpgradeCachedTexture`, `GpuCache.cs:739`, `:850`).
   - A coarse upload is skipped when the refinement has already arrived.
   - Alpha has a floor (BUG-RENDER-15): a 4-component texture refines to at least 64 px on its long side.
   - Its alpha classification (`DetectAlpha` / `AnalyzeAlphaMaskable`) is redone on every refinement,
     because a classification made on the tiny first image is the latent issue BUG-RENDER-15 names.
8. **`--diag` counters:** `[TexStream]` with first requests, refinements by target discard, bytes per step,
   `.part` hits, and partial decodes rescued by the shorter cut.

### Risks to measure in the A/B

- **Requests, not bytes.** The first pass is one small request per texture through 8 HTTP/1.1 slots with
  no pipelining (`GridSession.Assets.cs:315-323`). The viewer pipelines 8-12 connections. If `[TexHttp]`
  shows the coarse pass bound by round trips, raise the slots for small requests (`--tex-slots`). That is the
  case `-TexSlots 12` could not show while transfers were bandwidth-bound.
- **Main-thread uploads** rise from one to up to two or three per texture, against a `mainQueue` that is
  already the bottleneck. The A/B reports main-thread time, not just bytes.
- **Non-deterministic cut decodes** (see the measurement). Watch for speckle on coarse textures in-world.
  The tests decode the same partial twice in one process and compare. If this shows up for real, do not
  upload a coarse decode taken from inside the first quality layer: ask at least 2,000 B, the encoder's
  second layer target.
- **Non-power-of-two sizes:** `TryDecodeReduced` expects `dim >> steps`, while JPEG 2000 rounds up. Every
  sampled asset is a power of two. A mismatch is treated as "fetch in full", not as an error.

## Acceptance Criteria

PR 1 (spike):
- [x] Real cached codestreams are cut at the 600-byte request and at every `J2kByteSizeEstimator` boundary,
      and decoded on the pipeline's own methods (table above).
- [x] Why Phase 2 failed: the CoreJ2K fallback did not exist in the client, so every cut texture went blank.
      Today the pipeline would still treat a cut as broken (degraded branch, Magick first).
- [x] The viewer's behaviour is verified with file:line in `scratch/slviewer`.
- [x] Bakes: fetched in full, with the reason.
- [x] No production code changed.

PR 2 (build), measured with the FEAT-PERF-28 time base, Sirens or KinkyEvent, cold, at least 3 runs with
`--tex-stream` and 3 without:
- [ ] Bytes until texture inflight is back to 0: at most half the baseline (ADR 0005 target).
- [ ] Arrival until no face on screen is without a texture: at most half the baseline.
- [ ] No `[TextureGiveUp]`, `.degraded` file or UDP fallback caused by an intended partial stream.
- [ ] Main-thread time for texture uploads reported. More than +25 % against the baseline blocks the default.
- [ ] Near textures reach full resolution, alpha textures keep their silhouette at distance, and nothing is
      speckled (in-world look, maintainer).
- [x] Unit tests (test-first): Range response to completeness/total; partial decode via the reduced path
      only and never degraded; the same partial decoded twice gives the same pixels; the 256 B / 1 KB rescue;
      `.part` cache round trip and promotion; refinement target from the pixel area with the alpha floor
      (`TextureRangeTests`, `TextureStreamingTests`, `TextureStreamerTests`).

### How PR 2 is built (v0.28.83-alpha)

The A/B ran with `tools/baseline-run.ps1 -TexStream` on v0.28.83, where `--tex-stream` needed `--diag`. Since
v0.28.84 streaming is the default (see "Default on" below).

- `SLNG.Net.TextureRange` (pure) builds the range and reads the response; `GridSession.FetchTextureRangeAsync`
  sends it over the ViewerAsset cap with the same slots and `[TexHttp]`/`[TexWait]` accounting as a full fetch.
  In `AssetService` it waits at the same 32-wide admission gate first, so both arms queue alike.
- `SLNG.Assets.TextureStreamer` does one step per call: the coarse first request, or the missing bytes up to the
  target discard, or nothing when the `.part` already covers it. A complete response becomes `<id>_v5.j2c` and is
  decoded by the existing cache path. Anything that does not work out (no cap, an error, an undecodable prefix after
  the rescue) falls back to today's full fetch.
- **Refinement reuses `GpuCache`'s sharpen path** instead of a second mechanism. A partial upload is recorded as
  built for a quarter of its texels (`TextureStreaming.BuiltForArea`), so `TryUpgradeCachedTexture` fetches more as
  soon as the face shows more. It is called right after the coarse upload, and again on every re-offer; the existing
  budget and admission rules decide when there is room.
- Not streamed: sculpt maps, bakes (`GetBakeTextureAsync`), the wearer's own textures (`_noShrink`), and any request
  without a screen area.
- Left out on purpose: no decoded-cache entry and no compressed GPU copy is stored from a prefix; both stand for the
  whole asset.
- `[TexStream]` (`--diag`, every 5 s): first requests and MB, refinements and MB, decodes from `.part` without a
  request, completed assets, rescued decodes, fallbacks.

### Default on (v0.28.84-alpha)

The A/B (off `baseline/20261101-1636`, on `baseline/20261101-1745`): cold texture bytes 1.94 -> 0.83 GB, the last
network wave over at ~126 -> ~91 s, warm unchanged; the maintainer's look in-world found nothing wrong. So:

- Streaming is on in every session, with or without `--diag`. `--no-tex-stream` turns it off
  (`TextureStreaming.EnabledByArgs`); `tools/run-client.ps1 -NoTexStream` and `tools/baseline-run.ps1 -NoTexStream`
  pass it, and `info.txt` records the arm.
- `--tex-stream` / `-TexStream` are still accepted and change nothing, so older scripts and notes keep working.
- `--tex-stream-first=N` stays an A/B knob and needs `--diag`, like `--tex-slots`. The `[TexStream]` line is
  `--diag` only, as before.
- **No Preferences entry.** The Preferences plan (`docs/specs/FEAT-UI-79-preferences-rework.md`) has no row for it.
  The fitting place by its own "Rule for Advanced" would be Network & cache › Network › Advanced, but adding a row
  the plan does not name needs the maintainer's OK and a plan update first (AGENTS.md).

**Known gap: alpha is classified on the coarse image.** `GpuCache` computes `DetectAlpha` / `AnalyzeAlphaMaskable`
on every upload, the refined one included, but `ObjectRenderer.ApplyAlphaCutout` reads them only when a face's
material is built, so the coarse upload decides. Measured offline on the 33 four-component textures of the spike
sample: 29 keep their class, 3 change from None (an alpha channel that is all 255) to Blend on the 64 px coarse
decode, all of them 2048x2048. Those faces get the cutout shader with alpha ~1, so they look opaque but leave the
opaque path. The reference viewer re-reads the alpha on each new image (`LLImageGL::analyzeAlphaData`). Fixing it
here takes three parts, which is why it is not in this change: a "refined, alpha class changed" signal from
`GpuCache`'s sharpen; an index from texture id to the legacy materials bound to it (with their tint, fullbright and
"resolved by the legacy material" flags, from `BindLegacyFaceTexture`); and an `ApplyAlphaCutout` that can run a
second time, since today it starts from an opaque material and its None branch returns without undoing an earlier
alpha choice.

## Technical Specs & Affected Files (PR 2)

- `src/SLNG.Net/GridSession.Assets.cs`: Range fetch with completeness, offset continuation, stale comment
- `src/SLNG.Net/J2kByteSizeEstimator.cs`: unchanged (verified port)
- `src/SLNG.Assets/AssetService.cs`: partial decode path, `.part` cache, retry and degraded rules for partials
- `src/SLNG.Assets/TextureData.cs`: loaded discard
- `src/SLNG.Assets/TextureLod.cs`: alpha floor for the refinement target
- `app/scripts/GpuCache.cs`: first request, refinement, in-place replacement, alpha re-classification
- `app/scripts/Diagnostics.cs`, `tools/run-client.ps1`: `--tex-stream`, `--tex-stream-first`
- `app/scripts/Boot.cs`: `AppVersion`

## Sub-tasks / Progress

- [x] PR 1: spike, offline from the cache (this spec)
- [x] PR 2: build behind `--tex-stream`, tests first (v0.28.83-alpha)
- [x] Streaming on by default, `--no-tex-stream` to turn it off (v0.28.84-alpha)
- [ ] PR 2: in-world A/B against the FEAT-PERF-28 baseline
