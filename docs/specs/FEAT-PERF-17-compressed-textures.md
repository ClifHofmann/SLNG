# [FEAT-PERF-17] Real VRAM budget (Part A) and GPU-compressed textures (Part B)

- **Feature ID:** `FEAT-PERF-17`
- **Track:** `render` / `assets`
- **Status:** `🧪 Review`. Part A built (v0.27.33-alpha); Part B built (v0.27.46-alpha), in-world acceptance open.
- **Owner:** `gemini` (Part A), `claude` (Part B)
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md), [MVP6-1](MVP6-1-performance-program.md), [ADR 0004](../adr/0004-performance-architecture.md) pillar P4

## Overview & Goal

The viewer must not assume a 12 GB graphics card. It has to stay smooth on 4 GB and 8 GB cards, and
on a 12 GB card that is shared with other programs.

Measured on 2026-10-10 at Sirens Beach (Agni) on an RTX 4070 with 12 GB:

| Who | Dedicated VRAM |
|---|---|
| Whole adapter (Windows counter) | **11.2 of 12 GB** |
| Puris | 6.5 GB |
| Firestorm (running alongside) | 2.1 GB |
| Sunshine | 1.5 GB |

`renderGpuMs` swung between 30 and 250 ms even with shadows off and only 5–13k draws. That is the
signature of the driver paging video memory. Closing Firestorm raised Puris from ~12 to 24 fps.

Today `GpuCache` (`app/scripts/GpuCache.cs`) works against a **fixed** budget taken from the
texture-memory slider:
- `GraphicsSettings.TextureMemoryMb`, default 1536
- set via `GpuCache.SetBudget` in `Boot.ApplyGraphicsSettings` (`Boot.cs` ~3084)
- read once by the constructor (`Boot.cs` ~3839)

Nothing knows how much memory the card really has free.

- **Part A** asks Windows for this process's real video-memory budget and sizes the cache from it.
- **Part B** stores textures block-compressed (BC1/BC3/BC7), about 1/4 of today's RGBA8 size.

---

## Part A: real VRAM budget (implement this now)

### Rules for this task

- Branch `feature/FEAT-PERF-17-vram-budget`, created from the **current** branch. Do not switch
  away from it.
- Set this task's roadmap row: Owner `gemini`, status 🚧. Set it to 🧪 when done.
- Bump `AppVersion` in `app/scripts/Boot.cs` by one patch number (v0.27.32-alpha → v0.27.33-alpha,
  or one above whatever is there).
- **No `using Godot;` in `src/`.**
- **No new NuGet packages, and no `unsafe`** (the app project does not allow unsafe blocks). Call
  COM through vtable pointers with `Marshal`, as described below.
- **The viewer must never crash or stop because of this feature.** Every DXGI call sits in
  try/catch. On any failure, log once and fall back to today's behaviour (the slider value).
- Commit format: `feat(render): [FEAT-PERF-17] real VRAM budget from DXGI (v0.27.33-alpha)`.
  Do not merge.

### A1. Pure budget policy in `src/SLNG.Core` (write the tests first)

**New file `src/SLNG.Core/VramBudgetPolicy.cs`.** A static class, no Godot, no Windows types:

```csharp
public static class VramBudgetPolicy
{
    public const long MinBudgetBytes = 256L << 20;
    public const long MinHeadroomBytes = 256L << 20;
    public const double HeadroomFraction = 0.10;
    public const double DropThreshold = 0.97;     // a fall of more than 3 % applies at once
    public const double RiseThreshold = 1.03;     // a rise of more than 3 % is considered...
    public const double MaxRiseFactor = 1.10;     // ...but grows by at most 10 % per step
    public const double RiseIntervalSeconds = 10; // and at most once every 10 s

    /// osBudget: what Windows grants this process (bytes), already capped by --vram-budget.
    /// osUsage: what this process currently uses on the card (bytes).
    /// cacheBytes: GpuCache.CurrentSizeBytes. manualCapBytes: null in Auto mode.
    /// Returns the new GpuCache budget in bytes.
    public static long Compute(long osBudget, long osUsage, long cacheBytes, long? manualCapBytes,
                               long previousBudget, double secondsSinceLastRise, out bool rose);
}
```

