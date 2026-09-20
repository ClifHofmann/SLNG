# Feature: FEAT-UI-23 (Right-click a worn object in-world → Detach / Edit)

## Context
A worn attachment renders in the 3D scene but cannot be picked there — the only way
to act on it is via the inventory or the Angezogen tab. Users expect the reference-viewer
behaviour: right-click the thing on your avatar and get Detach / Edit right there.

## Requirements
1. **Selection:**
   - `ObjectSelectionController` must let a raycast hit a prim whose `ParentID` is the
     local avatar (an attachment), not just world objects. Today those are skipped.
   - Attachments worn by *other* avatars stay non-selectable for this menu (that is the
     avatar context menu's job).
2. **Context menu (`InWorldContextMenu`):**
   - **Ablegen** — reuse `GridSession.DetachByLocalId`; refresh the Angezogen list.
   - **Bearbeiten** — open `ObjectEditWindow` for the attachment. Shown only when the
     object is modify-perm for the current user; hidden (or disabled) otherwise.
   - Keep the existing Touch / entries where they apply to an attachment.
3. **Permissions:**
   - Read the perm mask already carried on the prim component; do not rely on the sim
     to reject an edit the user cannot make.

## Acceptance Criteria
- [x] Right-clicking a worn attachment in the viewport opens a context menu.
- [ ] Ablegen detaches it and the change survives a relog — wired, in-world confirmation outstanding.
- [x] Bearbeiten opens the editor; its fields are gated on Modify by FEAT-SEC-04 already.
- [x] Other avatars' attachments do not trigger this menu — they get no collider at all.

## Technical Specs & Affected Files
- `app/scripts/ObjectSelectionController.cs`
- `app/scripts/UI/InWorldContextMenu.cs`
- `app/scripts/UI/ObjectEditWindow.cs`
- `src/SLNG.Net/GridSession.cs` (existing `DetachByLocalId`)

## How it came out (`v0.24.1-alpha`)

**Picking.** A worn item had no collider, so the ray went straight through it. It gets the same
kind of tagged `StaticBody3D` that `ObjectRenderer` puts on a world prim, with the same
`EntityId` and `LocalId` metas, so the whole existing selection path resolves it with no special
case — including the peek-behind-the-local-avatar logic the feature depends on: the avatar's
capsule swallows the ray before anything worn inside it.

Two deliberate limits. Only the **local agent's** attachments get a collider, which is all the
feature needs and keeps a trimesh shape per worn item off every avatar on a busy sim. And only
**static** ones: a rigged mesh is deformed by the skeleton every frame while a trimesh shape
would stay in the bind pose, so the collider would sit where the item visibly is not. Rigged
worn items stay unpickable, which costs little — their prim transform does not move them anyway.

**Menu.** A worn item shows **Detach** in place of Sit and Delete. You cannot sit on what you are
wearing, and the reference viewer offers Detach there too. `GridSession.DetachByLocalId` already
does the Current-Outfit write-back from FEAT-INV-03.

**An avatar is not a link root.** This is the trap the feature is really made of. A worn item's
`ParentLocalId` is the avatar wearing it, and three separate root-walks would happily have
climbed it: the selection controller's, the edit window's, and the link-root lookup that
FEAT-UI-06 added for parent-relative transforms. Right-clicking a hat would have selected the
avatar, and the third would have subtracted the avatar's position from an offset that is already
attach-point-relative. All three stop at an avatar now, the way
`WorldSimulation.ResolveWorldTransform` always has.

## Known limitation: no gizmo on a worn item

The handles are deliberately refused for anything worn, the item itself and every child prim of
a worn linkset. An attachment's transform is relative to its **attach point** —
`ResolveWorldTransform` leaves it local precisely because the renderer places it through the
bone hierarchy — and the gizmo places its handles from `TransformComponent.Position`, which for
an attachment is a few centimetres of offset and would put them at the corner of the region.
Dragging would then send that back as a position, which is how a linkset was flung across the
region twice while FEAT-UI-06 was being built. Refusing costs one guard and risks nothing.

The numeric fields *are* already in the attach point's frame, so editing a worn item by typing
works today. The gizmo needs the attach point's world transform, which only the scene graph
knows — a separate piece of work.
