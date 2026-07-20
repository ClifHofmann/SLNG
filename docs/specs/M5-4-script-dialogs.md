# [M5-4] LSL / SLS Script Dialog System (llDialog)

- **Feature ID:** `M5-4`
- **Track:** `ui/net`
- **Status:** `⏸️ Pending`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Implement support for in-world script-triggered menu dialogs generated via LSL/SLS (`llDialog`). When an in-world object or HUD script triggers a dialog, SLNG receives the `ScriptDialog` packet, presents a modal window popup inheriting from `SLNGWindow`, renders interactive buttons, and dispatches the user's selection back to the script channel.

## Functional Requirements

### 1. Packet & Event Handling (`SLNG.Net`)
- Listen to `GridClient.Self.ScriptDialog` events from LibreMetaverse.
- Extract payload details:
  - `ObjectID` & `ObjectName`
  - `OwnerID` & `FirstName`/`LastName` (Owner Name)
  - `Message` (dialog prompt text)
  - `ChatChannel` (integer channel for response)
  - `ButtonLabels` (list of strings, max 12 per dialog)
  - `ID` (unique dialog session UUID)

### 2. UI Dialog Window (`SLNGWindow`)
- UI Popup window class `ScriptDialogWindow.cs` inheriting from `SLNGWindow`.
- **Header:** Object Name and Owner Name.
- **Content Body:** Dialog message prompt text (with scrolling for long text).
- **Interactive Buttons:**
  - Dynamic button grid rendering up to 12 buttons (3 columns x 4 rows layout, matching standard SL viewer button order bottom-to-top/left-to-right).
  - Standard action buttons: `Ignore` / `Close`.
- **Visual Styling:** Matches SLNG dark glassmorphism theme.

### 3. User Response Dispatch
- Upon clicking a dialog button:
  - Transmit response back via `GridClient.Self.Chat(buttonText, ChatType.Normal, channel)` or `ReplyScriptDialog`.
  - Automatically close the dialog window.

### 4. Dialog Queue & Management
- Support multiple simultaneous dialog requests via a stacked notification queue or tabbed dialog overlay.
- Timeouts: Auto-expire dialogs if unanswered after server script timeout (or explicit dismissal).
- Option to ignore dialogs per object/owner.

## Acceptance Criteria
- [ ] LibreMetaverse `ScriptDialog` packet events are handled on background threads, buffered, and dispatched to the Godot main thread.
- [ ] Dialog window derives from `SLNGWindow` and displays object name, owner, prompt text, and up to 12 dynamic buttons.
- [ ] Clicking a button sends the selected text back on the specified channel and closes the dialog.
- [ ] Multiple concurrent dialogs queue properly without UI overlaps or crashes.
- [ ] Unit & integration tests written for packet parsing and channel reply formatting.

## Technical Specs & Affected Files
- `app/scripts/UI/ScriptDialogWindow.cs` — UI modal window class inheriting from `SLNGWindow`.
- `app/scripts/UI/DialogQueueManager.cs` — Manager for stacked/queued script dialogs.
- `src/SLNG.Net/GridSession.cs` — Event subscription and thread-safe buffering for `ScriptDialog`.
- `docs/specs/M5-4-script-dialogs.md` — Feature specification.

## Sub-tasks / Progress
- [ ] Create `M5-4` spec & update `ROADMAP.md`
- [ ] Subscribe to `ScriptDialog` events in `SLNG.Net`
- [ ] Create `ScriptDialogWindow` UI inheriting from `SLNGWindow` with 3x4 button grid
- [ ] Implement response dispatching on script channel
- [ ] Implement `DialogQueueManager` for concurrent dialog popups