**The computation:**

1. `nonCache = max(0, osUsage - cacheBytes)`. This covers render targets, shadow maps, buffers and
   everything else that is not in the cache.
2. `headroom = max(MinHeadroomBytes, osBudget * HeadroomFraction)`.
3. `target = max(MinBudgetBytes, osBudget - nonCache - headroom)`.
4. If `manualCapBytes` has a value: `target = min(target, manualCapBytes)`. In Manual mode the OS
   budget is still a hard safety limit, because exceeding it pages.
5. Smoothing against `previousBudget`. On the very first call pass `previousBudget = 0` and return
   `target` directly.
   - `target < previousBudget * DropThreshold` → return `target` (shrink at once).
   - `target > previousBudget * RiseThreshold` and `secondsSinceLastRise >= RiseIntervalSeconds`
     → return `min(target, previousBudget * MaxRiseFactor)` and set `rose = true`.
   - Otherwise return `previousBudget` (dead band, no change).

**Tests: new file `tests/SLNG.Core.Tests/VramBudgetPolicyTests.cs`.** Cover at least:

- **First call:** returns the target.
- **12 GB card, little other usage:** a large budget.
- **3.5 GB simulated budget, ~1.2 GB non-cache usage:** ≈ 1.95 GB.
- **Usage above budget (paging):** the result shrinks immediately.
- **Small changes:** stay in the dead band.
- **Rises:** capped at +10 % and at most one per 10 s.
- **Manual cap:** below the target → the cap; above the target → the target.
- **Floor:** never below 256 MB.

### A2. DXGI probe in the app (Windows only)

**New file `app/scripts/DxgiVideoMemory.cs`**, class `DxgiVideoMemory : IDisposable`, namespace
`SLNG.App`. Use it only when `OperatingSystem.IsWindows()`.

**P/Invoke:**

```csharp
[DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
```

**IIDs:**
- `IDXGIFactory1` = `770aae78-f26f-4dba-a829-253c83d1b387`
- `IDXGIAdapter3` = `645967a4-1392-4310-a798-8053ce3e93fd`

**Calling a COM method by vtable slot** (no unsafe):

```csharp
IntPtr vtbl = Marshal.ReadIntPtr(comPtr);
IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
var d = Marshal.GetDelegateForFunctionPointer<SomeDelegate>(fn);
```

Declare each delegate with `[UnmanagedFunctionPointer(CallingConvention.StdCall)]`. The first
parameter is always the `IntPtr` this-pointer. Cache the delegates.

**Vtable slots** (count from 0; IUnknown 0–2, IDXGIObject 3–6):

| Interface | Method | Slot |
|---|---|---|
| IUnknown | QueryInterface(ref Guid, out IntPtr) | 0 |
| IUnknown | Release() | 2 |
| IDXGIFactory1 | EnumAdapters1(uint index, out IntPtr adapter1) | **12** |
| IDXGIAdapter1 | GetDesc1(out DXGI_ADAPTER_DESC1) | **10** |
| IDXGIAdapter3 | QueryVideoMemoryInfo(uint node, int segmentGroup, out DXGI_QUERY_VIDEO_MEMORY_INFO) | **14** |

- `EnumAdapters1` returns `DXGI_ERROR_NOT_FOUND` = `unchecked((int)0x887A0002)` after the last
  adapter.
- `segmentGroup` 0 = `DXGI_MEMORY_SEGMENT_GROUP_LOCAL` (dedicated VRAM). Use node 0.
- An `HRESULT < 0` is a failure.

**Structs** (`[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]`):

