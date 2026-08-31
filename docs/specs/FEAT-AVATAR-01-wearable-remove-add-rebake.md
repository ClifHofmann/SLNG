# [FEAT-AVATAR-01] System Wearables — Remove / Add + Self-Rebake

- **Feature ID:** `FEAT-AVATAR-01`
- **Track:** `net` / `render`
- **Status:** `⏸️ default-off, opt-in experiment` (`v0.11.30-alpha`). Default: remove/add is a
  logged **no-op** — every "prepare it ourselves" approach corrupted the stored appearance on
  OSGrid (see the ABANDONED table below). **`SLNG_APPEARANCE_SYNC=1`** flips
  `Settings.Agent.SendAppearance` on, so LibreMetaverse **3.1.3** runs its full appearance
  pipeline from login (download all worn wearables, keep the 218 params current, bake +
  `AgentSetAppearance`) — the same thing Firestorm does. The 2026-08-02 corruption was on LMV
  **3.0.0**, before the big appearance/bake rework; 3.1.3 may build correct params now. **If it
  doesn't, the first login corrupts the stored shape** — `LogVisualParamHealth()` logs the
  outcome (`[VisualParams]` line) and `OnAvatarAppearance` prints a loud WARNING if
  `MyVisualParameters` reads unhealthy against a good sim relay. Test on a throwaway alt / with
  Firestorm ready.
- **Owner:** `claude`
- **Agent:** `protocol-re` (net side) → `graphics-engineer` (bake/render side)
- **Dep:** `M4-3` (Appearance & BoM)

## Problem
Taking a **system wearable** off (or putting one on) in SLNG does nothing visible. Found while
verifying `M4-7`: the user removed an "Apollo Meshbody Full Alpha" layer and the system body
stayed hidden — because the change never reached the grid.

Two independent causes:

1. **`GridSession.DetachItemAsync` only detaches attachments.** It resolves candidate UUIDs and
   calls `_client.Appearance.Detach(u)` for each — that sends `DetachAttachmentIntoInv`, which
   the simulator applies only to *attachments*. A wearable (`AssetType.Clothing` /
   `AssetType.Bodypart`, e.g. Alpha, Skin, Shape, Tattoo, Universal) is not an attachment, so
   the packet is a no-op and the layer stays worn server-side. `AttachItemAsync` has the mirror
   gap on the add side.

2. **`SendAppearance = false`.** Set in `GridSession` ctor (see the long note at
   `GridSession.cs:243–273`) after the 2026-08-02 "avatar squat & deformed, in Firestorm too"
   incident: `Appearance.MyVisualParameters` was empty, so every `AgentSetAppearance` SLNG sent
   replaced the real shape with defaults. With the flag off, LibreMetaverse never computes a
   client-side bake, never uploads one, and never sends `AgentSetAppearance`.

Net effect: the baked textures the renderer holds are frozen at whatever the grid had when
SLNG logged in. `AvatarRenderer.UpdateVisual` step 3 (`anyBakeChanged`) never fires post-login
for the self avatar.

## Goal
Removing or adding a system wearable in SLNG updates the avatar within a few seconds — the
system body/head shows or hides to match — without a relog, and without regressing the shape
(the `SendAppearance` incident must not recur).

## Findings — LibreMetaverse 3.1.3 (confirmed against the pinned package)

Reflected the exact NuGet 3.1.3 assembly (`~/.nuget/.../libremetaverse/3.1.3/lib/net8.0`),
cross-read against the vendored `scratch/libremetaverse_src` (which is only `v3.0.2` — older
than pinned, use it for behaviour, not signatures).

### The wearable API
- `void AppearanceManager.RemoveFromOutfit(InventoryItem)` / `RemoveFromOutfit(List<InventoryItem>)`
- `void AppearanceManager.AddToOutfit(InventoryItem, bool replace = true)` / `AddToOutfit(List<InventoryItem>, bool replace = true)`
- `Task AppearanceManager.ReplaceOutfitAsync(List<InventoryItem>, bool safe = true)`
- `Task AppearanceManager.RequestSetAppearance(bool forceRebake = false)`
- `WearableType AppearanceManager.IsItemWorn(InventoryItem)` / `bool IsItemWorn(UUID)`

