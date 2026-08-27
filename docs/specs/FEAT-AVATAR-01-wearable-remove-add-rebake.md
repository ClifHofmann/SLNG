# [FEAT-AVATAR-01] System Wearables — Remove / Add + Self-Rebake

- **Feature ID:** `FEAT-AVATAR-01`
- **Track:** `net` / `render`
- **Status:** `⏸️ Pending`
- **Owner:** —
- **Agent:** `protocol-re` (net side) → `graphics-engineer` (bake/render side)
- **Dep:** `M4-3` (Appearance & BoM)

## Problem
Taking a **system wearable** off (or putting one on) in SLNG does nothing visible. Found while
verifying `M4-7`: the user removed an "Apollo Meshbody Full Alpha" layer and the system body
stayed hidden — in SLNG *and*, at that point, correctly matched nothing changing because the
change never reached the grid.

Two independent causes:

1. **`GridSession.DetachItemAsync` only detaches attachments.** It resolves candidate UUIDs and
   calls `_client.Appearance.Detach(u)` for each — that sends `DetachAttachmentIntoInv`, which
   the simulator applies only to *attachments*. A wearable (`AssetType.Clothing` /
   `AssetType.Bodypart`, e.g. Alpha, Skin, Shape, Tattoo, Universal) is not an attachment, so
   the packet is a no-op and the layer stays worn server-side. There is no
   `AppearanceManager.RemoveFromOutfit` / `ReplaceOutfit` / wearable-removal path anywhere, and
   `AttachAndRefreshAsync` has the mirror gap on the add side.

2. **`SendAppearance = false`.** Set in `GridSession` ctor (see the long note at
   `GridSession.cs:182–210`) after the 2026-08-02 "avatar squat & deformed, in Firestorm too"
   incident: `Appearance.MyVisualParameters` was empty, so every `AgentSetAppearance` SLNG sent
   replaced the real shape with defaults. With the flag off, LibreMetaverse never computes a
   client-side bake, never uploads one, and never sends `AgentSetAppearance` — so even a
   wearable change that *did* reach the sim would not produce a new bake on OpenSim, and the
   `AgentWearablesUpdate` / `RebakeAvatarTextures` triggers are all no-ops.

Net effect: the baked textures the renderer holds are frozen at whatever the grid had when
SLNG logged in. `AvatarRenderer.UpdateVisual` step 3 (`anyBakeChanged`) never fires post-login
for the self avatar.

## Goal
Removing or adding a system wearable in SLNG updates the avatar within a few seconds — the
system body/head shows or hides to match — without a relog, and without regressing the shape
(the `SendAppearance` incident must not recur).

## Constraints / landmines
- **Do not just flip `SendAppearance = true`.** `LogVisualParamHealth()` must first read a sane
  parameter set at the moment of any send (218-ish params, not all-0 / all-128). The existing
  `[VisualParams]` log line is the gate — wire a real check, don't assume.
- `src/` stays engine-neutral: no LibreMetaverse type crosses `GridSession`'s public surface.
- Wearable changes arrive on background threads — buffer + drain, never touch `World` or Godot
  from the callback (AGENTS.md).
- OpenSim is the test target. SL server-side-baking path is secondary.

## Sketch (to be confirmed by `protocol-re` against LibreMetaverse 3.1.3)
1. **Net:** real wearable remove/add — `AppearanceManager.RemoveFromOutfit(items)` /
   `AddToOutfit(items, replace)` (or `ReplaceOutfit`), branching on asset type so
   `DetachItemAsync` / `AttachItemAsync` route attachments vs. wearables correctly.
2. **Bake trigger:** decide per grid — on SSB regions request a rebake; on OpenSim client-side
   path, guarded re-enable of the bake+`AgentSetAppearance` *only* once the visual-param health
   check passes, else fall back to `RequestSetAppearance` and hope the sim bakes.
3. **Receive:** ensure `OnAvatarAppearance` / `OnAppearanceSet` fire again after the change and
   the new baked ids propagate to `AvatarComponent.BakedTextures` (they already emit through
   `AvatarAppearanceReceived`; verify the post-change event actually arrives).
4. **Render:** `UpdateVisual` step 3 already reloads changed bakes and re-resolves
   `BomAttachments` — should need no change once fresh bakes arrive.

## Acceptance criteria
- [ ] Detaching an Alpha wearable reveals the system body it was hiding, no relog.
- [ ] Wearing an Alpha wearable hides the painted regions.
- [ ] Swapping skin / shape updates the avatar.
- [ ] `LogVisualParamHealth()` reads a full, non-default parameter set before any
      `AgentSetAppearance`; the 2026-08-02 flattening does not recur (A/B in Firestorm).
- [ ] Attachment detach (the current `DetachItemAsync` behaviour) still works.
- [ ] Unit test: wearable vs attachment routing picks the right LibreMetaverse call.

## Affected files (anticipated)
- `src/SLNG.Net/GridSession.cs` — `DetachItemAsync` / attach path split; wearable remove/add;
  `SendAppearance` handling; rebake trigger.
- `app/scripts/UI/InventoryPanel.cs` — `DetachAndRefreshAsync` / `AttachAndRefreshAsync` already
  call the session; likely unchanged.
- `app/scripts/AvatarRenderer.cs` — verify only; `UpdateVisual` step 3 + `RecomputeMeshVisibility`
  (M4-7) should already handle the fresh bakes.