```csharp
struct DXGI_ADAPTER_DESC1 {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    public uint VendorId, DeviceId, SubSysId, Revision;
    public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory; // SIZE_T
    public uint LuidLow; public int LuidHigh;
    public uint Flags;            // bit 2 (value 2) = DXGI_ADAPTER_FLAG_SOFTWARE -> skip
}
struct DXGI_QUERY_VIDEO_MEMORY_INFO {
    public ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation;
}
```

**Adapter choice** (once, at start):

1. Enumerate all hardware adapters (skip `Flags & 2`). Keep each `IDXGIAdapter1` pointer and its
   desc; release the rest at the end.
2. Godot renders through Vulkan, so match by name: take `RenderingServer.GetVideoAdapterName()`
   (call it once, on the main thread). Pick the adapter whose `Description` equals it
   case-insensitively, or else contains it or is contained in it.
3. No match → the adapter with the largest `DedicatedVideoMemory`.
4. `QueryInterface` the chosen adapter to `IDXGIAdapter3`, keep that pointer, release all others.
5. Log once:
   `[VramBudget] adapter="<desc>" dedicated=<MB> MB matched=<name|largest> osBudget=<MB> osUsage=<MB>`.

**API:**
- `bool TryQuery(out long budgetBytes, out long usageBytes)`
- `Dispose()` releases the adapter pointer
- `static DxgiVideoMemory? TryCreate(string godotAdapterName)` returns null on any failure and
  logs why, once

**Thread:** call it from the main thread, at most every 2 s. It costs microseconds.

### A3. Test flag `--vram-budget <MB>`

Read it with `OS.GetCmdlineUserArgs()` (pattern: `Boot.cs` ~3853, `--no-object-cache`). Accept
`--vram-budget 3500` and `--vram-budget=3500`.

When set, use `osBudget = min(realOsBudget, MB << 20)`. **This is how we test a 4 GB card on a
12 GB machine.** Also allow the flag without DXGI (non-Windows or DXGI failure): then
`osBudget = MB << 20` and `osUsage` = Godot's reported total, via
`RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed)`. That call is a
synchronous render-server read: do it only in this fallback, at most every 2 s.

### A4. Wiring

**`GraphicsSettings.cs`:** add `public bool TextureMemoryAuto { get; private set; } = true;`
- config key `texture_memory_auto`
- load and save next to `texture_memory_mb`, including the per-preset save/load places
  (~401 and ~440)
- setter `SetTextureMemoryAuto(bool)` that calls `Save()`
- presets do not change it

**`GraphicsPreferencesPage.cs`** (slider ~537):
- add a checkbox "Automatic (from free video memory)" above the slider
- add both strings to `app/i18n/en-US.json` and `app/i18n/de-DE.json`. German:
  "Automatisch (nach freiem Grafikspeicher)"
- **When Auto is on:** the slider is disabled and its label shows the current automatic budget,
  e.g. `Auto: 4120 MB`
- **When Auto is off:** the slider works as today, and the OS limit still caps it (A1 step 4)

**`Boot.cs`:**
- After `_gpuCache` is created (~3839), create the probe: `DxgiVideoMemory.TryCreate(...)` on
  Windows. Keep it in a field and dispose it on exit.
- Every 2 s, in `_Process` next to `_gpuCache?.Tick()` (~2869):
  1. query the OS budget and usage, applying `--vram-budget`
  2. call `VramBudgetPolicy.Compute(...)`, using `null` as the manual cap when
     `TextureMemoryAuto` is on, else `TextureMemoryMb << 20`
  3. call `_gpuCache.SetBudget(result)` only when the value changed
  4. remember the last rise time
- `ApplyGraphicsSettings` (~3084) must not overwrite the automatic budget with the slider when
  Auto is on. Run the same policy there instead, so a slider or checkbox change applies at once.
- With no probe and no flag, everything stays exactly as today.

