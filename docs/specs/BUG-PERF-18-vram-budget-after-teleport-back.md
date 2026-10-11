# [BUG-PERF-18] After a teleport away and back the texture budget sits at its floor and world textures never arrive

- **Feature ID:** `BUG-PERF-18`
- **Track:** `render/perf`
- **Status:** `🧪 Review` (built v0.27.41-alpha, in-world check open)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md) -- follows FEAT-PERF-17 (real VRAM budget) and FEAT-PERF-25 (low-VRAM caps)

## What was seen

In-world 2026-10-11, RTX 4070 (12 GB), render thread separate: Millenium -> Sirens -> back to Millenium. Fine until the
return. After it `slng-perf.log` read

```
[GpuCache] ... entries=6530 sizeMB=356/256 ... pinnedMB=356 noShrinkMB=157 avatarMB=175 reducibleMB=0 lodBias=2 ... osBudgetMB=8698 osUsageMB=8427
```

and `godot.log` had 3,161 `[LegacyMaterial] normal map ... fetch/decode returned null`, 1,131 for specular maps, and alpha
grass / flower planes drawn untextured -- with no `[TextureFetch]` error behind any of them.

The budget history in `slng-perf.log` (cache 5165 MB at Millenium -> 1180 -> 834 -> 350 MB while `osUsageMB` stayed ~8.4-8.9 GB)
shows the order: leaving Millenium released its textures from the cache, Windows did not take the memory back, and the
policy read `osUsage - cacheBytes` (~8 GB) as non-cache usage.

## Causes

1. **The budget counted memory Godot no longer holds.** Godot's own counters (stats overlay) said 2.6 GB (tex 2087, buf 302) of the
   process's 8.4 GB. The other ~6 GB are freed textures the allocator pools or the driver has not returned -- ours, reusable by the
   next texture, not another program's. `VramBudgetPolicy` took `osBudget - (osUsage - cache) - headroom`, which is negative, and the
   policy's floor was 256 MB.
2. **At that budget every new texture was evicted by its own `Put`.** The 356 MB left in the cache were all pinned, i.e. over the budget,
   so `GpuCache.EvictIfNeeded` disposed every unreferenced entry it found -- the one just uploaded first. An object's texture refs
   are registered in `material.refcount`, a main-thread queue item that runs *after* `BuildFaceMaterialAsync` has awaited the face's
   normal and specular maps. So those maps (and any diffuse texture that arrived first) were disposed before anything referenced them;
   the callback's `IsInstanceValid` check then logged "fetch/decode returned null". It is not a decode failure and not a network one.
3. **A reduce level beyond the codestream** (a smaller issue found on the way): `TryDecodeReduced` gave up and the whole image was
   decoded (4-30x the work, and filed in the decoded cache under the level that was *asked for*); nothing kept a 32x32 texture from
   being reduced to a pixel.

## What changed

- `VramBudgetPolicy.Compute(osBudget, osUsage, godotUsage, cacheBytes, ...)`: non-cache usage = `godotUsage - cacheBytes` (Godot's
  figure, clamped to the OS one so it never lowers the budget below the old reading) + the slack `osUsage - godotUsage` counted up to
  `SlackFraction` (15 %) of the OS budget while `osUsage <= osBudget`, and in full once the OS is over budget (it is then demoting
  us). The old 7-argument overload is kept (no Godot figure = old behaviour). Floor = `FloorFraction` (20 %) of the OS budget, 256 MB
  absolute; a manual cap below it still wins.
- `Boot.UpdateVramBudget` feeds it `Performance.GetMonitor(RenderVideoMemUsed)` -- not a render-thread round trip (FEAT-PERF-13), so
  fine every 2 s.
- `GpuCache`: an entry handed out (Put, or looked up while unreferenced) is not evictable for `FreshGraceMs` (10 s); once referenced
  the grace no longer matters. `[GpuCache]` shows `godotMB=` next to `osUsageMB=`.
- `TextureLod.ClampReduceLevel(reduce, w, h, decompositionLevels)`: at most the codestream's levels, `MaxDiscardLevel`, and no side below
  8 px; used by the reduce decision, the decoder and the shrink source, so the decoded cache is keyed by what was served.
- `[VramBreakdown]` 20 s after every region change (own count, 8 per session) with the cache's pinned vs unreferenced bytes and the scene
  node count.

## Leak check

No leak found. After the return to Millenium scene nodes were 50,801 at +5 s (Sirens still being freed), 22,651 at +15 s and 22,821 at
+60 s -- the first visit's baseline was 23.2k -- and the cache's pinned bytes fell with them. Godot's own `VideoMemUsed` (2567 MB) says
the old textures are freed. What the process still held (~6 GB) is allocator / driver slack, which the budget now treats as such.
The `[VramBreakdown]` after the next region change shows `driver/allocator` beside the pinned bytes to confirm it.

## Tests

- `VramBudgetPolicyTests`: the teleport-back numbers (8698 MB budget, 8427 MB DXGI, 2567 MB Godot, 356 MB cache -> ~4.3 GB, not the
  floor), the same numbers without Godot's figure (proportional floor, not 256 MB), slack within / beyond the allowance, slack in full
  under OS pressure, Godot figure above the OS one, floor vs manual cap, tiny budgets.
- `ReduceClampTests` (SLNG.Assets.Tests): the clamp table, the invariant over sizes x levels x requests, a decode with a level beyond
  the stream, a tiny texture.
- `--selftest` "a fresh unreferenced texture survives its own Put when the pinned ones exceed the budget" (fails without the grace).

## Acceptance (in-world)

Teleport A -> B -> A between two busy regions: textures load normally after the return; `[GpuCache]` budget stays well above the floor
(it should land near `osBudget - Godot's non-cache - 15 % - 10 %`); no `fetch/decode returned null` flood; `[VramBreakdown]` 20 s after
each arrival shows the pinned bytes of the new region only.
