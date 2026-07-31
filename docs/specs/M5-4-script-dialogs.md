# [M5-4] LSL / SLS Script Dialog System (llDialog)

- **Feature ID:** `M5-4`
- **Track:** `ui/net`
- **Status:** `✅ Done`
- **Owner:** `claude`
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
- [x] LibreMetaverse `ScriptDialog` packet events are handled on background threads, buffered, and dispatched to the Godot main thread.
- [x] Dialog window derives from `SLNGWindow` and displays object name, owner, prompt text, and up to 12 dynamic buttons.
- [x] Clicking a button sends the selected text back on the specified channel and closes the dialog.
- [x] Multiple concurrent dialogs queue properly without UI overlaps or crashes (cascading placement, same idiom as `ObjectEditWindow`).
- [x] Unit & integration tests written for packet parsing and channel reply formatting (`GridSessionTests.OnScriptDialog_maps_wire_event_to_ScriptDialogEvent`, `ReplyToScriptDialog_without_connection_does_not_throw`).

Live-verified 2026-07-31 against a `touch_start` -> `llDialog` test script: popup rendered with object/owner/message, button click sent the reply and closed the dialog, the script's `listen()` received it correctly.

**Known gaps, deliberately out of scope for this pass:** LibreMetaverse's `ScriptDialogEventArgs` has no dialog-session UUID (the spec's `ID` field, item 22 below, doesn't exist on the wire event), no auto-expire timeout, and no per-object/owner "always ignore" option. None of these are required by the acceptance criteria above.

## Technical Specs & Affected Files
- `app/scripts/UI/ScriptDialogWindow.cs` — UI modal window class inheriting from `SLNGWindow`.
- `app/scripts/UI/DialogQueueManager.cs` — Manager for stacked/queued script dialogs.
- `src/SLNG.Net/GridSession.cs` — Event subscription and thread-safe buffering for `ScriptDialog`.
- `docs/specs/M5-4-script-dialogs.md` — Feature specification.

## Sub-tasks / Progress
- [x] Create `M5-4` spec & update `ROADMAP.md`
- [x] Subscribe to `ScriptDialog` events in `SLNG.Net`
- [x] Create `ScriptDialogWindow` UI inheriting from `SLNGWindow` with 3x4 button grid
- [x] Implement response dispatching on script channel
- [x] Implement `DialogQueueManager` for concurrent dialog popups
