# [FEAT-NET-04] Persistent object cache (the reference viewer's `ObjectUpdateCached` protocol)

- **Feature ID:** `FEAT-NET-04`
- **Track:** `net`
- **Status:** `🚧 In Progress`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Follows:** `BUG-NET-21` (which keeps a left region's terrain and re-requests its objects by id)

## Overview & Goal

The reference viewer keeps the objects it has seen, per region, on disk. When it enters a region
the simulator does not send the objects again: it sends a short *probe* for each cacheable object
(id + CRC), the viewer recognises the ones it holds, builds them from disk without any network
traffic, and asks only for the rest. SLNG has no cache at all, and it tells the simulator so
(`RegionHandshakeReply` flags `0x7`, bit 1 = "my cache is empty"), so every arrival pays the full
stream: measured on OSGrid, 5439 objects take about 60 s to arrive on every visit, and a return to
a region costs the same as a first visit.

Goal: a cache that makes the second arrival in a region (this session or a later one) show its
static content at once, on the simulators that speak the protocol -- Second Life and OpenSim with
`SupportViewerObjectsCache` (default `true`, `LLUDPServer.cs:352`).

## How the reference viewer does it (read from `scratch/slviewer`, not from memory)

| Step | Where | What |
|---|---|---|
| Handshake | `llviewerregion.cpp:3201-3222` | `RegionHandshakeReply` flags: bit 0 = "send all cacheable objects" (`sVOCacheCullingEnabled`), **bit 1 = "my cache is empty, don't probe"**, bit 2 = self appearance. The cache is loaded from disk *before* the reply (`loadObjectCache`, line 781), keyed by region handle + the region's **CacheID** from the handshake. |
| Probe | `llviewerobjectlist.cpp:696-741`, `llviewerregion.cpp:2864-2919` | `ObjectUpdateCached` carries `(ID, CRC, UpdateFlags)` per object. Same CRC: hit, the object is built from the stored data. Different CRC: miss, type `CRC`. Not held: miss, type `TOTAL`. |
| Ask | `llviewerregion.cpp:2926-2960` | All misses in one go, `RequestMultipleObjects`, 255 blocks per message, each with its `CacheMissType`. The simulator answers with the full state. |
| Store | `llviewerobjectlist.cpp:521-560` | A full or terse update refreshes the entry (`updateCacheEntry`). Stored: the object's packed state, its local id, its CRC, its update flags. |
| Save | `llviewerregion.cpp:806` | Written when the region goes away; entries not seen for a long time are dropped. |
| Regions | `llworld.cpp:144-177` | A region the viewer still holds, same host and alive, is reused as it is -- which is what makes a quick return work without a cache at all. |

## What OpenSim does with it (`OpenSim/Region/ClientStack/Linden/UDP/LLClientView.cs`, `ScenePresence.cs`)

- `HandlerRegionHandshakeReply` stores the reply flags when the simulator supports the cache
  (line 8995-9006); `GetViewerCaps` hands them to `ScenePresence.SendInitialData` (line 13422).
- `SendInitialData`: cache flag *empty* -> full updates; otherwise cacheable groups get
  `SendUpdateProbes` (`ScenePresence.cs` ~4095).
- Updates for cacheable groups already go out as **`ObjectUpdateCompressed`** whatever the flags --
  83 % of what Millenium streamed (4991 of 6045 per full visit) is compressed. A cache filled from
  compressed blocks is therefore filled even on the first visit.
- `RequestMultipleObjects` is answered with a full update for any local id it knows
  (`Scene.PacketHandlers.cs`, `RequestPrim`).
- `cached=0` in every `[RegionLoad]` line so far is consistent with our reply saying "empty".

## What LibreMetaverse 3.1.6 does, and does not

- `RegionHandshakeHandler` (NetworkManager.cs:1399) fills the simulator's fields and sends the
  reply with **`Flags = 0x1 | 0x2 | 0x4`** -- hard-coded, no setting. It stores the region's
  CacheID as `Simulator.ID` and the region id as `Simulator.RegionID`.
- `ObjectUpdateCachedHandler` (ObjectManager.PacketHandlers.cs:928): with
  `World.AlwaysRequestObjects` (default `true`) it asks for **every** cached object; with `false`
  it does nothing, which is what we want once the probe is ours.
- `ObjectUpdateCompressedHandler` decodes a compressed block into a `Primitive` and raises the
  ordinary events (the same path our `OnObjectUpdate` already converts). Block layout:
  `FullID(16) LocalID(4) PCode(1) State(1) CRC(4) Material ...`, CRC at offset 22.
- `PacketEvents` is `internal`; packets cannot be injected through a public API. Replaying a cached
  block means calling the protected handler by reflection, once, cached as a delegate.

## Design

### Layers (AGENTS.md: dependency direction is fixed)

- `SLNG.Net` owns everything here. The store is plain bytes and integers, **no LibreMetaverse type
  on its public surface**: `(ulong regionHandle, Guid cacheId)` -> entries of
  `(uint localId, uint crc, uint updateFlags, byte[] compressedBlock)`.
- `app` only supplies a directory (like `GridSession.UseCacheDirectories`) and the on/off setting.
- Nothing in `SLNG.Core`: the world model does not know there is a cache; replayed blocks arrive as
  the ordinary `ObjectUpdateEvent`.

### Components

1. **`ObjectCacheStore`** -- per region: `Probe(localId, crc)`, `Put(localId, crc, flags, block)`,
   `Remove`, entry count, LRU stamp. In memory first (phase 1); the file format comes in phase 2.
2. **Handshake takeover** -- LibreMetaverse's reply says "cache empty" unconditionally. Replace its
   callback (`UnregisterCallback` on the handler fetched by reflection) with our own that does what
   it does and sends the reply with the real flags: bit 0 set, bit 1 set **only if the cache for
   this region is empty**, bit 2 set.
3. **Probe handler** -- on `ObjectUpdateCached`: for each block look the object up. Hit: build the
   `ObjectUpdateCompressed` packet from the stored block and the probe's update flags and hand it to
   LibreMetaverse's handler. Miss: collect, then send `RequestMultipleObjects` in chunks of 255 with
   the miss type. Runs with `World.AlwaysRequestObjects = false`.
4. **Write path** -- every `ObjectUpdateCompressed` block we see: parse `LocalID` and `CRC`, `Put`.
   (Full `ObjectUpdate` and terse updates are not stored in phase 1: they cannot be replayed without
   re-packing the object, which the viewer does and we do not need to start with.)
5. **Persistence** (phase 2) -- one file per region under the cache directory, versioned header,
   atomic write, total size budget with least-recently-used regions dropped first, written when a
   region is left and at logout.
6. **Recovery hook** (phase 3) -- `BUG-NET-21`'s id re-request answers from the cache where it can
   (no CRC to check there: the simulator believes we have these), and asks the simulator only for
   the rest.

### Safety

- **Kill switch:** preference `ObjectCacheEnabled`, plus a "clear cache" action. Off = today's
  behaviour exactly (reply flags `0x7`, `AlwaysRequestObjects = true`).
- **A cache must never make the world wrong.** A block that does not decode, a CRC that does not
  match, a cache id that changed (the region was reset): the entry is dropped and the object is
  requested. Nothing is replayed without a probe that matched.
- **TPV policy:** the cache stores what the simulator already sent to this viewer, for this viewer's
  own rendering, in the format the reference viewer itself persists; it is not an export path and
  nothing reads it except the probe handler. Kept out of the user-visible asset/inventory surfaces.
- **Threading:** probes arrive on the network thread and are answered there (LibreMetaverse's
  handler already runs there); the store is guarded by a lock per region; disk I/O is on a worker.

## Acceptance Criteria

- [ ] Phase 1: with the cache on, a second arrival in a region shows `cached>0` in `[RegionLoad]`
      and the objects it held appear without being streamed again; `[Cache]` lines give hits,
      CRC misses, total misses and requests sent.
- [ ] Phase 1: with the cache off, packet-for-packet the same behaviour as before (reply flags,
      `AlwaysRequestObjects`).
- [ ] Phase 2: the cache survives a restart; a region visited yesterday arrives from disk.
- [ ] Phase 2: the size budget holds; a corrupt or foreign file is ignored, never fatal.
- [ ] Phase 3: a return to a region answers BUG-NET-21's re-request from the cache.
- [ ] Unit tests for the store, the block parser, the probe decision, the chunking, the file format.
- [ ] Measured in-world against Millenium (OpenSim) and, separately, a Second Life region: arrival
      time and bytes received with and without the cache.

## Technical Specs & Affected Files

- `src/SLNG.Net/ObjectCache/ObjectCacheStore.cs`, `CompressedObjectBlock.cs` (new)
- `src/SLNG.Net/GridSession.ObjectCache.cs` (new): handshake takeover, probe handler, write path
- `src/SLNG.Net/GridSession.ObjectRecovery.cs`: phase 3
- `app/scripts/Boot.cs` (cache directory, preference), a settings entry
- `tests/SLNG.Net.Tests/ObjectCache*Tests.cs`

## Open Questions

- Does the SL simulator also want `0x1` ("send all cacheable objects") to probe, or does it probe
  whatever the flags say? Only the viewer side is visible to us; first check is in-world on Agni.
- Replaying a block changes nothing in `Primitive` that a later terse update relies on? (LMV keeps
  per-`Simulator` dictionaries; the replayed block fills them exactly as a wire block would.)
- Cache id changes on region restart on OpenSim? If it does not, a CRC is all there is to trust.

## Sub-tasks / Progress

- [x] Read the viewer, OpenSim and LibreMetaverse sides (this document)
- [x] Phase 1 (`v0.24.110-alpha`): store, block parser, probe handler, write path, kill switch (`--no-object-cache`). **Handshake:** not a takeover of LibreMetaverse's handler after all -- it has already replied `0x7` when our handler runs, so when the cache is not empty a **second** reply with `0x5` follows. OpenSim keeps the latest flags and reads them a few heartbeats later in `SendInitialData`; whether the SL simulator does is the open question above.
- [x] Phase 2 (`v0.24.111-alpha`): `ObjectCacheFile` (versioned, checksummed, all-or-nothing), `ObjectCacheDisk` (atomic swap, 512 MB budget by age, older cache ids dropped), loaded before the handshake reply, written when a region is left, every 5 minutes while it changes, and at teardown. Files: `user://cache/objects/<handle>-<cacheId>.slobj`.
- **Measured with `v0.24.111` (log in `scratch/logs/`):** cache read from disk (2387 objects) and declared non-empty (`0x5`), yet `cached=0`, `hit=0`, no `first probe` line -- **neither OSGrid region (Secret Love, Millenium) probes.** Either they do not support the viewer cache, or they read the flags before our second reply. So the cache cannot be checked per object there.
- [x] Phase 3 (`v0.24.112-alpha`): **optimistic restore** for simulators that do not probe. Three seconds after the handshake, if no probe came: replay what is held (nearest first, attachments never cached), ask the simulator for every one of those objects, and once its answers have stopped (8 s flat, at least 10 s, never on a timeout) remove what it never answered for. Also: the non-empty handshake reply is sent three times (now, +0.5 s, +1 s) in case ordering is what hid it.
- [ ] In-world: second login shows `has not probed: showing N objects from the cache`, the scene at once, then `N of M cached objects confirmed ... K gone and removed`. Open: Agni (probes expected), BUG-NET-21's id re-request answered from the cache, a settings entry and a clear-cache action.
