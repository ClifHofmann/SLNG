# [FEAT-UI-43] Keyboard shortcuts as in Second Life, in one rebindable table

- **Feature ID:** `FEAT-UI-43`
- **Track:** `ui` / `core`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Shortcuts used to be hard-coded where they happened (`Boot._Input`, `AvatarController`, `AvatarRenderer`,
`InventoryPanel`, plus polled movement and camera keys), with no overview, no conflict check and nothing the
person could change. Goal: one table of **actions** with stable ids, one list of **key chords** per action
whose defaults are the Second Life viewer's own, one **dispatcher** for discrete presses and one cheap
**held-key** lookup for the polled movement/camera keys, a **Keyboard** page in Preferences, and persistence of
only the differences from the defaults.

## Decisions

1. **Core / app split.** Everything that is not Godot is in `src/SLNG.Core/Input` and unit-tested: `KeyChord`
   (+ `KeyMods`, `KeyNames`), `KeyContext`, `KeyAction` / `KeyActions` (the catalog), `KeyBindingTable`
   (defaults + overrides + conflicts + persistence contract), `TextEditChords`, `SlShortcutGaps`. The app
   (`app/scripts/Input`) only maps Godot keys to chords (`GodotKeyMap`), dispatches (`KeyDispatcher`), answers
   "is it held" (`HeldKeys`), stores (`KeyBindings`), and draws the page (`KeyboardPreferencesPage`).
2. **Key identity is a name, not a Godot keycode.** `KeyChord.Key` is a canonical string (`I`, `F3`, `PageUp`,
   `Comma`, `KpAdd`), the text form is `Ctrl+Alt+Shift+Key`. The saved file, the table and the page never see
   a Godot enum, so a renumbered engine enum cannot corrupt a saved binding. `GodotKeyMap` matches
   `InputEventKey.Keycode` (the label on the person's layout, unchanged by Shift, so `Ctrl+Shift+1` is stable).
3. **Contexts are sets of focus states.** Four states are told apart: `World` (nothing focused / a HUD widget),
   `Window` (a control inside an `SLNGWindow`), `TextField` (a LineEdit/TextEdit), `ChatInput` (the chat bar's
   line). An action's context is the set it is live in (`Always` = all four, `NotInTextField` = World|Window,
   `World`, `ChatInput`). Two actions on one chord **conflict only when their contexts share a state** - so
   `Enter` may start chatting in the world and send inside the chat bar. The classification is the rule
   `AvatarController`'s movement gate already used (`BlocksMovement`), now shared (`KeyDispatcher.Classify`).
4. **Exact modifiers.** A chord matches only with exactly its modifiers: `W` walks, `Alt+W` moves the camera in,
   `Ctrl+W` is a shortcut. The one exception is `KeyAction.IgnoreExtraShift`, set on the six avatar movement
   actions, because Shift is SLNG's run modifier (Shift+W is still forward). It also widens the conflict rule:
   binding `Shift+A` to something else conflicts with `move.turn_left`.