**`GpuCache.cs`:**
- `SetBudget` logs every call with `Console.Error.WriteLine`. With an automatic budget that is
  too noisy: only log when the change is at least 5 % or 128 MB.
- Add `public void ReportOsMemory(long budgetBytes, long usageBytes)`, which stores both.
- Append `osBudgetMB=<n> osUsageMB=<n> auto=<true|false>` to the periodic `[GpuCache]` stats line
  (~448). Keep the existing fields unchanged; tools parse them.

### A5. Acceptance checks (all must pass, report each)

1. `dotnet build SLNG.sln` and `dotnet build app/SLNG.App.csproj`: 0 errors. `dotnet test`: all
   green, including the new policy tests. `dotnet format` clean on the new files.
2. Headless selftest passes:
   `godot --headless --path app --log-file user://logs/selftest/godot.log -- --selftest`.
   Headless has no DXGI adapter: the probe must fail gracefully, and the selftest must not get
   slower or noisier than one log line.
3. **In-world on the 12 GB card, viewer alone:**
   - The `[VramBudget]` line shows the RTX 4070.
   - `osUsageMB` is within ~10 % of Task Manager's "Dedicated GPU memory" for the Godot process.
   - The cache budget is well above 1536 MB.
4. **In-world with Firestorm running alongside:** `osBudgetMB` drops by roughly Firestorm's usage
   within a few seconds, and the cache budget follows it down.
5. **In-world with `--vram-budget 3500` at a crowded sim (Sirens Beach):**
   - The cache budget settles at ≈ 1.5–2 GB.
   - `renderGpuMs` has no 100+ ms swings.
   - The `[GpuCache] shrank` lines do not repeat endlessly for the same textures (no churn).
6. Turning Auto off and moving the slider works as before, except it can never exceed the OS
   limit.

### Out of scope for Part A

- Any texture format change (that is Part B).
- The avatar cap reacting to memory pressure. Note it in this spec as a follow-up if Part A shows
  textures alone cannot fit.

---

## Part B: block-compressed textures

### Why

Godot 4.7's Vulkan allocator keeps freed VRAM until a whole block is empty, so the process sits at
its **high-water mark** (BUG-PERF-19). The only lasting fix is a lower peak, and RGBA8 textures are
the biggest item: at a quiet region `[VramBreakdown]` showed 3.78 GB of texture cache plus 2 GB of
textures outside it. Block compression stores a texture in 1/8 (BC1) or 1/4 (BC3) of its RGBA8 bytes.

### B0. What Godot can do in an exported build (checked)

Nothing. The stock 4.7 templates have no texture compressor:

- `modules/etcpak/register_types.cpp` (4.7-stable) sets `_image_compress_bc_func` / `etc1` / `etc2`
  only under `#ifdef TOOLS_ENABLED`.
- `modules/cvtt/config.py` and `modules/betsy/config.py`: `can_build` returns
  `env.editor_build or env["cvtt_export_templates"]` / `env["betsy_export_templates"]`.
- Measured: a GDScript probe exported to a `.pck` and run with
  `windows_release_x86_64_console.exe` from the installed 4.7.stable.mono templates, windowed and
  headless: `Image.compress(COMPRESS_S3TC | BPTC | ETC | ETC2 | ASTC)` all return
  `ERR_UNAVAILABLE` ("Parameter `_image_compress_bc_func` is null"). The same script in the editor
  compresses. `Image.decompress()` of DXT1/BPTC works in the template (bcdec is not editor-only).

So we encode ourselves.

### B1. Encoder: our own BC1/BC3 in C# (`src/SLNG.Assets/BcEncoder.cs`)

- **Algorithm:** stb_dxt's (Fabian Giesen, public domain / MIT; this repository is MIT), written
  anew for spans: table-optimal endpoints for a single-colour block; otherwise the principal colour
  axis by power iteration, its extreme texels as endpoints, projection matching, two least-squares
  refits kept only if they lower the error. Alpha (BC3): both block modes (8 values, or 6 plus exact
  0/255), the smaller error wins.
