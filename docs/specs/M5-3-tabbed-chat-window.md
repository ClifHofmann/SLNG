# [M5-3] Tabbed Chat, Friends & Groups Window

- **Feature ID:** `M5-3`
- **Track:** `ui`
- **Status:** `🚧 In Progress`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Implement a unified communication and social hub window inheriting from `SLNGWindow`. The window combines Local Region Chat, Direct Messaging (IMs), Friends List, and Group Memberships using a dual-axis (vertical + horizontal) tabbed layout.

## Functional Requirements

### 1. Window Architecture & Styling
- Must derive from `SLNG.App.UI.SLNGWindow` to maintain SLNG glassmorphism aesthetics, dragging, and title bar behavior.
- **Vertical Navigation Tabs (Left Sidebar):**
  - `Chat`: Primary chat interface.
  - `Friends`: Social contact management.
  - `Groups`: Joined group listing and management.

### 2. Vertical Tab: `Chat`
- **Horizontal Tabs (Top Bar inside Chat view):**
  - **`Main`**: Fixed, non-closeable tab representing Local Region Chat (`ChatFromSimulator` packets). Always present as the first horizontal tab.
  - **Dynamic User IM Tabs**: Individual horizontal tabs created dynamically per active direct message conversation with another avatar. Each tab displays the avatar name and an option to close `(x)`.
- **Chat Body:**
  - Rich text message log (timestamps, sender names, message content, system notices), showing
    only the most recent lines of the active conversation (SL/Firestorm-style rolling window, not
    the full session).
  - Auto-scroll follows new messages, but **pauses** the instant the user scrolls up to read
    earlier lines — it must not yank them back to the bottom mid-read. A "jump to latest"
    affordance resumes auto-follow.
  - **History** control (button/link in the chat body) opens a small paginated history window
    (see §2a) backed by the on-disk log file for the active tab.
  - Message input text box with `Send` button (or `Enter` key trigger).
  - Unread message indicators on inactive horizontal tabs.

### 2a. Chat History Viewer
- A separate small window (inherits `SLNGWindow`) opened via the **History** control in the Chat
  body. Shows the full logged history for whichever tab was active when it was opened (local
  chat, a specific IM, or a group chat), read back from that tab's log file (§5) — not from the
  live in-memory buffer.
- **Pagination:** page-by-page navigation (prev/next, page indicator) rather than one long
  scrolling list — mirrors classic viewer "conversation log" dialogs. Page size and exact chrome
  are an implementation detail; the key contract is that it reads from disk via `ChatLogger`,
  independent of what's currently buffered in memory.
- Read-only — no sending from this window.

### 3. Vertical Tab: `Friends`
- Displays the user's friend list retrieved via `GridSession` / LibreMetaverse `FriendsManager`.
- **Status Indicators:**
  - Clear visual badges/icons marking each friend as `Online` (green indicator / highlighted text) or `Offline` (greyed out).
- **Interactions:**
  - Double-clicking a friend or selecting "IM" opens a dynamic user chat tab in the `Chat` vertical view and switches to it.

### 4. Vertical Tab: `Groups`
- Displays all Second Life / OpenSim groups in which the avatar holds membership (`GroupManager`).
- Displays group name, group badge/icon placeholder, and role/status.
- Allows opening group chat sessions or viewing group profiles.
- **Group Chat Mute / Ignore (Phase 2):** Toggle option per group (or in group chat tab header) to mute/ignore incoming group chat messages and prevent unread notifications or tab auto-opening.

### 5. Chat Logging & History (Original Viewer Parity)
- **SL Log Format Parity:** Appends messages asynchronously using standard viewer format: `[YYYY/MM/DD HH:MM:SS] Sender Name: Message`.
- **File Structure:** Separate log files per session:
  - Local Chat: `chat.txt`
  - IMs: `<Avatar_Name>.txt`
  - Group Chats: `<Group_Name>.txt`
