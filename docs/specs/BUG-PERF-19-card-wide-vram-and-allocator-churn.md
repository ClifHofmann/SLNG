# [BUG-PERF-19] After a graphics preset switch the whole card is full and the frame rate collapses

- **Feature ID:** `BUG-PERF-19`
- **Track:** `render/perf`
- **Status:** `🧪 Review` (built v0.27.44-alpha, in-world check open)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md) -- follows FEAT-PERF-17 (real VRAM budget) and BUG-PERF-18 (budget from Godot's own figure)

## What was seen

In-world 2026-10-11 (RTX 4070, 12 GB): 40+ fps on the Ultra preset; after switching to High the viewer fell to 7-8 fps and stayed
there -- `renderGpuMs` 125-274 ms with only ~8.5 k draws, `postFlushMs` following it, i.e. the GPU waiting on memory. Windows at that
moment: the adapter's dedicated usage was 11.3 of 12 GB -- Puris 7.25 GB, Sunshine 2.0, a slicer 1.36, Edge 0.55 -- so the card was full
and paging. DXGI's per-process budget for the viewer still read 9.0 GB (usage 8.4 GB).

Two things together: **(1)** the texture budget only knew the per-process figures. `QueryVideoMemoryInfo`'s `Budget` is a share Windows
will grant when asked, not a statement that the card has the room; three other programs held 4 GB of it and nothing in the per-process
numbers showed that. **(2)** The memory the process holds is not what Godot says it holds (BUG-PERF-18 found 8.4 GB against 2.6 GB after a
teleport), and a graphics switch makes it hold more, not less (below).

## Part 1 -- the whole card

- `AdapterMemoryCounter` (new, `app/scripts`) reads the adapter's total dedicated usage: the performance counter
  `\GPU Adapter Memory(*)\Dedicated Usage` through `pdh.dll` (`PdhAddEnglishCounterW` + `PdhGetFormattedCounterArrayW`), one instance per
  adapter and node (`luid_0x00000000_0x00013E3C_phys_0`), matched by the LUID `DxgiVideoMemory` now exposes. No unsafe code, no new
  package; the formatted-array buffer is walked with `Marshal` (24-byte items, name at 0, status at 8, value at 16 -- checked against the live
  counter). It runs on the thread pool, one request at a time and at most every 4 s; anything that fails (counter missing, no
  access, not Windows) switches it off for the session with one `[VramBudget]` line and leaves the budget exactly as before. A reading older
  than 15 s is ignored.
- `VramBudgetPolicy.CardBudget(dedicated, adapterUsage, osUsage)` = 92 % of the dedicated memory minus what every other program holds
  (`adapterUsage - osUsage`). `Compute(...)` has a new overload that takes the two adapter figures; the target becomes
  `min(per-process target, CardBudget - non-cache)`, never below the floor, smoothed like before (a fall applies at once, a rise is
  capped at +10 % per 10 s). Boot feeds it the largest "others" of the last 30 s (`RollingMinimum` on the negated value), so a program that
  starts using the card lowers the budget at once and one that leaves raises it only after the reading has aged out.
- **Why "projected", not "the present overflow".** The task text said to lower the budget by how far the adapter is over 92 %. That is
  zero until the card is already full, and subtracting it from a target the cache has not reached changes nothing. The ceiling above is
  measured at the target: it holds the cache back while the card still has room. In the regime the cache sits at its budget the two are
  the same number plus the headroom the per-process maths already keeps.
- **Why it cannot chase its own tail.** `CardBudget` uses the process's usage and the others' usage, not the cache's size, so when the
  cache shrinks and the allocator keeps the memory (below) nothing in it changes and the budget does not fall again on the next tick
  (unit test `Memory_the_process_keeps_after_the_cache_shrinks_does_not_drive_the_card_budget_down_again`). Slack still counts up to 15 % of
  the OS budget exactly as in BUG-PERF-18.
- Numbers of the incident (`VramBudgetPolicyTests`): OS budget 9.0 GB, process 7.25 GB, Godot 5.4 GB, cache 3.5 GB, adapter 11.3 of 12 GB ->
  texture target 5112 MB per process, **3976 MB with the card** (-1136 MB). A card with room (6 of 12 GB, others 2 GB) is untouched.
- `[GpuCache]` stats line: `adapterMB=<all programs>/<dedicated> cardMB=<CardBudget>` (`n/a` without a reading). A state-change line
  `[VramBudget] the whole card is the limit: ...` / `... has room again` goes to godot.log and the perf sidecar.
- `[VramBreakdown]` now also prints the whole card and the others' share.

## Part 2 -- what the process keeps (measured)

### How it was measured

`tools/vram-probe` (standalone GDScript project, windowed Vulkan Forward+, same engine as the viewer, Godot 4.7-stable mono) prints, after
every step, Godot's `RENDER_VIDEO_MEM_USED` against the process's *Dedicated Usage* from the Windows counter (what DXGI
`QueryVideoMemoryInfo` reports for the process) and the whole adapter. RTX 4070, 12 GB, other programs on the card (~3.9 GB).
Raw output: `side-session-notes/.../BUG-PERF-19-probe-results.md`.

### Godot 4.7's allocator, from its source

`drivers/vulkan/rendering_device_driver_vulkan.cpp` (4.7-stable): one `VmaAllocator` with `VmaAllocatorCreateInfo` left at its defaults
(no `preferredLargeHeapBlockSize` -> VMA's 256 MiB blocks for large heaps, smaller first blocks), plus one VMA pool per memory type for
allocations <= 4096 bytes (`SMALL_ALLOCATION_MAX_SIZE`, `blockSize = 0`, `minBlockCount = 0`). Textures and buffers go through
`vmaCreateImage` / `vmaCreateBuffer` with `VMA_MEMORY_USAGE_AUTO_PREFER_DEVICE`; there is no dedicated-allocation flag, VMA decides.
`get_total_memory_used()` -- what the stats overlay, `Performance RENDER_VIDEO_MEM_USED` and `RenderingServer.VideoMemUsed` show -- is the
sum of `vmaGetHeapBudgets()[i].statistics.allocationBytes` over **all** heaps (host-visible ones too): the bytes *allocated*, not the bytes
of the blocks VMA holds. The difference between the two is what the process keeps. There is no project setting and no `RenderingDevice`
method to trim, defragment or size the pools (the 4.7 class reference lists only `get_memory_usage` and the `get_device_*` /
`get_driver_*` report calls, which need `--extra-gpu-memory-tracking`).

### Results

**Texture churn** (600 textures, 200 x 1024^2 + 400 x 512^2 with mips, interleaved with ~450 small meshes, ~16 MB of buffers):

| step | Godot | process | kept |
|---|---|---|---|
| 600 textures + meshes | 1632 | 1861 | |
| half the textures freed | 831 | 1861 | 1030 |
| survivors SetImage 256^2 (shrink), x3 cycles | 131 | 1861 | 1730 |
| survivors SetImage back to 512/1024 (sharpen), x3 | 831 | 1861 | |
| all textures freed, meshes kept | 31 | **1861** | 1830 |
| 600 textures allocated again | 1632 | **1861** (no growth) | |
| textures freed again, then **meshes freed** | 16 | **197** | |

The blocks that held textures go back to the OS only when *nothing* lives in them. About 16 MB of small long-lived buffers spread through the
blocks kept 1.7 GB; the instant they were freed the process fell to its baseline. The kept memory is reusable (the second round of
textures cost nothing), so it is a high-water mark, not a leak. `ImageTexture.SetImage` with a new size (the shrink / sharpen of
`GpuCache`) does **not** grow the process across repeated cycles: the freed storage is reused at once. So that churn is not a source.

**Render buffers** (a 2560 x 1440 viewport: MSAA 8x, SSAO, SSIL, SSR, glow, 2-split sun shadow; Ultra <-> High = MSAA 4x, no SSIL):

| | Ultra | -> High | -> Ultra | -> High | -> Ultra |
|---|---|---|---|---|---|
| Godot | 980 | 614 | 980 | 614 | 980 |
| process, switched **directly** | 1272 | 1324 | **1784** | 1324 | 1784 |
| process, through a **buffer-free state** (8 frames) | 1272 | 812 | **1272** | 1068 | 1272 |
| process, directly, after the texture churn (1861 MB held) | 2360 | 2156 | **2616** | 2156 | 2616 |
| process, buffer-free, after the texture churn | 2360 | 1900 | **2361** | 1900 | 2361 |

Switching directly keeps what Godot freed (High: Godot -366 MB, process +52 MB) and the set that comes back is placed beside the
blocks the old one sat in: **+512 MB** for the same Ultra (+256 MB with the churn slack present), once per distinct pair of settings,
stable afterwards. (The mechanism -- old and new set alive at the same time in the frame of the switch, or blocks pinned by the new set's
smaller targets -- was not separated; the measured effect is what the fix relies on.) Going through MSAA off + every buffer-owning
post effect off for a few frames lets the old set be freed and the blocks returned first: the process then follows Godot. The slack left by
the textures is reusable for roughly half of the render buffers (+970 MB in Godot, +499 MB in the process), not for all of them. At the
login screen the world is not rendered, so no 3D buffers exist (the in-app run showed Godot constant at 165 MB across all presets); the
numbers above are from the standalone probe.

### What changed

- `RenderBufferSwitch` (new, `SLNG.Core`, unit-tested): when `Boot.ApplyGraphicsSettings` would change what the render buffers are made of
  (MSAA, SSAO, SSIL, SSR, glow, the shadow atlas -- `GraphicsSettings.PredictedBufferSignature()` against the applied one), it first puts
  the buffer-free state in the engine (`GraphicsSettings.ApplyBufferFreeState`: MSAA off, SSAO/SSIL/SSR/glow off), waits 8 frames, and only then
  applies the real options. A second change during the wait restarts the wait; options that do not touch the buffers (draw distance, a
  slider) apply at once. The cost is ~8 frames without MSAA / post effects after a preset change. `--no-buffer-release` switches directly
  again, for an A/B in-world. A `[VramBuffers]` line says when it happens.
- Engine no-ops confirmed in the 4.7-stable source: `Viewport::set_msaa_3d` and `LightStorage::directional_shadow_atlas_set_size` return
  early when the value is unchanged (the latter frees the shadow depth texture only when the size or bit depth really changes), so
  `GraphicsSettings.Apply` (called on every slider move) re-creates nothing when no buffer-relevant option moved. No change needed there.
- `[VramBreakdown]` runs 10 s after a preset switch (own count, 8 per session) and prints, for every breakdown, what Godot and the process
  did *since* the moment the change was applied (graphics) or the previous breakdown (teleports): `Godot -366 MB, process +52 MB (kept by the
  process: 418 MB of the 366 MB Godot freed)`. After a teleport A -> B -> A the lines at +20 s show the same for the region change.

### What cannot be reduced here

- The allocator's block pinning. Godot ships no pool, trim or defragment control for VMA and the project uses the stock export templates;
  a long-lived small buffer anywhere in a 256 MiB block keeps the block. A custom engine build with a smaller `preferredLargeHeapBlockSize`
  or a separate pool for textures would shrink it; that is an engine fork, not a viewer change.
- Therefore the process stays at its high-water mark after a teleport or a quality drop, and nothing the cache frees returns to other
  programs. The remedy is to not let the high-water mark rise on a shared card (Part 1) and to not raise it by switching (Part 2), not to
  expect the memory back.
- `RenderVideoMemUsed` counts host-visible heaps too and only allocated bytes; "process - Godot" is block slack plus the driver's own
  overhead (a few hundred MB on this card) and cannot be split further without `--extra-gpu-memory-tracking`.

## Tests

- `VramBudgetPolicyTests`: card budget arithmetic and the unknown-figure case, the incident's numbers, a card with room, no reading =
  old behaviour, a full card cannot go below the floor, a manual cap still wins, no cascade when the process keeps freed memory, a fall applies
  at once and a rise is capped, an adapter reading below the process's own usage counts nobody else.
- `RenderBufferSwitchTests`: first apply, an unrelated change applies at once, a preset switch releases then applies once, a second change
  restarts the wait, the wait ends exactly once, the signature separates what matters.
- `--selftest` "dxgi video memory probe": the whole-card counter reads at least this process's usage and an unknown LUID switches it off
  instead of throwing.

## Acceptance (in-world)

With Sunshine (or another GPU program) and the viewer on the same card: Ultra -> High -> Ultra on a busy region; fps does not collapse,
`[GpuCache] ... adapterMB=` stays under ~92 % of the dedicated memory (`cardMB` is the ceiling it works to), the `[VramBuffers]` line appears per
switch and the `[VramBreakdown] ... since the graphics change` line 10 s after it shows the process following Godot down. Compare with
`--no-buffer-release` if the staged switch is suspected of anything.