- **No new package, no `unsafe`, no native code.** Rejected: BCnEncoder.NET (MIT, a NuGet
  dependency plus its own, BC7 far too slow for a worker); Compressonator / ISPC texcomp (a native
  DLL per platform in the export); ImageMagick's DDS writer (already referenced, but a full image
  round trip per level).
- **BC1 for opaque, BC3 for alpha. No BC7.** Godot samples `FORMAT_DXT1` as `BC1_RGB` with alpha
  forced to 1 (`texture_storage.cpp`), so anything with an alpha below 255 goes BC3. BC7 is the
  same size as BC3 (8 bpp); a fast BC7 (mode 6 only) puts alpha on the colour line and loses cutout
  edges, a good one is a mode search an order of magnitude slower. BC3's separate alpha block keeps
  0 and 255 exact.
- **Always 4-colour mode** (c0 > c1): BC1's 3-colour mode's fourth entry is black in Godot's RGB
  view.
- **Measured, Release, one thread** (scratch bench over 400 random textures of the user's decoded
  cache, 357 Mpx): BC1 **30.9 ms/Mpx**, BC3 **28.7 ms/Mpx** -- about 41 ms for a 1024² texture with
  its mips, against 47.6 ms for its full J2K decode. Quality, RGB PSNR over visible texels: BC1
  median **38.5 dB** (p10 31.0); BC3 colour median 39.2 dB, alpha median **49.9 dB** (min 35.0).
  Worst cases looked at 4x: foliage cutouts and grass are indistinguishable; the worst BC1 case
  (21 dB) is a fine fabric-weave **normal map**, visibly noisier. Two refits instead of one cost
  5 % time for +0.2 dB; the projection matcher saved 20 % at the same PSNR.
- **Verified:** xUnit against a decoder written from the format description and against
  ImageMagick's DDS reader (max difference 1 level); Godot's own decoder in the selftest.

### B2. Which textures

| Texture | Compressed | Why |
|---|---|---|
| World faces (diffuse, specular, PBR base colour / metallic-roughness / emissive), particles, terrain, worn (non-bake) avatar textures | yes | the bulk of the cache |
| Normal maps (legacy and PBR) | **no** | the worst measured loss (above); BC5 needs a shader change -- follow-up |
| Avatar bakes and Bakes-on-Mesh (`bakeChannel`) | **no** | skin; ~11 per avatar and protected near; the task's default |
| HUDs | no | drawn at screen scale, text and all -- BC1's blocks would show |
| UI (profiles, icons, map tiles, world map), environment (sun / moon / cloud) | no | few, sharp edges / shader-sensitive |

SL's own viewer has the same switch: `RenderCompressTextures` (settings.xml, default 0,
"EXPERIMENTAL"), driver-side compression of every fetched texture except UI images
(`llviewertexturelist.cpp:1687`) and media (`llviewertexture.cpp:3242`).

Rules: sides must be multiples of 4 (every SL power-of-two texture from 4 up), else RGBA8. The
per-call opt-in is `GetOrUploadTextureAsync(compress: true)`, and the switch
`GpuCache.CompressTextures` (the setting, B6) must be on.

### B3. Pipeline

- **Cold (no compressed copy):** unchanged up to the RGBA8 image on the worker (FixAlphaEdges, LOD
  resize, admission). The alpha numbers (`_alphaModes`, `_alphaMaskable`, `_alphaStats`) are
  measured on those **uncompressed** pixels, as before. Then Godot's `GenerateMipmaps`, then
  `BcEncoder.EncodeChain` over the whole chain, then `Image.CreateFromData(DXT1 | DXT5, mipmaps)`.
  All on the image-prep worker (`RunOnImageWorker`); the main thread only uploads. The format is
  chosen before admission from the decoded pixels' alpha, so admission sizes the texture in its
  real bytes.