`RemoveFromOutfit` drops the wearable from the `Wearables` collection (it **skips body parts** —
a Shape/Skin/Eyes/Hair can only be *replaced*, never removed — matching the viewer), sends the
`AgentIsNowWearing` packet (server-side wearing state, **not** gated by any flag), then calls
`DelayedRequestSetAppearance()`. `AddToOutfit` and `ReplaceOutfitAsync` do the same.

### The load-bearing gotcha
`RemoveFromOutfit` / `AddToOutfit` / `ReplaceOutfitAsync` call `DelayedRequestSetAppearance()`
**unconditionally**. The `Settings.Agent.SendAppearance` flag is checked in only three places
(`AppearanceManager.cs` in the 3.0.2 checkout: lines 2993, 3011, 3160) — the
`AgentWearablesUpdate` handler, the `RebakeAvatarTextures` handler, and
`Simulator_OnCapabilitiesReceived` (login / region change). **None of them is on the
`RemoveFromOutfit → DelayedRequestSetAppearance → RequestSetAppearance → RequestSetAppearanceAsync
→ (SSB POST | client-side bake + AgentSetAppearance)` path.**

Therefore:
- Calling `RemoveFromOutfit` / `AddToOutfit` **is** the rebake trigger this task needs, and
- it **is also** the exact `AgentSetAppearance` path the 2026-08-02 `SendAppearance=false` guard
  was protecting — so the guard alone does not make these calls safe.

### The second load-bearing gotcha (found by a live corruption repro, 2026-08-29)
`AppearanceManager.MakeAppearancePacket()` — which builds the outgoing `AgentSetAppearance` —
**does not read `MyVisualParameters` at all.** It rebuilds every one of the 218 (or 251 with
Physics) params from `wearable.Asset.Params`, and for any param not supplied by a worn wearable's
**decoded asset** it uses `vp.DefaultValue`. Then it *overwrites* `MyVisualParameters` with the
result (`AppearanceManager.cs:2500-2544`).

With `SendAppearance = false` LibreMetaverse never downloads/decodes the wearable assets, so
`wearable.Asset` is `null` for every wearable → every param falls to `DefaultValue` → the packet
is the **default shape**. Seeding `MyVisualParameters` from the login relay does nothing to stop
this — the seed is never consulted and is then clobbered.

The client-side-bake branch of `RequestSetAppearanceAsync` (3.1.3) *does* call
`GatherAgentWearablesAsync` + `DownloadWearablesAsync` + `CreateBakesAsync` before sending, so
**if** those downloads all succeed the packet carries the real shape. Whether they succeed on a
given grid/session is not observable from SLNG's log, and a single failure sends defaults.

**Live repro 2026-08-29 (`v0.11.10-alpha`):** the user removed an "Apollo Meshbody Full Alpha"
Clothing layer via the Phase-1 `RemoveFromOutfit` route; `godot.log` shows the seed
(`[VisualParams] seeded 253 params`) and then `[Appearance] removed worn layer … via
RemoveFromOutfit`, after which the avatar rendered as nothing. This is the 2026-08-02 failure
mode. Phase 1's wearable **send was reverted** the same day — `AttachItemAsync` /
`DetachItemAsync` now classify the item and, for a wearable, log and send **nothing**.

### The param-seed (kept — diagnostic only)
`GridSession.OnAvatarAppearance` still seeds `MyVisualParameters` from the self relay. It is
harmless and lets `LogVisualParamHealth()` report a real value, but per the gotcha above it is
**not** a safety mechanism.

## Constraints / landmines
- **Do not just flip `SendAppearance = true`.** A wearable edit must be refused unless
  `MyVisualParameters` reads as a full, non-default set at that moment.
