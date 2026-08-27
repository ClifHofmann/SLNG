# [M4-7] Base-mesh Hiding Under Worn Mesh

- **Feature ID:** `M4-7`
- **Track:** `render`
- **Status:** `✅ Done` (attachment side) — the add-only-set bug is fixed and matches the
  viewer's `updateMeshVisibility` semantics. The full visual end-to-end is blocked by a
  separate gap, not by M4-7: SLNG runs `SendAppearance = false` and `DetachItemAsync` only
  detaches *attachments*, so **removing a system wearable (Alpha layer, skin, clothing) does
  nothing** — no rebake, sim never told — so a "hide everything" bake can't be cleared to see
  the fix pay off. Tracked as `FEAT-AVATAR-01`.
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Goal
System head/body parts are hidden when a worn mesh covers them, per Bakes-on-Mesh rules and
worn alpha layers — the viewer's `LLVOAvatar::updateMeshVisibility` plus the baked alpha
channel. No system skin should show through a mesh body/head.

## What already existed (roadmap flag was stale)
`AvatarRenderer.RegisterBomAndUpdateVisibility` (called on every worn mesh, self **and** other
avatars) already:
- detects which bake channels a worn mesh consumes via its face texture ids
  (`BakedTextureIds.TryGetBakeIndex`) and hides the matching system parts —
  `head`+`eyelashes` (ch 8), `upper_body` (9), `lower_body` (10), `eye` (11), `hair` (20);
- re-resolves late-arriving server bakes onto already-worn BoM meshes
  (`UpdateVisual` step 3, `anyBakeChanged` → `BomAttachments` sweep);
- relies on the server bake's **alpha channel** (painted by Alpha wearables) + unconditional
  `AlphaHash` on the system-bake material to punch holes in the system body for classic
  (non-BoM) mesh bodies.

## Bug fixed in this pass
`AvatarVisual.AttachmentBakeChannels` was **add-only** — one `.Add(b)`, no removal or rebuild
anywhere. `RemoveVisual` (detach) reverted the pelvis fixup but never touched the channel set
or re-showed parts. Result: take off a BoM mesh head/body and the system part stayed
**invisible until relog**. A body-for-body swap had the same failure for any channel the new
body didn't use.

**Fix (`AvatarRenderer.cs`):**
- `RegisterBomAndUpdateVisibility` now just adds the mesh to `BomAttachments` (when it uses BoM)
  and delegates to a new `RecomputeMeshVisibility(AvatarVisual)`.
- `RecomputeMeshVisibility` rebuilds `AttachmentBakeChannels` **from scratch** off the live
  `BomAttachments` list (dropping entries whose `MeshInstance3D` was freed) and re-applies the
  six `SetPartVisible` calls. This is `LLVOAvatar::updateMeshVisibility`, which the viewer runs
  on add *and* remove.
- `RemoveVisual` and the "moved onto a HUD point" path now drop the removed mesh from
  `BomAttachments` by reference (its `QueueFree` is deferred, so an `IsInstanceValid` sweep
  would still see it live this frame) and call `RecomputeMeshVisibility` on the owner avatar.
- Replace-in-place (`_riggedAttachments` swap at the mesh-attachment update site) needs no
  extra call: the new mesh's own `RegisterBomAndUpdateVisibility` runs a full rebuild once it
  loads, by which point the old node is freed and pruned.

## Acceptance criteria
- [x] Wearing a BoM mesh body/head hides the matching system part(s).
- [x] **Detaching** that mesh re-shows the system part node without a relog (was `Visible=false`
      until relog before this pass). *(code fix; visual pay-off gated on `FEAT-AVATAR-01`)*
- [x] Swapping one BoM body for another leaves only the still-covered parts hidden.
- [~] Classic (non-BoM) mesh body + Alpha wearable — same bake-alpha + `AlphaHash` path already
      working for the user's alpha layer; no separate non-BoM body available to A/B.
- [→] Alpha wearable worn alone / removed to reveal the system body — **moved to
      `FEAT-AVATAR-01`**: `DetachItemAsync` can't remove wearables and there's no self-rebake,
      so this can't be exercised yet.
- [ ] **Live:** other avatars wearing BoM meshes hide their system body (shared code path;
      quick glance next login).

## Affected files
- `app/scripts/AvatarRenderer.cs` — `RecomputeMeshVisibility` (new), `RegisterBomAndUpdateVisibility`
  (refactored to call it), `RemoveVisual` + HUD-move path (recompute on removal).

## Out of scope
- Viewer-side per-region alpha *mode* UI (Firestorm's "Alpha Mode: none/blend/mask/emissive"
  per bake region) — SLNG has no wearables-editing UI; the bake arrives composited.
- `LLTexLayerParam` client-side baking of alpha layers — SLNG trusts the server bake.