- **Store:** the encoded chain plus the alpha numbers go to the decoded disk cache (B5), from a
  clean decode only.
- **Warm:** before asking for RGBA pixels, `AssetService.TryGetCompressedTextureAsync`. If the copy
  is at least as large as the level the screen asks for, the upload is a slice of its mip chain
  (`CompressedTextureData.Slice`): no J2K decode, no RGBA read, no encode. Admission's extra levels
  are slices too. Too small (the camera is closer than on any earlier visit): the cold path, whose
  larger encode replaces the stored copy.
- **Sharpen** (`TryUpgradeCachedTexture`): the same order -- the compressed copy if large enough,
  else RGBA + encode.
- **Shrink** (FEAT-PERF-25 worker path): a compressed texture shrinks by slicing the stored copy
  one level down when there is one; else the RGBA source as today, then encode. Still applied on the
  Refine lane, 2 per frame.
- **Fresh-texture grace** (BUG-PERF-18) and texture animation (UV transforms) are untouched.

### B4. Byte accounting

`TextureAdmission.TextureBytes(w, h, mips, format)`: RGBA8 as before (base + 1/3), BC exactly as
Godot lays it out (`BcLayout.ChainBytes`: each level padded to 4x4 blocks, down to 1x1; a 1024²
chain is 699,064 B in BC1, 1,398,128 B in BC3). Every place that sizes or re-sizes an entry
(admission, commit, sharpen, shrink) uses the format of the image it uploads. Each cache entry
remembers its format; `[GpuCache]` gets `bcMB=` (compressed bytes resident), `[VramBreakdown]` the
same split.

### B5. Disk cache

`DecodedTextureCache`: a new file per texture, `{id}_gpu_v1.dec`, magic `SLGB`, version 1, a
40-byte header (format, mipmaps, maskable, size, source size, alpha mode / min / fractions, payload
length) and the LZ4'd chain. It holds the **largest** level encoded so far (a smaller encode never
replaces it); every smaller level is a slice. Stale when the `.j2c` is newer, deleted on a parse
error and by `Invalidate`, evicted with the rest (`*.dec`). The RGBA entries are unchanged
(version 1); `Invalidate` and the r0 prune now cover every reduce level up to `MaxDiscardLevel`
(they stopped at 3).

### B6. Setting

`GraphicsSettings.CompressTextures` (`compress_textures`, default **on**, presets do not change
it), a checkbox on the Graphics preferences page next to the texture memory, en-US / de-DE strings,
a line in `BENUTZERHANDBUCH.md`. It applies to textures loaded or re-sized from then on.

### B7. Logging

`[GpuCompress]` every 10 s while anything happened (perf sidecar; godot.log under `--diag`):
textures encoded per format, Mpx, encode ms total / per Mpx / max, upload ms per format on the main
thread, warm hits from the disk cache, shrinks by slicing, MB saved against RGBA8.

### B8. Selftests

