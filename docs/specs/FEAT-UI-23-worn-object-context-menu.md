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
- [ ] Right-clicking a worn attachment in the viewport opens a context menu.
- [ ] Ablegen detaches it and the change survives a relog (COF write-back already handled by FEAT-INV-03).
- [ ] Bearbeiten opens the editor only for mod-perm attachments.
- [ ] Other avatars' attachments do not trigger this menu.

## Technical Specs & Affected Files
- `app/scripts/ObjectSelectionController.cs`
- `app/scripts/UI/InWorldContextMenu.cs`
- `app/scripts/UI/ObjectEditWindow.cs`
- `src/SLNG.Net/GridSession.cs` (existing `DetachByLocalId`)