5. **Two phases, picked by context.** A `World`-only action runs from `_UnhandledInput` (only if no control
   consumed the key - exactly where `AvatarController` handled Home/Esc/Ctrl+R), everything else from `_Input`
   (before the GUI - where `Boot._Input` ran). Handlers return `bool`: true consumes the key, false lets it
   fall through (the inventory's Ctrl+C returns false away from the panel).
6. **Handlers register, they do not poll.** Boot, `AvatarController`, `AvatarRenderer` and `InventoryPanel`
   call `KeyDispatcher.Register(owner, actionId, handler)`; a handler whose owner node was freed is dropped.
7. **Persistence** in `preferences.cfg`, section `key_bindings`, same `ConfigFile` pattern as `UiSettings` /
   `MediaSettings`: one key per customised action, `window.inventory="Ctrl+B;Ctrl+Shift+B"`, chords joined by
   `;`, an empty string = deliberately unbound. Only differences from the defaults are written, so an untouched
   install has no section, and a default changed in a later version reaches everyone who never customised that
   action. `Load` only reads; `Save` is called by the Keyboard page after a change - nothing on the boot path
   writes (a `--selftest` boots on the maintainer's real `user://`). The reader is tolerant by contract: an
   unknown action id (a file from another version), an unparsable chord, or a value of the wrong type is ignored;
   an entry whose chords ALL fail to parse keeps the default rather than silently unbinding the action.
8. **What a text field keeps for itself** (`TextEditChords.Owns`). The requirement is "typing never fires a
   shortcut". Interpretation, written down because it is a judgement: while a text field has focus an action
   fires only if (a) its context allows typing (`Always`) **and** (b) the chord is not one the field uses for
   editing. Owned = plain or Shift-only printable keys, Space, Enter, Tab, Backspace/Delete, caret and page keys,
   and with Ctrl: `A C V X Z Y`, Backspace/Delete, `Left/Right/Home/End/Insert` (also with Shift). So Ctrl+C in
   the chat bar is text whatever the inventory's Ctrl+C is bound to, but Ctrl+I, F3, Alt+Shift+N and Ctrl+Alt+R
   still fire from a text field, as every menu shortcut does in the reference viewer and as they always did in
   SLNG. Movement, camera, fly, Enter-to-chat and the editing actions are never live while typing. Known edge:
   Windows reports AltGr as Ctrl+Alt and some layouts type characters with it (`@` on a German keyboard is
   AltGr+Q); a Ctrl+Alt+letter chord is deliberately NOT treated as owned, which keeps Ctrl+Alt+R/T working
   while typing at the price that rebinding an `Always` action to such a chord fires it while typing that
   character.
9. **Esc is not capturable** in the rebind dialog (it cancels), and a bare modifier is ignored until a real key
   follows. Esc stays reachable through "reset". Mac Cmd / the Windows key (Meta) is out of scope: a key event
   with Meta held never matches. Mouse bindings (Alt+LMB orbit, the wheel) are not in the table.
10. **Menu hints are live.** `TopMenu` appends the current chord to the entries that have an action
    (`Always Run (Ctrl+R)`); the hard-coded hints were removed from the language files. The Keyboard page, the
    menus and the manual cannot disagree because there is one table.

## Action list (58 actions, generated from `KeyActions.cs`)

Context = where it fires; kind `held` is polled every frame (`HeldKeys`), `press` is dispatched once per key press
(never on repeat). `Source` is the reference-viewer file the default was verified against: `menu:<name>` is an
item of `menu_viewer.xml` (or `menu_edit.xml`), `key:<mode>:<command>` is a binding in `key_bindings.xml`.
SLNG-only defaults are listed under "SLNG extras".

| Action id | Category | Context | Kind | Default chords | Source |
|---|---|---|---|---|---|
| `move.forward` | Movement | World | held | W, Up | `key:third_person:push_forward` |
| `move.backward` | Movement | World | held | S, Down | `key:third_person:push_backward` |
| `move.turn_left` | Movement | World | held | A, Left | `key:third_person:turn_left` |
| `move.turn_right` | Movement | World | held | D, Right | `key:third_person:turn_right` |
| `move.up` | Movement | World | held | E, PageUp | `key:third_person:jump` |
| `move.down` | Movement | World | held | C, PageDown, Q | `key:third_person:push_down` |
| `move.toggle_fly` | Movement | World | press | F, Home | `key:third_person:toggle_fly` |
| `camera.alt.orbit_cw` | Camera | World | held | Alt+A, Alt+Left | `key:third_person:spin_around_cw` |
| `camera.alt.orbit_ccw` | Camera | World | held | Alt+D, Alt+Right | `key:third_person:spin_around_ccw` |
| `camera.alt.zoom_in` | Camera | World | held | Alt+W, Alt+Up | `key:third_person:move_forward` |
| `camera.alt.zoom_out` | Camera | World | held | Alt+S, Alt+Down | `key:third_person:move_backward` |
| `camera.alt.orbit_over` | Camera | World | held | Alt+E, Alt+PageUp, Ctrl+Alt+W, Ctrl+Alt+Up | `key:third_person:spin_over` |
| `camera.alt.orbit_under` | Camera | World | held | Alt+C, Alt+PageDown, Ctrl+Alt+S, Ctrl+Alt+Down | `key:third_person:spin_under` |
| `camera.pan_left` | Camera | World | held | Ctrl+Alt+Shift+A, Ctrl+Alt+Shift+Left | `key:third_person:pan_left` |
| `camera.pan_right` | Camera | World | held | Ctrl+Alt+Shift+D, Ctrl+Alt+Shift+Right | `key:third_person:pan_right` |
| `camera.pan_up` | Camera | World | held | Ctrl+Alt+Shift+W, Ctrl+Alt+Shift+Up | `key:third_person:pan_up` |
| `camera.pan_down` | Camera | World | held | Ctrl+Alt+Shift+S, Ctrl+Alt+Shift+Down | `key:third_person:pan_down` |
| `camera.dolly_in` | Camera | World | held | Equal, KpAdd | SLNG |
| `camera.dolly_out` | Camera | World | held | Minus, KpSubtract | SLNG |
| `camera.reset` | Camera | World | press | Escape | `menu:Reset View` |
| `window.inventory` | Windows | Always | press | Ctrl+I | `menu:Inventory` |
| `window.outfits` | Windows | Always | press | Ctrl+O | `menu:NowWearing` |
| `window.stats` | Windows | Always | press | Ctrl+Shift+1 | `menu:Statistics Bar` |
| `window.preferences` | Windows | Always | press | Ctrl+P | `menu:Preferences` |
| `window.conversations` | Windows | Always | press | Ctrl+T | `menu:Conversations` |
| `window.nearby_chat` | Windows | Always | press | Ctrl+H | `menu:Nearby Chat` |
| `window.friends` | Windows | Always | press | Ctrl+Shift+F | `menu:My Friends` |
| `window.groups` | Windows | Always | press | Ctrl+Shift+G | `menu:My Groups` |
| `window.world_map` | Windows | Always | press | Ctrl+M | `menu:World Map` |
| `window.mini_map` | Windows | Always | press | Ctrl+Shift+M | `menu:Mini-Map` |
| `window.snapshot` | Windows | Always | press | Ctrl+Shift+S | `menu:Take Snapshot` |
| `window.camera_controls` | Windows | Always | press | Ctrl+K | `menu:Camera Controls` |
| `window.notifications` | Windows | Always | press | Alt+Shift+N | `menu:Notifications` |
| `window.hover_height` | Windows | Always | press | Ctrl+Alt+H | `menu:Hover Height` |
| `window.close` | Windows | Always | press | Ctrl+W | `menu:Close Window` |
| `window.close_all` | Windows | Always | press | Ctrl+Shift+W | `menu:Close All Windows` |
| `avatar.always_run` | Avatar | Always | press | Ctrl+R | `menu:Always Run` |
| `avatar.stop_animations` | Avatar | Always | press | Alt+Shift+A | `menu:Stop Animating My Avatar` |
| `avatar.sit_stand` | Avatar | Always | press | Alt+Shift+S | `menu:Sit stand` |
| `avatar.rebake` | Avatar | Always | press | Ctrl+Alt+R | `menu:Rebake Texture` |
| `view.hide_ui` | View | Always | press | Ctrl+Shift+U | `menu:Hide UI` |
| `view.zoom_in` | View | Always | press | Ctrl+0 | `menu:Zoom In` |
| `view.zoom_out` | View | Always | press | Ctrl+8 | `menu:Zoom Out` |
| `view.zoom_default` | View | Always | press | Ctrl+9 | `menu:Zoom Default` |
| `app.exit` | View | Always | press | Ctrl+Q | `menu:Quit` |
| `chat.start` | Chat | World | press | Enter | `key:third_person:start_chat` |
| `edit.cut` | Editing | NotInTextField | press | Ctrl+X | `menu:Cut` |
| `edit.copy` | Editing | NotInTextField | press | Ctrl+C | `menu:Copy` |
| `edit.paste` | Editing | NotInTextField | press | Ctrl+V | `menu:Paste` |
| `dev.draw_distance_down` | Developer | Always | press | F3 | SLNG |
| `dev.draw_distance_up` | Developer | Always | press | F4 | SLNG |
| `dev.sun_gizmo` | Developer | Always | press | F5 | SLNG |
| `dev.nearby_objects` | Developer | Always | press | F6 | SLNG |
| `dev.avatar_shadows` | Developer | Always | press | F7 | SLNG |
| `dev.tpose` | Developer | Always | press | F8 | SLNG |
| `dev.ndotl` | Developer | Always | press | F9 | SLNG |
| `dev.wireframe` | Developer | Always | press | Ctrl+Shift+R | `menu:Wireframe` |
| `dev.create_test_skin` | Developer | Always | press | Ctrl+Alt+T | SLNG |

Labels: `ui.keys.action.<id with '.' -> '_'>` in `app/i18n/en-US.json` and `de-DE.json`; categories
`ui.keys.category.*`; modifier and key names `ui.keys.mod.*` / `ui.keys.key.*`. The selftest checks every one of
them exists in both locales.

### SLNG extras (defaults that are not the reference viewer's)
- `move.down`: `Q` (always crouched/descended in SLNG).
- `camera.dolly_in` / `camera.dolly_out`: `=`, `Num +` / `-`, `Num -` - the keyboard distance zoom that existed
  before the table.
- `dev.draw_distance_down/up`, `dev.sun_gizmo`, `dev.nearby_objects`, `dev.avatar_shadows`, `dev.tpose`,
  `dev.ndotl`: F3-F9 developer keys.
- `dev.create_test_skin`: `Ctrl+Alt+T`. **Collides with SL:** the viewer binds that chord to "Highlight
  Transparent", which SLNG does not have. Kept so the developer tool does not move; rebind either way.

## Second Life shortcuts SLNG leaves unbound (no feature - never faked)

Not in the catalog, so not rebindable and not shown as actions. Every chord below was checked to be a real
binding in the vendored viewer (a test does it when `scratch/slviewer` is present). Developer/Admin menu entries
(`Ctrl+Alt+Q` Develop menu, the `Ctrl+Alt+Shift+...` rendering-type toggles, `Ctrl+Alt+F1-F9` rendering
features, the consoles, the Linden-only admin chords) are not listed one by one: SLNG has neither menu.

| SL chord | SL command | Why SLNG leaves it unbound |
|---|---|---|
| `Shift+A` | Slide left (third person) | SLNG has no strafing: A/D turn the avatar and no sideways movement is sent. |
| `Shift+D` | Slide right (third person) | Same. |
| `Shift+Left` | Slide left (third person) | Same. |
| `Shift+Right` | Slide right (third person) | Same. |
| `Space` | Stop moving | No such command. |
| `M` | Mouselook | SLNG has no first-person / mouselook camera yet. |
| `Alt+Shift+F` | Joystick flycam | No joystick or flycam support. |
| `Ctrl+\` | Look at last chatter | SLNG does not track who spoke last. |
| `Shift+Enter` | Whisper (chat bar) | The chat bar has only say; no whisper or shout. |
| `Ctrl+Enter` | Shout (chat bar) | Same. |
| `Ctrl+Up` | Recall previous chat input | The chat bar keeps no input history. |
| `Ctrl+Down` | Recall next chat input | Same. |
| `Ctrl+Shift+I` | New inventory window | One inventory window only. |
| `Ctrl+G` | Gestures | No gestures window. |
| `Ctrl+F` | Search | No in-viewer search. |
| `Ctrl+Shift+A` | Nearby people | The nearby list is part of the mini-map window; Ctrl+Shift+M opens it. |
| `Ctrl+Shift+H` | Teleport home | SLNG can teleport home (the region-restart window offers it) but has no Teleport Home command to bind. |
| `Ctrl+B` | Build | The edit window opens from an object's context menu, not as a tool mode. |
| `Ctrl+1` | Focus tool | No build tool modes (Ctrl+1 .. Ctrl+5). |
| `Ctrl+L` | Link | Linking is not available. |
| `Ctrl+Shift+L` | Unlink | Linking is not available. |
| `Ctrl+Z` | Undo (objects) | No object undo; Ctrl+Z inside a text field is the field's own. |
| `Ctrl+Y` | Redo (objects) | Same. |
| `Ctrl+D` | Duplicate | Not available as a shortcut. |
| `Ctrl+E` | Deselect | Not available as a shortcut. |
| `Ctrl+A` | Select all | No object select-all; inside a text field it is the field's own. |
| `Delete` | Delete selection | Not available as a shortcut. |
| `Ctrl+.` | Select next part or face | No part-by-part selection. |
| `Ctrl+,` | Select previous part or face | No part-by-part selection. |
| `H` | Focus on selection | Not available. |
| `G` | Snap to grid | Grid snapping is set in the edit window. |
| `Ctrl+U` | Upload image | No upload dialog. |
| `Ctrl+Shift+Y` | Sun: midday | Time of day is set in the Environment window. |
| `Ctrl+Shift+N` | Sun: sunset | Same. |
| `Ctrl+Shift+O` | Sun: sunrise | Same. |
| `Ctrl+Shift+Z` | Sun: midnight | Same. |
| `Ctrl+Alt+Shift+N` | Show beacons | No beacons. |
| `Ctrl+Alt+Shift+P` | Show property lines | No property-line overlay. |
| `Ctrl+Alt+Shift+M` | Mute / unmute sound | SLNG plays no sound. |
| `Ctrl+Shift+2` | Scene load statistics | No such window. |
| `Ctrl+`` | Snapshot to disk | Use the Snapshot window (Ctrl+Shift+S). |
| `Alt+Shift+R` | Remove selected attachments | Detach is in the Avatar menu and the inventory. |
| `Ctrl+Alt+Q` | Develop menu | SLNG has no Develop/Admin menu; its developer tools are the F-keys and the Developer menu. |

## Wiki versus source (source wins)

The SL wiki list was the user-facing reference; where it disagrees with the viewer's own files the table follows
the files.

- The wiki lists `Ctrl+Alt+Left/Right` as orbit keys and `Ctrl+Alt+Up/Down` as orbit forward/back.
  `key_bindings.xml` (`third_person`) binds only `Ctrl+Alt+W/S/Up/Down` (spin over / spin under); `Ctrl+Alt+Left`
  and `Ctrl+Alt+Right` are unbound. The table follows the file.
- The wiki words `Alt+W/S` as "zoom"; the file's commands are `move_forward` / `move_backward` (the camera moves
  in / out) - the same thing.
- The wiki writes `Ctrl+Alt+Shift+D` for the Advanced menu; no `shortcut=` for it exists in `menu_viewer.xml`
  (the viewer enables it in code), so it is not treated as a verifiable default and is not in the catalog.
- The wiki's `Ctrl+Alt+Q` "Develop menu" is `menu_viewer.xml` "Debug Mode" (`QAMode`); same chord.
- Chat-bar chords (`Shift+Enter` whisper, `Ctrl+Enter` shout, `Ctrl+Up/Down` recall) are not in the XML files at
  all: they are in `llfloaterimnearbychat.cpp` / `llchatentry.cpp`. Confirmed there; SLNG has none of the three
  features (listed above).
- **Firestorm differences could not be established.** The vendored clone is Linden's own viewer; Firestorm's
  `key_bindings.xml` / `menu_viewer.xml` are not in the repository and no summary of them was trusted. Defaults are
  therefore the Linden viewer's; any Firestorm-only chord is a rebinding away. (The viewer's own rebind dialog,
  `llsetkeybinddialog.cpp`, also treats Esc as cancel - the same choice as here.)

