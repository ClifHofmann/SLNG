# [FEAT-ANIM-06] Play inventory animations — local / inworld

- **Feature ID:** `FEAT-ANIM-06`
- **Track:** `render` (+ `net`, `ui`)
- **Status:** `⏸️ Pending`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Firestorm parity: right-click an animation in inventory → **"Play Locally"** (renders
only in your own viewer, nobody else sees it) and **"Play Inworld"** (sent to the sim,
everyone sees it), each with a Stop. Useful for previewing anims before use/purchase,
machinima, and posing.

SLNG has all the pieces: inventory browser (M5-1 / MVP3-1), asset fetch
(`AssetService.GetAnimationAsync`), and its own per-bone blender
(`AvatarAnimationPlayer`).

## Behaviour

### Play Locally
- Fetch the `.anim` asset by its **asset UUID**, add it to the self avatar's
  `AvatarAnimationPlayer` as a **local overlay** — a set kept separate from the
  network-driven `_active` so that `ApplyActiveAnimations`' reconcile against the sim
  echo does **not** prune it.
- Priority: apply at the clip's authored priority, same per-bone blend as everything
  else, so a low-priority preview doesn't stomp a worn AO and vice versa (matches FS).
- No packet is sent. Other viewers see nothing.
- Stop: remove from the local overlay; if the overlay empties and the network set is
  also empty, `ResetToRestPose()`.

### Play Inworld
- `Client.Self.AnimationStart(assetUuid, true)` → `AgentAnimation` → the sim broadcasts;
  SLNG plays it via the normal echo path, so no local-overlay bookkeeping needed.
- Stop: `Client.Self.AnimationStop(assetUuid, true)`.
- Only offered for animations the user can access (in their own inventory); the sim
  rejects unknown assets — surface a chat line on failure, don't fail silently.

### Currently-playing / Stop UI
- Shared with FEAT-ANIM-04's per-anim list: one panel listing everything playing on the
  self avatar — network-sourced (with source-object name, from FEAT-ANIM-03),
  local-overlay (tagged "lokal"), and inworld-started — each with a Stop button, plus
  "Stop all local" / "Stop all".

## Acceptance Criteria

- [ ] Right-click an animation inventory item → "Lokal abspielen", "Inworld abspielen",
      "Stoppen" (context-menu entries only for the Animation asset type).
- [ ] "Lokal abspielen": the anim plays on the self avatar in SLNG; a second SLNG /
      Firestorm client sees nothing.
- [ ] "Inworld abspielen": a second client sees the anim; SLNG itself also shows it.
- [ ] A locally-played anim survives incoming network `AvatarAnimation` updates (not
      pruned by the reconcile) until explicitly stopped.
- [ ] Stopping a local anim removes it without disturbing network / inworld anims.
- [ ] Inworld play of an asset the sim rejects reports a visible error, no silent
      no-op.
- [ ] The playing-now panel lists local + network + inworld anims with working Stop.
- [ ] Unit tests: local overlay add/remove is independent of `SetActiveAnimations`;
      overlay clip participates in the priority blend.
- [ ] No regression: FEAT-ANIM-01/03/04, BUG-ANIM-01/02.

## Technical Specs & Affected Files

- `app/scripts/AvatarAnimationPlayer.cs`
  - `_localOverlay` list parallel to `_active`; `AddLocal(Guid id, AnimationData)`,
    `RemoveLocal(Guid id)`, `ClearLocal()`.
  - `ApplyBonePoses` iterates `_active` **and** `_localOverlay`; `Advance` advances both.
  - `SetActiveAnimations` / the reconcile only ever touches `_active`.
- `src/SLNG.Net/GridSession.cs`
  - `PlayAnimationInworld(Guid assetId)` / `StopAnimationInworld(Guid assetId)` wrapping
    `Client.Self.AnimationStart/Stop`; neutral signature.
- `app/scripts/AvatarRenderer.cs` — `PlaySelfAnimationLocal(Guid assetId)` /
  `StopSelfAnimationLocal(Guid assetId)`: fetch via `AssetService`, push to the self
  visual's `AnimPlayer` local overlay on the main thread.
- `app/scripts/UI/InventoryPanel.cs` — context-menu entries for the Animation type,
  wired to the above; locale strings both languages.
- Playing-now panel: extend the FEAT-ANIM-04 `AnimationListWindow` (or build it here if
  ANIM-04 hasn't landed) with the local/inworld tags + stop.
- `app/scripts/Boot.cs` — `AppVersion` bump.

## Sub-tasks / Progress

- [ ] `AvatarAnimationPlayer` local-overlay support + unit tests.
- [ ] `GridSession.PlayAnimationInworld` / stop.
- [ ] `AvatarRenderer` local play/stop entry points.
- [ ] `InventoryPanel` context menu + locale strings.
- [ ] Playing-now panel (shared with FEAT-ANIM-04).
- [ ] `AppVersion` bump.
- [ ] In-world: local vs inworld visibility across two clients; overlay survives echo;
      stop paths.
