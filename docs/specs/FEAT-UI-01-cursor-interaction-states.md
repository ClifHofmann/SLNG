# [FEAT-UI-01] Contextual Cursors & Interaction Feedback

- **Feature ID:** `FEAT-UI-01`
- **Track:** `ui` / `render`
- **Status:** `⏸️ Pending`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Implement dynamic mouse cursor state changes based on key modifier holds and object hover states to match SL/Firestorm UX standards:
1. Holding `Alt` key changes cursor to a Magnifying Glass (camera orbit/zoom mode).
2. Hovering over scripted/clickable in-world objects changes cursor to a Hand icon (`Hand` / `Touch`).
3. Hovering over sitting objects changes cursor to a Sit icon (`Sit`).

## Functional Requirements
- **Key Modifiers:**
  - `Alt` key press/release updates `Input.MouseMode` / custom Godot cursor shape (`Control.CursorShape.Zoom` / Magnifying Glass texture).
- **In-World Raycast Hover Detection:**
  - 3D mouse hover raycast checks underlying entity components (`ClickAction`, script touch handler, sit target).
  - Dynamically changes viewport cursor texture (Hand / Sit / Zoom / Default).

## Acceptance Criteria
- [ ] Holding `Alt` switches cursor to magnifying glass icon immediately.
- [ ] Hovering over clickable objects changes cursor to hand icon.
- [ ] Releasing modifier keys or un-hovering restores default cursor smoothly.

## Technical Specs & Affected Files
- `app/scripts/Input/CursorManager.cs` — Central manager for cursor states and raycast hover feedback.
- `app/scripts/Render/MainViewport.cs` — Viewport hover raycast logic.