- `src/` stays engine-neutral: no LibreMetaverse type crosses `GridSession`'s public surface.
- Wearable changes arrive on background threads — buffer + drain, never touch `World` or Godot
  from the callback (AGENTS.md).
- OpenSim is the test target. SL server-side-baking path is secondary.

## Phase plan

### Phase 1 — landed then partly reverted (`v0.11.10` → `v0.11.11-alpha`)
Kept:
- **Param seed.** `GridSession.OnAvatarAppearance` seeds `MyVisualParameters` from the healthy
  self relay (diagnostic only — see the gotcha).
- **Classification.** `ClassifyItem(bool isInventoryWearable, int assetType)` → `{ Wearable,
  Attachment }`. `AttachItemAsync` / `DetachItemAsync` use it.
- Tests for `ClassifyItem` / `VisualParamsHealthy` / the seed.

Reverted (2026-08-29 live repro):
- The wearable **send**. `AttachItemAsync` / `DetachItemAsync` no longer call `AddToOutfit` /
  `RemoveFromOutfit` — for a wearable they log `[Appearance] … not sent: system-wearable rebake
  path is unsafe while SendAppearance is off (FEAT-AVATAR-01)` and return `DetachResult(false, 0)`.
  A `DetachAttachmentIntoInv` for a Clothing/Bodypart layer was always a no-op, so this is not a
  regression — the layer still cannot be removed, but nothing gets corrupted.

### Phase 2 — ABANDONED. Three send attempts, all corrupted the stored appearance (reverted `v0.11.29-alpha`)

**Summary of what does not work, so nobody tries it again:**

| Attempt | Idea | Result on OSGrid (live 2026-08-31) |
|---|---|---|
| `v0.11.26` | Gate on `RegionHasServerSideBaking()` (SL cap path — `RequestSetAppearanceAsync` POSTs only `{ cof_version }`, server composites) | Never fires. LMV's `ServerBakingRegion()` = `RegionProtocols.AgentAppearanceService` + `UpdateAvatarAppearance` cap, both **SL-only**. OpenSim's server-side appearance (XBakes, which the user confirmed configured) is a *different* mechanism the check doesn't see. Feature dead on OSGrid. |
| `v0.11.27` | Client-side: decode every worn wearable first (`RequestAgentWornAsync` + `Assets.RequestAssetAsync` + `AssetWearable.Decode()` onto `WearableData.Asset`), then `RemoveFromOutfit`/`AddToOutfit` so `MakeAppearancePacket` builds real params | `no worn wearables resolved from COF` — `RequestAgentWornAsync` (COF / `FetchInventoryDescendents2`) returns empty on OSGrid, and with `SendAppearance` off LMV never sent `AgentWearablesRequest` at login, so `AppearanceManager.Wearables` was empty. |
| `v0.11.28` | + `RequestWornWearablesViaLludpAsync()`: send the LLUDP `AgentWearablesRequestPacket` ourselves, wait for `AgentWearablesReply` | Worn list **arrived**, all wearables **decoded**, `RemoveFromOutfit`/`AddToOutfit` fired — and `RequestSetAppearanceAsync` **STILL sent a broken bake**. `[VisualParams]` came back **127 of 218 params zero**, avatar flattened (screenshot). |

**Root cause — structural, not a bug in any one attempt.** `SendAppearance=false` means the whole
bake pipeline (`Simulator_OnCapabilitiesReceived`, the `AgentWearablesUpdate` trigger,
`RebakeAvatarTextures`) never initialised for the session. Calling `RequestSetAppearanceAsync`
*once*, mid-session, bakes from that cold, partial state and uploads garbage baked textures + a
half-built param set, which the sim persists. Flipping `SendAppearance=true` is the 2026-08-02
landmine (`MyVisualParameters` empty → default shape persisted). There is no middle path from
outside LibreMetaverse.

