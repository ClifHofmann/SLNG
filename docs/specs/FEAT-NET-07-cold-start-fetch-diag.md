# [FEAT-NET-07] Cold-start asset fetch: diagnostics, `--tex-slots`/`--mesh-slots`, and mesh over our own HTTP client

- **Feature ID:** `FEAT-NET-07`
- **Track:** `net` / `perf` (diagnostics only; no change to a normal session)
- **Status:** `🧪 Review`
- **Owner:** `claude`

## Why

The first visit to a region (empty asset and object cache) takes minutes for the objects to arrive. A warm-cache arrival is
bound by the main-thread work queue (measured 2026-10-30, 79 avatars: queue 22k -> 46k, 65 s to drain, `avatar.face_material`
about half of the queue time), so the cold case needs its own numbers: which fetch lane is slow, and is the HTTP stack
silently capping us below the slot count we think we have.

The reference viewer: textures 8 connections (up to 12) with pipelining (`llappcorehttp.cpp:70`), `Mesh2MaxConcurrentRequests`
8 pipelined with a queue high water of 32-100 (`settings.xml:10350`, `llmeshrepository.cpp:352,4468`). Ours: 8 texture slots
(`GridSession.Assets.cs`), a `PriorityGate(32)` in front, materials 6, meshes through LibreMetaverse with no gate of our own.

## What it adds (all under `--diag`)

- `SLNG.Core.FetchPipeStats` (pure, tested): requests, open, peak open, completed, failed, bytes, p50/p95 latency of the
  interval. `FetchPipeReporter` prints one line per pipe every 5 s, silent when idle.
- `[MeshPipe]` (LibreMetaverse `RequestMeshAsync`: its `peak` is the concurrency the stack really gives us), `[MatPipe]`
  (material assets incl. the UDP fallback), `[TexHttp]` (texture transfer once a slot is held), `[TexWait]` (waiting for a
  slot: `inflight` = queue length, p50/p95 = wait).
- `[TexPipe]` every 5 s while there is traffic, instead of every 200 requests (unchanged cadence without `--diag`).
- `--tex-slots=N` (`tools/run-client.ps1 -TexSlots N`): HTTP texture slots, default 8; the admission gate becomes
  `max(32, 4N)`. Ignored without `--diag`.

## Reading a cold run

- `[TexWait] inflight` large with p95 wait in seconds, `[TexHttp] peak` = the slot count: slots are the limit -> try `-TexSlots 12`.
- `[TexHttp] peak` < slots: demand or the object stream is the limit, not the slots.
- `[MeshPipe] peak` small (1-4) with growing p95: LibreMetaverse serialises mesh fetches; the fix is our own gate/client.
- `[MeshPipe] failed` / `[MatPipe] failed` growing: retries/timeouts.

## Measured cause (cold visit, 2026-10-31, ~11 min, two regions)

`[MeshPipe]`: 8,876 requests, up to 3,772 open, completions a constant ~50 per 5 s (10/s) whatever the size, median wait up
to 400 s, 192 failures. `[TexHttp]` (own client, same servers): 20-47 a second at 8 slots, median 20-80 ms. So the limit was
LibreMetaverse's download queue / caps rate limiter (12 tokens a second in the v3.0.2 source; the pinned 3.1.6 was not read),
not the sim. The reference viewer's mesh path is bound by concurrent connections instead (8 pipelined, queue 32-100:
`llappcorehttp.cpp:70`, `llmeshrepository.cpp:352`).

## The fix

`GridSession.FetchMeshDataAsync` fetches `{GetMesh cap}?mesh_id={id}` (`SLNG.Core.MeshFetchUrl`) with the shared HTTP client:
60 s per transfer, two attempts for 408/429/5xx/timeouts, 403/404/410 remembered by `AssetRefusals` (as for materials).
`AssetService` bounds the concurrency with a `PriorityGate` of 16 (`--mesh-slots=N`, `-MeshSlots N`, `--diag` only), the seat
a focus-avatar priority can later use. LibreMetaverse's own mesh cache is no longer consulted; SLNG's `.mesh` disk cache is.

## Verify

Same cold run as before (rename `cache` and `grids/<grid>/cache/objects`, `run-client.ps1 -Diag`, ~3 min at a busy place):
`[MeshPipe] peak` should now reach the slot count and `+N done` per interval should be far above 50.

## Counters for the paths that still go through LibreMetaverse (follow-up)

No behaviour change; the same `FetchPipeStats` lines every 5 s under `--diag`, so a cold run says whether any of them queues the way the
mesh fetch did:

- `[LegacyMatPipe]` -- one request is one `RenderMaterials` POST (up to 50 ids) through `HttpCapsClient`, i.e. LibreMetaverse's caps rate
  limiter. "done" = the batch returned at least one material; the byte column counts materials.
- `[NamePipe]` -- one request is one `GetDisplayNamesAsync` call (a batch of ids).
- `[WearablePipe]` -- the local avatar's wearable assets (`RequestAssetAsync`, appearance/bake at login).

Read them like `[MeshPipe]`: a small `peak` with a growing p95 means a queue in front of the request; then that path gets its own client
and slot cap the way meshes did.