Compress/upload round trip (Godot decodes our blocks back within PSNR bounds, the alpha numbers
equal the uncompressed ones), accounting per format (entry size == Godot's own data size), disk-cache
round trip and a warm upload with no encode, slice-shrink.

### B9. Acceptance

#### Measured offline (2026-10-15, RTX 4070 12 GB, v0.27.46-alpha code)

A windowed Godot 4.7 probe (scratch, not committed) uploaded the **same 500 textures** from the user's
decoded cache (random sample of the r0 entries, 448 Mpx, 361 opaque / 139 with alpha), each in its own
process: as RGBA8 with mips (today), BC-encoded with mips (cold), and from the stored compressed copies
(warm). Process = PDH `GPU Process Memory(pid_N*)\Dedicated Usage`, Godot = `Performance` monitors.

| | RGBA8 (today) | BC1/BC3 cold | BC1/BC3 warm |
|---|---|---|---|
| Godot textures | 2,283 MB | **344 MB** | 344 MB |
| Godot video total | 2,318 MB | 355 MB | 355 MB |
| process (PDH dedicated) | 2,611 MB | **563 MB** | 563 MB |
| booked by our accounting | 2,280 MB | 341 MB | 341 MB |
| read per texture (worker) | 2.44 ms (RGBA .dec, LZ4) | same | **0.62 ms** (`_gpu_v1.dec`) |
| image + mips per texture (worker) | 8.15 ms | 8.6 ms | 0.08 ms (slice) |
| encode per texture (worker) | -- | **27.6 ms** avg, 125 ms max (30.8 ms/Mpx) | -- |
| upload per texture (main thread) | 0.80 ms | 0.28 ms | **0.19 ms** |
| 500 textures, one thread | 5.8 s | 40 s (once) | **0.5 s** |
| disk for the copies | -- | 206 MB for 500 | -- |

Our booked bytes match Godot's own texture figure to within 1 %, in both formats. Texture VRAM is
**6.6x smaller**; the process 4.6x. A warm start does a tenth of today's CPU work per texture and a
quarter of the main thread's. The cold encode is a one-time cost per texture and level (the copy
serves every smaller level), on the image workers.

Selftest (headless, Godot's own bcdec decoding our blocks): a 256² ramp/disc at 44.1 dB (BC1) and
44.2 / 46.1 dB colour / alpha (BC3); 40 x 512² BC1 fit uncut in the budget that held 5 RGBA8.

#### In-world (open -- needs a login)

- **Same spot before/after:** at one region, `[VramBreakdown]` / `[GpuCache]` / `[Perf]` with the
  setting off, then relog with it on: texture cache MB, `bcMB=`, Godot video MB, process MB.
- **Low VRAM:** `pwsh tools/run-client.ps1 -VramBudget 3500` at Sirens Beach: the texture budget
  should now hold the scene at full or near-full resolution (`[GpuShrink]` quiet, `lodBias=0`),
  `renderGpuMs` without 100+ ms swings.
- **`[GpuCompress]`** every 10 s while loading: encode ms/Mpx, `fromDiskCopy=` high on the second
  visit, upload ms per format.
- **Look at:** cutout foliage and lace edges, text boards and signs at reading distance, skin
  (bakes are not compressed, applier skins on mesh bodies are), shiny surfaces with normal maps
  (unchanged), HUDs (unchanged).

VRAM before/after at the same spot (`[VramBreakdown]`, `[GpuCache]`, `[Perf]`: texture cache MB,
Godot video MB, process MB), encode ms per texture, upload ms, warm-start time; the A5.5 low-VRAM
test (`-VramBudget 3500`) with better visual results; no visible loss on alpha edges, text boards
or skin.

## Sub-tasks / Progress

- [x] A1 policy + tests
- [x] A2 DXGI probe
- [x] A3 `--vram-budget` flag
- [x] A4 wiring, setting, UI, log fields
- [x] A5 acceptance checks reported
- [x] B0 encoder availability checked
- [x] B1 encoder + tests
- [x] B3-B5 pipeline, accounting, disk cache
- [x] B6 setting, UI, manual
- [x] B7-B8 logging, selftests
- [x] B9 offline measurements reported
- [ ] B9 in-world acceptance (same-spot VRAM, `-VramBudget 3500`, visual check)
- Follow-up: BC5 normal maps (needs the shaders to rebuild Z), BC7 for alpha if BC3 shows its limits
- Follow-up (mobile goal): a GPU without BC support makes Godot decompress DXT to RGBA8 on upload
  (`texture_storage.cpp`), so the cache would book 1/8 of what it holds; every desktop target supports
  BC. Mobile needs ETC2/ASTC and a check of `texture_is_format_supported_for_usage` before choosing BC.