**What a real fix would need** (option b is now the `SLNG_APPEARANCE_SYNC` experiment):
(a) fork/patch LibreMetaverse so a wearable edit can run a *fully* initialised bake without the
login-time `SendAppearance` gate; or **(b) turn `SendAppearance=true` from login** so LMV's whole
pipeline runs as Firestorm's does — this is `SLNG_APPEARANCE_SYNC=1` (`v0.11.30-alpha`). The
2026-08-02 failure was on LMV 3.0.0; 3.1.3 had a major appearance/bake rework since, so this may
just work now. `LogVisualParamHealth()` + a loud WARNING in `OnAvatarAppearance` report whether
the params come back healthy; if they don't the login already corrupted the shape. Off by default.
Or (c) a server-side (region module) approach entirely outside the viewer.

### SLNG_APPEARANCE_SYNC — the experiment (`v0.11.30-alpha`)
- `GridSession` ctor reads `SLNG_APPEARANCE_SYNC` (`1`/`true`); when set, `SendAppearance = true`
  and it prints a warning line to stderr.
- `AttachItemAsync`/`DetachItemAsync` wearable branch → `WearWearableAsync`/`RemoveWearableAsync`:
  with the flag, real `AddToOutfit`/`RemoveFromOutfit` (LMV owns the rebake); without, the no-op.
- `OnAvatarAppearance` skips `TrySeedVisualParams` when the flag is on (LMV owns
  `MyVisualParameters`), and prints `[Appearance] WARNING (SLNG_APPEARANCE_SYNC): … unhealthy …`
  if LMV's param set reads bad against a good sim relay.
- `LogAppearanceEditReadiness()` logs "edits ON via SLNG_APPEARANCE_SYNC" vs "disabled".
- **Test:** run with the var set, on OSGrid (throwaway alt), Firestorm ready. Watch the first
  `[VisualParams]` line after login — varied values ≈ good; most of 218 at 0 = corrupted, relog
  Firestorm + re-wear the shape + unset the var. If good, detach an Alpha layer → system body
  should return with the shape intact.

**Left in the code:** `ClassifyItem`, `VisualParamsHealthy`, `TrySeedVisualParams` (diagnostic),
`RegionHasServerSideBaking()` (harmless query + test), `LogAppearanceEditReadiness()` (logs
"disabled" once per region). `AttachItemAsync`/`DetachItemAsync` route a Clothing/Bodypart layer
to `WearWearableAsync`/`RemoveWearableAsync`, which **log + raise `WearableEditUnavailable` and
change nothing**.

---

<details><summary>Original Phase 2 write-up (obsolete — kept for the LibreMetaverse RE it contains)</summary>

**First cut was SSB-only** — gate the send on `RegionHasServerSideBaking()` (the SL cap path, where
`RequestSetAppearanceAsync` POSTs only `{ cof_version }` to `UpdateAvatarAppearance`,
`AppearanceManager.cs:2225`, and the server composites). Live-tested 2026-08-31: **the client
logged "region has no server-side baking" on every OSGrid region.** The user verified the grid's
`OpenSim.ini` has `[XBakes] URL = http://xbakes.osgrid.org` — OpenSim's server-side appearance IS
on. But **OpenSim SSA is not the same thing as LibreMetaverse's `ServerBakingRegion()`**: that
checks `RegionProtocols.AgentAppearanceService` + the `UpdateAvatarAppearance` cap, both SL-only
(the LMV source says so outright — `RequestSetAppearanceAsync:3264`, `MakeAppearancePacket` ~2952
"always false on OpenSim"). So the SSB gate is dead on OSGrid and the feature never fired.

**Reworked to decode-then-send.** The actual failure mode (2026-08-02 / 2026-08-29) is
`MakeAppearancePacket` rebuilding the 218 visual params and falling back to `vp.DefaultValue` for
every param whose worn wearable it couldn't decode — a flat avatar when the Shape bodypart is
missing. So make that impossible before letting the edit fire:

