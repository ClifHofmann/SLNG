# [BUG-RENDER-47] Prims keep a wrong shape until right-clicked

- **Feature ID:** `BUG-RENDER-47`
- **Track:** `render` / `net`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
At Sirens (Agni) with the object cache enabled (`FEAT-NET-04`), several prims rendered with incorrect geometry until right-clicked (selected). Most prominently:
- Chalkboard text displays made of small glyph prims (XyzzyText / FURWARE style, sliced/hollowed prisms) appeared as rows of grey sawtooth triangles (`/\/\/\/\`).
- Grey triangular shapes lying flat on boardwalks.
- Right-clicking selects the object via `ObjectSelectionController` -> `_session.SelectObject`, triggering an uncompressed `ObjectUpdatePacket` from the simulator that rebuilt the visual with correct shape parameters.

### Root Cause
1. Display glyphs are initially rezzed by scripts as default uncut equilateral triangles (`ProfileCurve = 0x23` or `0x03`, `Cut = 0..1`, `Hollow = 0`) before script configuration via `llSetPrimitiveParams` sets the final sliced shape (`ProfileBegin = 0.2`, `ProfileEnd = 0.8`, `ProfileHollow = 0.3`).
2. The initial rez was received via `ObjectUpdateCompressedPacket` and cached to `.slobj` by `FEAT-NET-04`.
3. Subsequent configuration arrived as uncompressed `ObjectUpdatePacket`. `GridSession` had no invalidation for `_objectCache` on uncompressed updates, leaving the initial default shape in `.slobj` indefinitely.
4. On subsequent logins / region revisits, `ObjectUpdateCached` probe or optimistic restore matched the stale cached default shape, rendering sawtooth triangles or boardwalk triangles until a right-click forced an uncompressed update.

## Fix
1. **Invalidate Object Cache on Uncompressed Updates:**
   - Added `ObjectCacheStore.Remove(RegionKey, uint localId)` to discard stale entries when uncompressed `ObjectUpdate` packets arrive.
   - Wired invalidation in `GridSession.Objects.cs` upon receiving uncompressed updates.
2. **Untrusted / Incomplete Shape Detection (`CompressedObjectBlock.IsUntrustedShape`):**
   - Rejects unpopulated volume params (`pathCurve == 0`, `pathScale == 0`).
   - Rejects declared hole types with 0 hollow (`(profileCurve & 0xF0) != 0 && profileHollow == 0`).
   - Rejects default uncut unhollowed equilateral triangles (`profileCurve & 0x0F == 3 && profileBegin == 0 && profileEnd >= 49500 && profileHollow == 0`).
   - Rejects persisting such untrusted shapes in `CacheStore`.
   - In `CacheProbeReceived`, probe hits matching untrusted shapes are purged from the cache and treated as `CacheProbe.TotalMiss`, triggering full `RequestMultipleObjects` from the simulator.
   - In `RestoreFromCacheAsync`, untrusted shapes are skipped, removed from the cache, and requested via `RequestMultipleObjects`.
3. **Preserve Hole Type High Nibble:**
   - In `GridSession.Objects.cs`, passed raw `shape.ProfileCurve` to preserve hole shape instead of masked curve.
4. **Diagnostics Logging on Select:**
   - In `ObjectSelectionController.cs`, added diagnostics log (`[SelectDiagnostic]`) comparing pre-selection shape parameters with post-update shape parameters when `Diagnostics.Enabled` or `SLNG.Core.Diag.Verbose` is active.

## Acceptance Criteria
- [x] Cache invalidation implemented on uncompressed updates.
- [x] Untrusted / incomplete shapes detected and treated as misses, purged from cache, and requested via `RequestMultipleObjects`.
- [x] Diagnostics-gated log on select in `ObjectSelectionController`.
- [x] Unit tests for `IsUntrustedShape` and `ObjectCacheStore.Remove` passing.
- [x] In-world: logging into Sirens displays chalkboard glyphs as flat text strips without requiring a right-click.

## Technical Specs & Affected Files
- `src/SLNG.Net/CompressedParticleRepair.cs`
- `src/SLNG.Net/ObjectCache/CompressedObjectBlock.cs`
- `src/SLNG.Net/ObjectCache/ObjectCacheStore.cs`
- `src/SLNG.Net/GridSession.ObjectCache.cs`
- `src/SLNG.Net/GridSession.ObjectRestore.cs`
- `src/SLNG.Net/GridSession.Objects.cs`
- `app/scripts/ObjectSelectionController.cs`
- `tests/SLNG.Net.Tests/PrimShapeRoundTripTests.cs`
- `tests/SLNG.Net.Tests/ObjectCacheTests.cs`
