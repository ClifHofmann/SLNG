# [FEAT-PERF-17] Real VRAM budget (Part A) and GPU-compressed textures (Part B)

- **Feature ID:** `FEAT-PERF-17`
- **Track:** `render` / `assets`
- **Status:** `⏸️ Pending`. Part A goes to agy; Part B is built later in a separate session.
- **Owner:** `gemini` (Part A)
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

## Part B: block-compressed textures (later, separate session)

Outline only; the session that builds it refines this section first.

- **Encoding:** compress on workers. BC1 for opaque, BC3 or BC7 for alpha, mipmaps compressed
  too.
  - Check what Godot 4.7 can do at runtime in an **exported** build: `Image.Compress` depends on
    modules that may be editor-only.
  - Otherwise use a C# or native BC encoder. Cite it, justify it, and check the licence is
    compatible.
- **Upload and cache:** upload the compressed formats. Store the result in the decoded disk cache
  (FEAT-PERF-11) as a new versioned format, so a warm start uploads without re-encoding.
- **Must stay correct:**
  - `AlphaStats` and `_alphaModes` (measured before compression)
  - avatar bakes and Bakes-on-Mesh
  - texture animation
  - shrink and sharpen
  - per-format byte accounting in `GpuCache`
- **Setting:** a switch to disable compression.
- **Acceptance:** VRAM before and after at the same spot, and the A5.5 low-VRAM test again with
  better visual results. No visible loss on alpha edges or skin.

## Sub-tasks / Progress

- [ ] A1 policy + tests
- [ ] A2 DXGI probe
- [ ] A3 `--vram-budget` flag
- [ ] A4 wiring, setting, UI, log fields
- [ ] A5 acceptance checks reported
- [ ] Part B (separate session)
