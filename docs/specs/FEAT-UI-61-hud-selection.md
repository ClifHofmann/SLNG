# [FEAT-UI-61] Select worn HUDs like objects

- **Feature ID:** `FEAT-UI-61`
- **Track:** `ui` / `render`
- **Status:** `✅ Done`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Asked 2026-10-05: "dass ich angelegte Huds wie Objekte auswählen kann (also dann auch bearbeiten oder ablegen)".
Worn body attachments were already selectable (FEAT-UI-23). A worn HUD was not: it is drawn in the HUD overlay
(`AvatarRenderer`, own `SubViewport` with its own physics world), which the selection raycast in the main world
cannot reach. A right-click on a HUD therefore opened the ground menu for whatever lay behind it, and a left click
could only touch.

## Behaviour
| Input | Build mode closed | Build mode open |
|---|---|---|
| Left click on a HUD prim | touch (unchanged) | pick it; `Shift` adds/removes |
| Right-click on a HUD prim | mark it + object menu: Edit, Detach (no Create) | pick it + same menu |
| Mouse wheel | camera | HUD zoom while a HUD is the selection (FEAT-UI-23, unchanged) |

A HUD is on top of everything, so `ObjectSelectionController` asks for a HUD hit before it raycasts the world.
Linksets resolve to their root as everywhere else (Edit Linked Parts off), stopping at the avatar.

## Acceptance Criteria
- [ ] Right-click on a worn HUD prim outlines it and shows Edit / Detach, not the ground menu.
- [ ] Detach takes it off and it stays off after a relog (existing `DetachByLocalId` path).
- [ ] Edit opens the edit window on the HUD; position/size fields change it.
- [ ] In build mode a left click on another HUD prim moves the selection to it instead of pressing it.
- [ ] With the edit window closed, a left click still presses HUD buttons.
- [ ] Right-click beside the HUD still reaches the world (object / ground menu).

## Technical Specs & Affected Files
- `app/scripts/AvatarRenderer.cs` — `TryHitHud` (ray + surface detail), `TryPickHud` (public, quiet), `TryClickHud`
  (touch; stands aside when `IsBuildMode`).
- `app/scripts/ObjectSelectionController.cs` — `PickHud`, `EditSessionOpen`, `TryHandleHudClick`,
  `ResolveSelectionTarget`, `MarkRightClicked`.
- `app/scripts/UI/InWorldContextMenu.cs` — no *Create* entry for a HUD.
- `app/scripts/Boot.cs` — wires `PickHud` and `IsBuildMode`; `AppVersion` v0.26.62-alpha.

## Not in scope
- A move/stretch gizmo on a HUD -- done in [FEAT-UI-64](FEAT-UI-64-hud-gizmo.md). Until then position and size are
  typed in the edit window.
- Touch / Inspect entries in the object menu are still placeholders (`Boot`: "logic later"); unchanged.

## Sub-tasks / Progress
- [x] HUD ray split out, quiet pick
- [x] controller asks the HUD first; right-click menu, build-mode pick
- [x] no Create entry on a HUD
- [x] built and run (needs .NET 8 + Godot 4.7; not available in the session that wrote this)
- [x] confirmed in-world
