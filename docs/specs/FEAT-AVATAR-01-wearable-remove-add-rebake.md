# [FEAT-AVATAR-01] System Wearables — Remove / Add + Self-Rebake

- **Feature ID:** `FEAT-AVATAR-01`
- **Track:** `net` / `render`
- **Status:** `🧪 Review` — Phase 2 (SSB-gated send) landed `v0.11.26-alpha`, awaiting in-world A/B.
  Phase 1's wearable send was reverted after a live corruption repro
  2026-08-29 — see "The second load-bearing gotcha"; only the param-seed + classification + tests
  remain)
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

### Phase 2 — SSB-gated wearable send (landed `v0.11.26-alpha`, `feature/FEAT-AVATAR-01-wearable-remove-add-rebake`)
Chosen the **SSB-only** option — it is *provably* safe by construction. Verified against the pinned
LibreMetaverse 3.1.3: `RemoveFromOutfit`/`AddToOutfit` → `DelayedRequestSetAppearance` →
`RequestSetAppearanceAsync`; on a server-side-baking region that path's only outgoing request is
`UpdateAvatarAppearanceAsync`, whose POST body is `new OSDMap { ["cof_version"] = N }`
(`AppearanceManager.cs:2225`) — **no visual params, no shape, no wearable data**. The server reads
the Current Outfit Folder from inventory and composites the bake. There is no code path by which a
wrong client shape can be persisted.

- `GridSession.RegionHasServerSideBaking()` = `Network.Connected` **and** `ServerBakingRegion()`
  **and** `CurrentSim.Caps.CapabilityURI("UpdateAvatarAppearance") != null`. Both halves are
  required: LibreMetaverse itself falls back to the unsafe client-side bake when the cap is absent
  even though the protocol flag is set (`RequestSetAppearanceAsync:3264`), which is exactly why the
  2026-08-29 OSGrid repro flattened the avatar.
- `AttachItemAsync` / `DetachItemAsync` wearable branch: if `RegionHasServerSideBaking()` →
  `AddToOutfit(item, replace)` / `RemoveFromOutfit(item)` and (`DetachItemAsync`) return
  `DetachResult(WearableRemoved: true)`. Otherwise → refuse: log `[Appearance] … not sent: region
  has no server-side baking …` and raise `WearableEditUnavailable` (→ `Boot` shows a nearby-chat
  System line "‹item› kann hier nicht geändert werden — die Region hat kein Server-Side Baking").
- `GridSession.LogServerSideBakingStatus()` writes one line per region at `EventQueueRunning`
  (caps are up by then): `[Appearance] <region>: server-side baking available/UNAVAILABLE
  (protocol=…, cap=…)` — so "why did nothing happen" always has an answer in the log.
- `InventoryPanel` reports `WearableRemoved` distinctly ("Wearable removed — server re-baking…").

**Not the "force a wearable download first" option** (make the non-SSB client-side path safe by
decoding all worn wearables before the send): LibreMetaverse exposes no download-only trigger, and
`RequestSetAppearanceAsync` cannot be aborted between its internal `DownloadWearablesAsync` (which
continues on partial failure) and `MakeAppearancePacket`. Left as a possible Phase 4 if a
non-SSB grid ever needs it.

**Needs in-world verification:** on an SSB region, detach an Alpha layer → system body returns, no
relog; A/B in Firestorm on a non-critical layer to confirm the shape is untouched. On a non-SSB
region the edit must (safely) do nothing but log + chat-notify.

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