- **Default Directory (User Space):**
  - Fallback default in OS user application data folder (e.g. `%APPDATA%\SLNG\logs\chat\` on Windows, `~/.config/slng/logs/chat/` on Linux).
- **Preferences Integration & Extensibility:**
  - Global toggle: `Enable Chat Logging` (default: On).
  - Path selector setting for custom log directory.
  - Extensible settings data structure for future preferences (timestamp format, per-chat logging flags, log retention/rotation).

## UX Design Proposal

*Prior art studied: `SLNGWindow.cs` (base shell), `PreferencesWindow.cs` (hand-built vertical
tab strip — the closest existing precedent for the Chat/Friends/Groups column),
`ItemPropertiesWindow.cs` / `ObjectEditWindow.cs` (sizing/margin conventions), and the current
M3-2 local chat, which lives as an inline `ChatBox`/`LogPanel` pair docked into `ButtonBar`
(`app/scenes/Boot.tscn` `VBoxContainer/LogPanel` + `VBoxContainer/ChatBox`, wired in
`Boot.cs`), not as a floating window. `GridSession` (`src/SLNG.Net/GridSession.cs`) today only
exposes `ChatMessageReceived`/`SendChat` — there is no IM, `FriendsManager`, or `GroupManager`
integration anywhere in `src/` yet. That gap directly shapes the phasing recommendation in §6.

### 1. Window shell & dual-axis tab composition

`ChatWindow : SLNGWindow`, registered as a new `ButtonBar` toolbar item (glyph `"chat"`,
replacing the current inline toggle — see §5 Migration). Reuses `PreferencesWindow`'s
left-column pattern verbatim for the outer axis, and adds a new horizontal chip-strip pattern
(no precedent yet) for the inner axis:

- **Default size:** 460 × 420, `CustomMinimumSize` 380 × 320 (small enough that the vertical
  strip + a horizontal chip row + a 3-line message log all still fit without crushing).
- **Default position:** `(16, 220)` — bottom-left quadrant, clear of `CameraHUD` (100,100,
  240×160 → ends at y=260, x=340) and `PreferencesWindow` (260,160, 520×360). At (16,220) the
  window's footprint is x:16–476, y:220–640, which doesn't overlap either. It sits low and left
  because that's where local chat has always lived in this client (the M3-2 inline box) and in
  SL/Firestorm muscle memory generally — keep chat's spatial "home" stable across the redesign.
  It's independent of `InventoryPanel` (800,100, 360×500) on the right side of a typical
  1920×1080 canvas.
- **Outer axis (vertical, left sidebar):** identical structural pattern to
  `PreferencesWindow._tabList` — a 140px-wide `PanelContainer` with the same
  `BgColor(1,1,1,0.03)` / right border `Color(1,1,1,0.08)` styling, a `VBoxContainer` of
  `ToggleMode` buttons using `PreferencesWindow.StyleTabButton`'s exact normal/hover/pressed
  StyleBoxFlats (rather than re-deriving new colors — one visual language for "which tab am I
  on" across every SLNGWindow). Three entries: Chat, Friends, Groups. `AddTab`-style API,
  mirrored 1:1 from `PreferencesWindow` so both windows are maintainable the same way.
- **Inner axis (horizontal, top of the Chat page only):** a new `ChatTabStrip` control — a
  fixed-height (30px) row living above the message log, *inside* the Chat page's content area
  only (Friends/Groups pages don't have one). Each tab is a chip: `ToggleMode` `Button` with
  `Text = displayName`, min-width ~90px, using the same three StyleBoxFlat states as the
  vertical strip but with `CornerRadiusTop*` only (rounded top, flat bottom, tab-like) instead
  of left-only rounding. `Main` is not closeable (no `(x)`); dynamic IM chips append a small
  `×` sub-button (14px, same styling as `SLNGWindow._closeButton`) at their right edge.
  Structurally: `HBoxContainer` of chips wrapped in a horizontal-only `ScrollContainer`
  (`vertical_scroll_mode = Disabled`) so it never fights the page's own vertical layout.

### 2. Chat tab

- **Message log:** `RichTextLabel`, `bbcode_enabled = true`, `scroll_following = true` — same
  control and cap discipline as the existing `Boot.LogMessage` (200-line cap per buffer, clear
  and restart rather than let it grow unbounded; `RichTextLabel` re-layouts on every append and
  an unbounded log is a real frame-time cost, per AGENTS.md's render-budget principle applied to
  UI). Each *tab* (Main, and each IM) owns its own buffer/cap independently — switching tabs
  swaps which buffer is bound to the visible `RichTextLabel`, it doesn't share one log.
  Formatting, on-screen (file format is separately specified in §5 of Functional Requirements
  and stays full-date): `[color=#888]HH:MM[/color] [b]Sender[/b]: message`, with system/status
  lines in orange and error/alert lines in the same red already used for other alerts, so a user
  moving between the old inline log and the new window sees continuous color semantics.
- **Input row:** `LineEdit` + `Send` `Button`, docked at the bottom of the Chat page,
  `SizeFlagsVertical = ShrinkEnd`; `Enter` submits (mirrors the existing `_chatInput.TextSubmitted`
  wiring exactly, just re-pointed at whichever tab is active).
- **Unread badge:** small circular badge, `BgColor(0.85, 0.25, 0.2, 0.9)` (matches the existing
  alert/red accent already used elsewhere for `[Alert]` lines), anchored to the chip's top-right
  corner, showing a count (`"9+"` past 9). Rendered only on *inactive* chips with unread > 0 —
  no badge, not even "0", when a chip is caught up. Clears the instant its chip becomes active.
- **Many IM tabs:** MVP is scroll-only (mouse wheel + drag on the chip strip) — no dropdown/
  overflow menu yet. This is the simplest thing that works and matches how the chip strip
  degrades gracefully; a "recent conversations" dropdown is a reasonable Phase 2 add if scrolling
  through a long row proves annoying in practice, but it's new UI with no precedent in this
  codebase and isn't needed for the near-term goal, so it's deliberately deferred (see §6).

### 3. Friends tab

- **List item:** `HBoxContainer` row — 8px presence dot (`Color(0.3,0.85,0.3)` online /
  `Color(0.4,0.4,0.4)` offline, same green/grey semantic as nothing-yet-established in this
  codebase but consistent with every SL-family viewer) — avatar display name label (dimmed to
  `Color(0.55,0.55,0.55)` when offline, matching `PreferencesWindow`'s unselected-tab font
  color) — right-aligned small icon `Button` (chat-bubble glyph, `MaterialSymbolsOutlined`,
  reusing the icon-font path already loaded in `ButtonBar._iconFont`) to open an IM directly.
  Double-clicking the row does the same thing (per functional requirement 3).
- **Sort:** online first, then alphabetical within each group — standard SL/Firestorm behavior,
  worth calling out explicitly since it's not stated in the functional requirements.
- **Empty state:** centered, muted (`Color(0.5,0.5,0.5)`) single line — "No friends yet. Add
  friends in-world to see them here." — no icon needed at this size; keep it text-only and quiet
  rather than inventing a new empty-state illustration pattern this codebase doesn't have
  elsewhere yet.

### 4. Groups tab

- **List item:** same row shape as Friends minus the presence dot — a 24px placeholder badge
  (colored circle with the group's first initial, since there's no group icon asset pipeline
  yet) — group name — secondary muted label for the member's role/title under the name (or
  inline, right-aligned, if vertical space is tight — implementation's call).
- **Empty state:** "You haven't joined any groups yet." — same muted single-line treatment as
  Friends, for visual consistency between the two "social" tabs.

### 5. Concrete states & migration

- **0 vs. many unread:** covered in §2 — no badge at 0, capped display at "9+".
- **Resize behavior:** the vertical tab strip (140px) and the horizontal chip strip (30px) are
  both fixed-size chrome; only the message log (`SizeFlagsVertical = ExpandFill`) grows/shrinks
  with the window. `CustomMinimumSize` (380×320) is the floor where that stops looking cramped.
- **Docked-next-to-other-windows default:** see §1 — (16,220) at 460×420 doesn't overlap
  `CameraHUD`, `PreferencesWindow`, or `InventoryPanel` at any of their own defaults.
- **Migration from M3-2:** the inline `ChatBox`/`LogPanel` in `Boot.tscn`/`ButtonBar` is retired,
  not kept alongside the new window. `Boot.OnChatMessage`/`ChatMessageReceived` gets re-pointed
  at `ChatWindow`'s Main-tab buffer instead of `_logPanel`; the `ButtonBar` "chat" toolbar entry
  toggles `ChatWindow.Visible` (opened to Chat/Main) instead of the inline box's parent
  visibility. Pre-login boot/connection diagnostics (`LoadingScreen`'s own log) are untouched —
  this migration is scoped to the post-login in-world chat surface only.

### 6. MVP phasing recommendation

Grounded in what already exists in `src/`: `GridSession` has `ChatMessageReceived`/`SendChat`
today and *nothing else* — no IM, no `FriendsManager`, no `GroupManager` wiring anywhere in the
codebase yet. That's the real gating factor, more than UI complexity.

**Phase 1 (this pass — serves "see avatars, chat" directly):**
- Window shell: `SLNGWindow` subclass, vertical Chat/Friends/Groups strip, horizontal chip strip
  (structural only — Main tab is enough to prove it).
- Chat → Main: full local-chat parity with today's inline box, migrated in (this alone closes
  the M3-2 replacement gap and is a strict win even before IM exists).
- Chat → dynamic IM tabs: buildable once `SLNG.Net` grows an IM send/receive path (net-layer
  prerequisite, not purely UI — flag this dependency explicitly in the net-side sub-tasks).
- Friends tab: UI (list, presence dot, sort, empty state, double-click→IM) is cheap to build now,
  but is inert without a `FriendsManager` wiring task on the net side — recommend landing that
  net task alongside this one, since "see other avatars are online" is squarely the stated
  near-term goal.
- Chat logging: Main-tab logging only (`chat.txt`) is enough for Phase 1 parity with SL viewer
  expectations; per-IM/per-group log files extend naturally once those tabs exist.

**Defer to a follow-up pass:**
- Groups tab end-to-end (needs `GroupManager` wiring, which serves group-chat/social polish more
  than the immediate "chat with people I can see" goal) — including the mute/ignore toggle,
  which the spec itself already marks Phase 2.
- Preferences integration for the log path/format settings (functional, but not blocking — a
  sane default path is enough until someone asks to change it).
- Horizontal chip-strip overflow dropdown (§2) — ship scroll-only first.

Nothing above removes anything from the Acceptance Criteria below; this is sequencing, not
scope-cutting — the full spec is still the target for `M5-3` overall.

### 7. Open questions / risks

- **Default position (16,220)** was chosen by checking against the *other* windows' hardcoded
  defaults, not against a live viewport — worth a quick visual sanity check once built, since
  none of these windows currently do responsive/viewport-relative positioning.

### 8. Resolved decisions (2026-07-21)

The open questions above (except the viewport-position sanity check, which just needs a visual
check once built) are resolved:

1. **IM/Friends/Groups net plumbing is split into its own work, but is needed soon — not a
   distant follow-up.** Land it as separate commits/PRs within this M5-3 effort, sequenced
   immediately after the window shell + Main-chat migration, not deferred to a later milestone.
   `FriendsManager` wiring in particular should land alongside the Friends UI since "see who's
   online" is the near-term goal driving this whole feature.
2. **Scroll behavior: pause while reading, don't force-follow.** Confirmed — see §2a above.
   `RichTextLabel.scroll_following` snaps unconditionally, so this needs custom handling: detect
   the user scrolling away from the bottom, stop auto-appending the view position (buffer still
   fills), show a "jump to latest" affordance, and only resume auto-follow once they scroll back
   to the bottom themselves (or click the affordance).
3. **Per-conversation history: rolling recent-lines view + full paginated History window.**
   Matches Firestorm: the live chat body only ever shows the last N lines of the current buffer;
   full history lives on disk (§5 log files) and is reached via the **History** button, which
   opens a small paginated viewer (§2a). This resolves the "does a closed IM tab keep history"
   question too — the authoritative record is the log file, not the in-memory buffer, so buffer
   lifetime in memory is an implementation detail (cheap to keep, cheap to discard) rather than a
   product decision.
4. **No cross-restart state.** `ChatWindow` always opens clean to Chat/Main on app launch — no
   persisted tab selection, no restored IM tabs. Simpler, and consistent with decision 3 (anything
   a user needs from before this session is one History click away, not resurrected automatically).
5. **`ChatLogger.cs` stays in `src/SLNG.Core/Services`** per the original proposal — plain file
   I/O is engine/protocol-agnostic by nature, so it satisfies `AGENTS.md`'s layering rule as long
   as its public API only takes neutral types (sender name, message, timestamp, a channel/kind
   enum) and never LibreMetaverse or Godot types. It now also needs a **paginated read-back API**
   (e.g. `GetPage(logFile, pageIndex, pageSize)`) to back the History viewer in §2a, not just
   append.

## Acceptance Criteria
- [ ] Window inherits from `SLNGWindow` and opens via shortcut or UI button.
- [ ] Vertical tabs (`Chat`, `Friends`, `Groups`) switch active panel cleanly.
- [ ] Horizontal tabs under `Chat` show `Main` as static default, with new IM tabs opening on message receipt or manual IM initiate.
- [ ] Friends list correctly renders online vs. offline status indicators.
- [ ] Group list displays user's joined groups.
- [ ] Group chat mute/ignore toggle supported per group (prevents notifications/auto-tab popups).
- [ ] Chat logging writes incoming/outgoing messages to text files in original SL viewer format (`[YYYY/MM/DD HH:MM:SS]`).
- [ ] Custom chat log folder configurable in Preferences, defaulting to OS user space.
- [ ] Live message log shows only the most recent lines per tab; scrolling up pauses auto-follow, with a "jump to latest" affordance to resume it.
- [ ] "History" control opens a paginated log viewer window reading the active tab's on-disk log file, independent of the live in-memory buffer.
- [ ] `ChatWindow` always opens fresh to the Chat/Main tab on app start — no tab selection or IM tabs persisted across restarts.
- [ ] All network callbacks from LibreMetaverse buffer events and update UI components on the Godot main thread without UI freezes.
- [ ] Unit / UI integration tests pass for tab management, message dispatching, and file logger output.

## Technical Specs & Affected Files
- `app/scripts/UI/ChatWindow.cs` — Main UI window class inheriting from `SLNGWindow`.
- `app/scripts/UI/ChatHistoryWindow.cs` — Paginated log history viewer (§2a), inherits `SLNGWindow`, read-only, backed by `ChatLogger.GetPage`.
- `app/scripts/UI/FriendsPanel.cs` — Friends list sub-component.
- `app/scripts/UI/GroupsPanel.cs` — Group list sub-component.
- `src/SLNG.Core/Services/ChatLogger.cs` — Async file logging service matching SL format; also exposes a paginated read-back API (`GetPage(logFile, pageIndex, pageSize)`) for `ChatHistoryWindow`.
- `src/SLNG.Net/GridSession.cs` — Event exposed for incoming IMs, friend status changes, and group updates.
- `docs/specs/M5-3-tabbed-chat-window.md` — Feature specification.

## Sub-tasks / Progress

**Phase 1 — window shell + Main chat (this pass, no net-layer prerequisites):**
- [x] Create `M5-3` spec & update `ROADMAP.md`
- [ ] Implement `ChatWindow` UI shell with vertical tab container (Chat/Friends/Groups) + horizontal chip strip
- [ ] Migrate `Main` local chat from the M3-2 inline `ChatBox`/`LogPanel` into the new window (retire the inline UI)
- [ ] Implement rolling recent-lines log with pause-on-scroll-up + "jump to latest"
- [ ] Implement `ChatLogger` service (SL format, user-space default path, async append + paginated read-back)
- [ ] Implement `ChatHistoryWindow` (paginated viewer, §2a)
- [ ] Connect Chat Logger & Chat Window options to Preferences

**Phase 1b — Friends (net + UI, sequenced immediately after shell, not deferred):**
- [ ] `SLNG.Net`: `FriendsManager` wiring (online/offline status) — new net sub-task
- [ ] Implement `Friends` panel with status indicators, wired to the above

**Phase 1c — IM (net + UI, needed soon per product priority):**
- [ ] `SLNG.Net`: IM send/receive plumbing — new net sub-task
- [ ] Implement dynamic horizontal IM tabs in `Chat` view, wired to the above

**Deferred follow-up pass:**
- [ ] `SLNG.Net`: `GroupManager` wiring
- [ ] Implement `Groups` panel
- [ ] Add Group Chat Mute/Ignore toggle per group (suppresses notifications & tab focus)

