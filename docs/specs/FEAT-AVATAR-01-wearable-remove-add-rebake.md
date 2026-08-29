# [FEAT-AVATAR-01] System Wearables — Remove / Add + Self-Rebake

- **Feature ID:** `FEAT-AVATAR-01`
- **Track:** `net` / `render`
- **Status:** `🚧 In Progress` (Phase 1 landed; needs in-world A/B before Phase 2)
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

### The safe lever
The simulator relays the **self** `AvatarAppearance` at login with a full ~218-byte
`VisualParams` block (SLNG's morph pipeline, `AvatarComponent.VisualParams` →
`AvatarShapeService`, already consumes it). LibreMetaverse never copies that block into
`AppearanceManager.MyVisualParameters` — that field is only populated by its own (disabled)
appearance workflow (SSB response `visual_params`, or the client-side bake decoding wearable
assets). `MyVisualParameters` is a **public field**, settable from `GridSession`.

Seeding `MyVisualParameters` from the login relay makes any subsequent `AgentSetAppearance`
carry the avatar's real shape as its baseline — directly neutralising the 2026-08-02 root cause
("empty params → simulator stores defaults"). Confirmed still-empty on 3.1.3: every recent
`godot.log` shows `[VisualParams] LibreMetaverse holds NO visual parameters`.

Residual risk: on a **client-side-baking** region `RequestSetAppearanceAsync` rebuilds
`MyVisualParameters` from *downloaded wearable assets*, overwriting the seed. If those downloads
succeed the params are still correct (that is their authoritative source); if one fails, LMV's
own behaviour applies. This is why Phase 1 does not flip the auto-triggers and why acceptance
criterion 4 (Firestorm A/B) can only be closed live.

## Constraints / landmines
- **Do not just flip `SendAppearance = true`.** A wearable edit must be refused unless
  `MyVisualParameters` reads as a full, non-default set at that moment.
- `src/` stays engine-neutral: no LibreMetaverse type crosses `GridSession`'s public surface.
- Wearable changes arrive on background threads — buffer + drain, never touch `World` or Godot
  from the callback (AGENTS.md).
- OpenSim is the test target. SL server-side-baking path is secondary.

## Phase plan

### Phase 1 — routing split + param seed (landed, `v0.11.10-alpha`) — no auto-trigger change
1. **Seed `MyVisualParameters`.** `GridSession.OnAvatarAppearance`, for the self avatar: if the
   incoming `VisualParams` passes `VisualParamsHealthy()` and LibreMetaverse currently holds a
   shorter/empty set, copy it into `_client.Appearance.MyVisualParameters`. Log
   `[VisualParams] seeded N params from self AvatarAppearance relay`.
2. **Classify + route.** `ClassifyItem(bool isInventoryWearable, int assetType)` →
   `{ Wearable, Attachment }` (`InventoryWearable`, or `AssetType.Clothing` / `AssetType.Bodypart`
   → `Wearable`). `AttachItemAsync` routes `Wearable` → `AddToOutfit(item, replace || isBodypart)`;
   `DetachItemAsync` routes `Wearable` → `RemoveFromOutfit(item)`. Attachments keep the current
   `Attach` / `Detach` path unchanged.
3. **Gate.** If the item is a `Wearable` but `AppearanceEditSafe` is false (params not seeded /
   not healthy), send **nothing** — log `[Appearance] wearable edit refused: <reason>` and return
   a result the UI surfaces. A rebake can then never fire on an empty param set.
4. `SendAppearance` stays `false`: the login / region-change / rebake-request auto-triggers are
   still off; only a user-initiated wearable edit produces a send, and it now carries a real shape.
5. `DetachResult` gains `bool WearableRemoved`; `InventoryPanel` reports "Removed worn layer —
   rebaking…" for that case.

### Phase 2 — enable the auto-triggers (needs live A/B first)
Once the Firestorm A/B confirms Phase 1 does not regress the shape: flip `SendAppearance = true`
(guarded by the same health check) so region-change re-bakes and sim `RebakeAvatarTextures`
requests are honoured, and so a fresh login computes a bake instead of relying on the relay.

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
- `src/SLNG.Net/GridSession.cs` — `OnAvatarAppearance` param seed; `ClassifyItem` /
  `VisualParamsHealthy` / `AppearanceEditSafe` helpers; `AttachItemAsync` / `DetachItemAsync`
  routing split.
- `src/SLNG.Core/AvatarProfile.cs` — `DetachResult.WearableRemoved`.
- `app/scripts/UI/InventoryPanel.cs` — surface the wearable-removed / refused messages.
- `tests/SLNG.Net.Tests/GridSessionTests.cs` — classification + health-check + routing tests.
- `app/scripts/AvatarRenderer.cs` — Phase 3 verify only.