## Behaviour changes versus before FEAT-UI-43

Every default that differs from what a key did yesterday:

1. **`F` now toggles fly** (the reference viewer's default; only `Home` did before).
2. **Movement and camera keys match modifiers exactly.** A movement key held with Ctrl no longer walks
   (`Ctrl+W/A/S/D/E/Q`, `Ctrl+arrows`, `Ctrl+PageUp/Down`); this is what makes `Ctrl+W` = Close Window and
   `Ctrl+Q` = Exit possible. Camera keys: `Ctrl+Alt+A/D/E/C` and `Ctrl+Alt+Left/Right/PageUp/PageDown` no longer
   spin the camera (the viewer binds Ctrl+Alt only to W/S/Up/Down), and `Alt+Shift+<movement key>` no longer
   moves the camera (was: Alt held, Shift ignored). Plain, Alt, Ctrl+Alt+W/S/Up/Down and Shift-as-run behave
   exactly as before.
3. **`Ctrl+Alt+Shift+W/A/S/D/arrows` now pan the camera** (the viewer's pan keys; new).
4. **Godot's built-in `ui_up/down/left/right/page_up/page_down` no longer drive movement.** They are the arrow and
   page keys (now table chords) **and the gamepad D-pad / left stick**: a gamepad no longer walks the avatar.
   SLNG has no other gamepad handling; not tested on hardware.
5. **The `=` / `-` camera-distance keys are now behind the focus gate** (they used to zoom while a "-" was typed
   into a text field).
6. **`Esc` (reset camera) and `Home`/`F` (fly) now need world focus.** They used to fire whenever no control had
   consumed the key, including with a slider or dropdown of an open window focused; now they behave like the
   movement keys and are blocked there. Clicking back into the world clears the stale focus, as before.
7. **Dispatched keys are consumed.** Boot's F3-F6, Ctrl+I, Ctrl+O, Ctrl+Shift+1, Ctrl+Alt+R/T never marked the
   event handled; they now do (nothing else wanted them).
8. **Menu text.** `Always Run (Ctrl+R)` and the other hints are now generated from the table; the fixed
   "(Ctrl+R)" was removed from the chat toast `Always Run: Enabled` and the Preferences label.
9. **New shortcuts for things SLNG already had** (all verified against the viewer's files, none verified in a
   running session): Preferences `Ctrl+P`, Communication window `Ctrl+T`, Nearby Chat `Ctrl+H`, Friends
   `Ctrl+Shift+F`, Groups `Ctrl+Shift+G`, World Map `Ctrl+M`, Mini-map `Ctrl+Shift+M`, Snapshot `Ctrl+Shift+S`,
   Camera Controls `Ctrl+K`, Notifications `Alt+Shift+N`, Hover Height `Ctrl+Alt+H`, Close Window `Ctrl+W`,
   Close All Windows `Ctrl+Shift+W`, Stop Animations `Alt+Shift+A`, Sit/Stand `Alt+Shift+S`, Hide All Controls
   `Ctrl+Shift+U`, Zoom In/Out/Default (field of view) `Ctrl+0` / `Ctrl+8` / `Ctrl+9`, Exit `Ctrl+Q`, Start chatting
   `Enter` (world focus), Wireframe `Ctrl+Shift+R`.

## Acceptance Criteria (from the roadmap row)
- [x] (1) One table of actions (stable ids, category, en/de label) and one list of chords per action (several
      allowed), defaults = the viewer's own, for every action SLNG has a feature for; the rest listed as unbound.
      *Gaps inside the table:* the edit actions are Cut/Copy/Paste on the inventory only (SLNG has no object
      undo/redo/duplicate/select-all); no build-tool, mouselook or chat-bar whisper/shout/recall shortcuts
      (no such features).
- [x] (2) One dispatcher replaces the scattered key checks; the polled movement/camera code asks the same table.
      Left on purpose: `ChatWindow`'s Esc (the chat bar's own - it releases focus and must stop the event reaching
      the camera reset), `FreeCamera`'s debug WASD, the mouse modifiers (Alt+LMB orbit, Ctrl/Shift in the gizmo).
- [x] (3) Typing in a text field never fires a shortcut except those the field itself does not own (see decision 8).
- [x] (4) Keyboard page: actions by category, current chords, rebind by pressing, conflict warning with Replace /
      Cancel, reset one / reset all (two clicks), search box, only differences saved.
- [x] (5) Pure logic in `SLNG.Core` with tests; the table is checked against `key_bindings.xml` and
      `menu_viewer.xml`.
- [x] (6) German manual lists the defaults (section "Tastenkürzel").
- [x] Unit tests (chord parse/format round trip, no duplicate default chord in overlapping contexts, override
      applies/clears, only differences saved, unknown ids ignored, future-version file does not crash, viewer
      parity) and selftest checks (dispatcher routing, text-field gating, held keys, page, in-memory persistence,
      labels in both locales).

## Technical Specs & Affected Files
- `src/SLNG.Core/Input/` - `KeyChord`, `KeyNames`, `KeyContext`, `KeyAction`, `KeyActionIds`, `KeyActions`,
  `KeyBindingTable`, `TextEditChords`, `SlShortcutGaps`
- `tests/SLNG.Core.Tests/` - `KeyChordTests`, `KeyBindingTableTests`, `KeyBindingsViewerParityTests`
- `app/scripts/Input/` - `GodotKeyMap`, `KeyDispatcher`, `HeldKeys`, `KeyBindings` (+ `KeyChordText`)
- `app/scripts/UI/KeyboardPreferencesPage.cs`; `PreferencesWindow` tab added in `Boot.SetupButtonBarAndPreferences`
- `app/scripts/Boot.KeyActions.cs` (Boot's handlers; `Boot._Input` removed), `AvatarController.cs`,
  `AvatarRenderer.cs`, `UI/InventoryPanel.cs`, `UI/ChatWindow.cs` (pages + chat-input group), `UI/SLNGWindow.cs`
  (`CloseFromTitleBar`), `UI/TopMenu.cs` (live hints)
- `app/i18n/en-US.json`, `de-DE.json`, `docs/BENUTZERHANDBUCH.md`

## Not verified (cannot be, without a real session)
The selftest drives synthetic `InputEventKey`s into `KeyDispatcher.Dispatch` (the code `_Input` /
`_UnhandledInput` call) and a fake keyboard into `HeldKeys`; it does not prove that the engine delivers a real key
press to the dispatcher, that a window toggle looks right, that the pan keys feel right (`PanKeySpeed` = 1.5 m/s x
the pan-speed setting is a guess), that Ctrl+W / Ctrl+Q behave in a running session, or that the Keyboard page
looks right in the Preferences window at 100 % / 200 %.

## Sub-tasks / Progress
- [x] Core model, defaults, conflicts, persistence, tests
- [x] Dispatcher + registration in Boot / AvatarController / AvatarRenderer / InventoryPanel
- [x] Polled movement and camera keys through `HeldKeys`
- [x] Keyboard page, persistence, live menu hints
- [x] Shortcuts for existing windows and commands (Phase 2, where the wiring was one call)
- [ ] In-world confirmation (maintainer): the list under "Behaviour changes", plus each new window shortcut
- [ ] Later: Shift+A/D slide, mouselook, look at last chatter, chat-bar whisper/shout/history, object undo/duplicate/
      deselect, gestures, search, teleport home, build tool modes - each needs its feature first