- `EnsureWornWearablesDecodedAsync()`:
  1. **Get the worn list.** With `SendAppearance` off, LibreMetaverse never sent an
     `AgentWearablesRequest` at login, so `AppearanceManager.Wearables` is empty. So
     `RequestWornWearablesViaLludpAsync()` — send the LLUDP `AgentWearablesRequestPacket`
     ourselves and wait for `AgentWearablesReply` (OpenSim answers this reliably; mirrors LMV's
     private `GatherAgentWearablesViaLLUDPAsync`). Fall back to `RequestAgentWornAsync()` (COF)
     only if that's still empty — **live 2026-08-31 the COF path returned nothing on every OSGrid
     region** (`FetchInventoryDescendents2` cap flakiness), which was the "no worn wearables
     resolved from COF" refusal.
  2. **Decode.** `_client.Assets.RequestAssetAsync(w.AssetID, w.AssetType, …)` +
     `AssetWearable.Decode()` for each, writing onto `WearableData.Asset` (the same reference
     objects LMV's `DownloadWearablesAsync` reads, so it skips its own fetch).
  3. **Verify.** Returns **false** — caller must refuse — if no Shape is worn or any wearable
     fails to fetch/decode. Serialised by a `SemaphoreSlim`.
- `PrepareWearableEditAsync(name)` = `RegionHasServerSideBaking()` (SL fast-path, nothing local) ||
  `EnsureWornWearablesDecodedAsync()`; on false → log + raise `WearableEditUnavailable`.
- `AttachItemAsync` / `DetachItemAsync` wearable branch → `WearWearableAsync` / `RemoveWearableAsync`
  (async): prepare, then `AddToOutfit` / `RemoveFromOutfit`; `RemoveWearableAsync` returns
  `DetachResult(WearableRemoved: true)` on success, `DetachResult(false, 0)` on refusal.
- `LogAppearanceEditReadiness()` — one line per region at `EventQueueRunning`: `SL server-side
  baking — straight through` vs `client-side bake path — decodes every worn wearable first`.
- `Boot.NotifyWearableEditUnavailable` → nearby-chat System line "'‹item›' konnte nicht geändert
  werden — nicht alle getragenen Wearables ließen sich laden".
- `InventoryPanel` reports `WearableRemoved` distinctly.

**Residual risk:** this DOES send `AgentSetAppearance` on OpenSim (there is no way around it there),
but only after verifying the params will be built from the user's real Shape/Skin assets. If a
worn wearable genuinely can't be fetched the edit is refused, not sent with defaults. First live
test still A/B in Firestorm on a non-critical Alpha layer.

**Not** manually seeding `MyVisualParameters` (Phase 1's approach) — `MakeAppearancePacket` ignores
it and rebuilds from `wearable.Asset`. Decoding the assets is the only thing that helps.

</details>

### Phase 3 — render verification (likely no code)
Confirm `AvatarRenderer.UpdateVisual` step 3 + `RecomputeMeshVisibility` (M4-7) pick up the fresh
bake ids after a wearable edit. Expected to already work once real bakes arrive.

## Acceptance criteria
- [ ] Detaching an Alpha wearable reveals the system body it was hiding, no relog.
- [ ] Wearing an Alpha wearable hides the painted regions.
- [ ] Swapping skin / shape updates the avatar.
- [ ] `LogVisualParamHealth()` reads a full, non-default parameter set before any
      `AgentSetAppearance`; the 2026-08-02 flattening does not recur (A/B in Firestorm).
- [x] Attachment detach (the current `DetachItemAsync` behaviour) still works — unchanged path.
- [x] Unit test: wearable vs attachment routing picks the right LibreMetaverse call.

## Affected files
- `src/SLNG.Net/GridSession.cs` — `OnAvatarAppearance` param seed (`TrySeedVisualParams`);
  `ClassifyItem` / `VisualParamsHealthy` helpers; `AttachItemAsync` / `DetachItemAsync` classify
  the item and refuse (log, no send) for a wearable.
- `src/SLNG.Core/AvatarProfile.cs` — `DetachResult.WearableRemoved` field added (kept for Phase 2;
  currently never set true).
- `tests/SLNG.Net.Tests/GridSessionTests.cs` — classification + health-check + seed tests.
- `app/scripts/AvatarRenderer.cs` — Phase 3 verify only.
