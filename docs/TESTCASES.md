# Test cases for the open topics

As of: v0.26.77-alpha (2026-10-05). Applies to the features that are built but **not yet confirmed in real
use** (roadmap status 🧪 or 🚧, or a “Not yet seen” in the roadmap entry).

**How to test:** The build is `PurisViewer_Setup_v0.26.77-alpha.exe` from the release or `build/windows/` (local
export). The log is at `%APPDATA%\Godot\app_userdata\Puris Viewer\logs\godot.log` (the file of the last
session is called `godot<timestamp>.log`). A test case passes if **all** expectations hold. Enter the result in
the *Result* column (✅ / ❌ / ⏭ not tested) and for ❌ the observation, ideally with the last lines of the log and
a screenshot.

**Accounts:** For TC-GRID and TC-LOG you need the same name on **Second Life** and on **OSGrid**
(e.g. “Clifton Howlett” on both). For TC-ANGEBOT, TC-RECHTE and TC-PRAESENZ you need a second account or a second person (ideally with
Firestorm, so you can see the other side).

| Group | Topic | Roadmap | Status |
|---|---|---|---|
| TC-GRID | Data separated per grid | BUG-GRID-01 | 🚧 |
| TC-LOG | Chat logs like Firestorm | FEAT-UI-41 | ✅ |
| TC-KEY | Keyboard shortcuts and keyboard page | FEAT-UI-43 | ✅ |
| TC-INV | Inventory opens ready to search | FEAT-INV-14 | ✅ |
| TC-DPI | Scaling on a high-resolution display | FEAT-UI-42 | ✅ (remainder open) |
| TC-LAND | Land info on OpenSim and special cases | FEAT-LAND-01…05 | ✅ (remainder open) |
| TC-ANGEBOT | Teleport and friendship requests, special cases | BUG-NET-27/28 | ✅ (remainder open) |
| TC-AVATAR | “Mannequin” at login | BUG-AVATAR-05 | ✅ (not reproduced) |
| TC-WARN | Engine warnings in the full hub | BUG-RENDER-40 | 🚧 |
| TC-EQ | Dead EventQueue | BUG-NET-20 | 🧪 |
| TC-KAT | Friend categories, view switches, online dot | FEAT-UI-65 | 🧪 |
| TC-RECHTE | Friend rights as icons, username behind the name | FEAT-UI-35 | 🧪 |
| TC-REITER | Tabs in the communication window, unread counter on the chat tab | FEAT-UI-66 | 🧪 |
| TC-CHATFOKUS | Input field stays active after Enter | BUG-UI-27 | 🧪 |
| TC-PRAESENZ | “… is online” notices with names | BUG-UI-28 | 🧪 |

---

## TC-GRID — Data separated per grid (BUG-GRID-01)

Background: The same account name on two grids shared files (login image, object cache, map tiles,
chat logs). Now everything lives under `user://grids/<Grid>/…` (Windows:
`%APPDATA%\Godot\app_userdata\Puris Viewer\grids\`). The grid identifier for Second Life is `agni`, for OSGrid
`hg.osgrid.org`.

| ID | Steps | Expectation | Result |
|---|---|---|---|
| TC-GRID-01 | Log in with the account on **OSGrid**, look around briefly, **log out**. Then in the login window select the same account on **Second Life** (profile selection). | The login image is **not** the one from the OSGrid login (at first the default image, because none is saved for Second Life yet). | ☐ |
| TC-GRID-02 | Log in with the same name on **Second Life**, look around, log out. Then select both profiles alternately in the login window. | Each profile shows **its own** last saved image. There are two files under `grids\<Grid>\`. | ☐ |
| TC-GRID-03 | Log in to Second Life, change to a region, log out. Then look at the folders `grids\agni\cache\objects` and `grids\hg.osgrid.org\cache\objects`. | The object cache files are **in their own folder per grid**. The OSGrid folder contains no file from the other side and vice versa. | ☐ |
| TC-GRID-04 | On OSGrid visit the default region (1000,1000), then on Second Life “Da Boom” (also 1000,1000). Then OSGrid again. | When you come back, **neither side loses** its cached regions (log: `[ObjectCache] … hit=` on return, not “cache empty”). | ☐ |
| TC-GRID-05 | Open the world map / minimap on both grids. | The map tiles belong to the respective grid (no image from the other world). Folder `grids\<Grid>\cache\maptiles`. | ☐ |
| TC-GRID-06 | Log in to grid A, note the sky/weather, log out, log in to grid B **without a restart** (one without its own environment data, e.g. a local OpenSim). | The sky of grid A does **not** stay; the default day cycle applies. | ☐ |
| TC-GRID-07 | Hold a conversation on grid A, log out, log in to grid B (without a restart). | The chat does **not** show the tabs and lines of grid A. | ☐ |
| TC-GRID-08 | Preferences → Network: “Clear Asset & Object Cache”. | The size display covers **all** grids plus the old shared folder; after clearing it shows about 0. | ☐ |

---

## TC-LOG — Chat logs like Firestorm (FEAT-UI-41)

Preparation: In Firestorm there is a log folder for the account (example
`…\Firestorm_x64\clifton_howlett` or a folder of your own such as OneDrive). **Firestorm must not be running during the
tests.** Make a copy of the folder beforehand.

| ID | Steps | Expectation | Result |
|---|---|---|---|
| TC-LOG-01 | Open Preferences → **Chat logs**. | An account list with your saved profiles (“Name — Second Life”, “— OSGrid”). For the selected account: folder (or “SLNG's default folder”), “Choose...”, “Use default” and the “In effect” line with the account subfolder. | ☐ |
| TC-LOG-02 | For your Second Life account “Choose...” → select the **base folder** that Firestorm uses for this account (the folder **in which** `clifton_howlett` lies). | The dialog is the normal Windows folder dialog; after choosing, the path is in the field and the “In effect” line shows `<base>\clifton_howlett`. | ☐ |
| TC-LOG-03 | Log in, in the chat window open the conversation with a person you wrote to in Firestorm. | The **old history from Firestorm** appears (the last lines). Timestamps match Firestorm (SL time). | ☐ |
| TC-LOG-04 | Open the History window for the same person. | The history can be read, even with a large file (no freeze longer than about a second). | ☐ |
| TC-LOG-05 | Send a message to a person (or write in nearby chat). Log out. Open the file in the account folder (`<Name>.txt` or `chat.txt`). | The line is appended at the **end** in the format `[2026/10/03 11:09]  Name: Text` (two spaces after the bracket, UTF-8, without changing the older lines). | ☐ |
| TC-LOG-06 | Then start **Firestorm**, log in with the same account, open the same conversation. | The line written by SLNG is **visible** in the Firestorm history; older lines are unchanged; Firestorm reports no error. Then close Firestorm. | ☐ |
| TC-LOG-07 | Back in SLNG: open the conversation. | The line written in Firestorm appears in the SLNG history. | ☐ |
| TC-LOG-08 | Hold a **group** conversation; check the file name in the folder. | The file is called `<group name> (group).txt` (not a UUID). If the group name is not yet known when you open it, note it (known weakness, it separates the history from Firestorm). | ☐ |
| TC-LOG-09 | Preferences → Chat logs: switch the style of the IM file names (`First Last.txt` ↔ `first_last.txt`), log in again, write to a person. | The new line goes into the file with the **name that matches Firestorm's setting** (for you: `first_last.txt`). No second file is created for the same person. | ☐ |
| TC-LOG-10 | “Import old SLNG logs...” (only possible when logged in). | A confirmation window with numbers; after “OK” lines are **appended**, old files under `%APPDATA%\SLNG\logs\chat` remain **unchanged**. | ☐ |
| TC-LOG-11 | Run the import **a second time**. | **Nothing** more is appended (no duplicate lines). | ☐ |
| TC-LOG-12 | Choose **different** folders for the two accounts (Second Life / OSGrid), log in to both. | Each account writes to **its own** folder; `…\<name>.osgrid` for OSGrid. The account subfolder for OSGrid is called `first_last.osgrid`. | ☐ |
| TC-LOG-13 | “Use default” for an account. | “In effect” shows `%APPDATA%\SLNG\logs\chat\<account>` again; the folder chosen before stays untouched. | ☐ |
| TC-LOG-14 | Change the log folder and **log out**. | Logging out returns to the login window and does not hang (the fix BUG-NET-29 is confirmed; this case tests the combination). The log contains `[Logout] network logout finished …`. | ☐ |

---

## TC-KEY — Keyboard shortcuts and keyboard page (FEAT-UI-43)

Preparation: logged in, focus **not** in a text field (click the world once). Full list:
`docs/specs/FEAT-UI-43-keybindings.md`.

| ID | Steps | Expectation | Result |
|---|---|---|---|
| TC-KEY-01 | `Ctrl+I` | The inventory opens (see TC-INV). | ☐ |
| TC-KEY-02 | `F`, then `Home`, then `E` | `F` and `Home` toggle flying; `E` starts flying. | ☐ |
| TC-KEY-03 | `W`, `S`, `A`, `D`, arrow keys | Walking / turning as before. `Shift` + `W` = run. | ☐ |
| TC-KEY-04 | Hold `Ctrl`, press `W` | The avatar does **not** walk (modifiers must match exactly). | ☐ |
| TC-KEY-05 | Hold `Alt`, `A`/`D` | The camera orbits around the focus, the avatar does not move. | ☐ |
| TC-KEY-06 | Hold `Alt`, `W`/`S` | The camera zooms in / out. | ☐ |
| TC-KEY-07 | Hold `Alt`, `E`, then `C` | The camera orbits over, then under the focus. Direction as expected? (otherwise note it) | ☐ |
| TC-KEY-08 | Hold `Ctrl+Alt+Shift`, `W`/`A`/`S`/`D` or arrows | The camera shifts (pan). Speed comfortable? (estimated 1.5 m/s) | ☐ |
| TC-KEY-09 | `=` and `-` | The camera distance changes. Typed in a **text field**, `-` does **not** zoom. | ☐ |
| TC-KEY-10 | `Esc` | The camera is reset. | ☐ |
| TC-KEY-11 | In order: `Ctrl+P`, `Ctrl+T`, `Ctrl+H`, `Ctrl+Shift+F`, `Ctrl+Shift+G`, `Ctrl+M`, `Ctrl+Shift+M`, `Ctrl+Shift+S`, `Ctrl+K`, `Alt+Shift+N`, `Ctrl+Alt+H` | These open: Preferences, Communication window, Nearby chat, Friends, Groups, World map, Mini-map, Snapshot, Camera controls, Notifications, Hover height. A second press closes the window or brings it to the front. | ☐ |
| TC-KEY-12 | Open two windows, `Ctrl+W`, then `Ctrl+Shift+W` | First the top window closes, then all of them. | ☐ |
| TC-KEY-13 | `Ctrl+Shift+U` twice | The interface is hidden, then shown again. | ☐ |
| TC-KEY-14 | `Ctrl+0`, `Ctrl+8`, `Ctrl+9` | The field of view zooms in / out / back to the default. | ☐ |
| TC-KEY-15 | `Ctrl+R` | “Always run” toggles; the message shows the **current** shortcut (not a fixed “Ctrl+R”). | ☐ |
| TC-KEY-16 | `Alt+Shift+S`, `Alt+Shift+A` | Sit / stand; running animations stop. | ☐ |
| TC-KEY-17 | `Enter` (without focus in a field) | The chat input gets the focus. | ☐ |
| TC-KEY-18 | Type in the chat input field: `w a s d e c`, `Ctrl+C`, `Ctrl+V`, `Ctrl+A`, `Ctrl+Z` | The avatar does **not** walk; the text commands work in the field. `Ctrl+I` from the field still opens the inventory. | ☐ |
| TC-KEY-19 | `Ctrl+Q` | The exit procedure starts (logout screen, exit). Test this only when everything else is done. | ☐ |
| TC-KEY-20 | Open Preferences → **Keyboard**. | Actions by category (Movement, Camera, Windows, Avatar, View, Chat, Editing, Developer), search field, current keys. All texts in the selected language. | ☐ |
| TC-KEY-21 | Search field: enter “Inventory”. | Only matching actions stay visible. | ☐ |
| TC-KEY-22 | For “Inventory” click a key, press `Ctrl+B`. | From now on `Ctrl+B` opens the inventory, `Ctrl+I` no longer does. The menu hints (tooltips) show the new key. | ☐ |
| TC-KEY-23 | Assign `Ctrl+B` to another action. | A warning with the name of the action that already has the key; the choice **Replace / Cancel** works. | ☐ |
| TC-KEY-24 | Press `Esc` while rebinding. | Listening is cancelled; `Esc` itself cannot be assigned as a key. The avatar does not start walking while rebinding. | ☐ |
| TC-KEY-25 | `+` on an action (second key), `×` (remove key), the “reset” arrow, “Reset all” (two clicks). | Each action behaves as labelled; after “Reset all” the default keys apply again. | ☐ |
| TC-KEY-26 | Change a key, **restart** SLNG. | The change still applies. In `preferences.cfg` the section `key_bindings` contains only the difference (e.g. `window.inventory="Ctrl+B"`). After “Reset all” the section is empty. | ☐ |
| TC-KEY-27 | A game controller (if available): D-pad / stick. | It no longer moves the avatar (known change, untested). Only note it. | ☐ |
| TC-KEY-28 | Manual, section “Tastenkürzel” and “Noch nicht verfügbare Tastenkürzel”. | The list matches what works in TC-KEY-01…19. The list of missing shortcuts gives the reason in German. | ☐ |

---

## TC-INV — Inventory opens ready to search (FEAT-INV-14)

| ID | Steps | Expectation | Result |
|---|---|---|---|
| TC-INV-01 | Open the inventory via the **menu** (closed). | Window **open, not collapsed**, in front, the **cursor blinks in the search field**. Typing right away (“pants”) fills the search field. | ☐ |
| TC-INV-02 | Open the inventory via the **toolbar**. | as TC-INV-01 | ☐ |
| TC-INV-03 | Close the inventory, `Ctrl+I`. | as TC-INV-01 | ☐ |
| TC-INV-04 | **Collapse** the inventory with the “_” button, then `Ctrl+I`. | The window **expands**, comes to the front, cursor in the search field (and does not close). | ☐ |
| TC-INV-05 | Collapse the inventory, then menu / toolbar. | as TC-INV-04 | ☐ |
| TC-INV-06 | Inventory open, focus in the search field, `Ctrl+I`. | The window **closes**. | ☐ |
| TC-INV-07 | Inventory open, another window in front of it (focus there), `Ctrl+I`. | The inventory comes **to the front**, cursor in the search field; it does **not** close. | ☐ |
| TC-INV-08 | `Ctrl+O` (Outfits), also with a collapsed window. | The “Outfits” tab is open, window expanded, cursor in the search field. | ☐ |
| TC-INV-09 | Open the inventory, leave a term in the search field, close, open again. | Behavior is consistent (term kept or cleared – note what happens; cursor in the field in any case). | ☐ |
| TC-INV-10 | While typing in the search field, the keys `w a s d`. | The avatar does **not** walk. | ☐ |

---

## TC-DPI — Scaling on a high-resolution display (FEAT-UI-42, remainder)

Preparation: laptop with Windows scaling of 150% or 200%. Preferences → Display.

| ID | Steps | Expectation | Result |
|---|---|---|---|
| TC-DPI-01 | Fresh settings (or tick “Automatic (follows the display)”), start. | Login screen, menus, tooltips and text are **immediately at a readable size**; the display “Display scale detected: 150%/200%” and “Automatic (follows the display)” is ticked. | ☐ |
| TC-DPI-02 | Shrink the login window (small window, e.g. 1152×648) at scale 2.0. | The login panel may be **cut off** (known limit). Note whether logging in is still possible. | ☐ |
| TC-DPI-03 | Logged in: look at all HUD parts (top bar, bottom bar, statistics, chat, radar, name tags). | Everything is readable and **sharp**, nothing overlaps or lies outside the screen. | ☐ |
| TC-DPI-04 | Look at a **worn HUD** (attachment at the HUD point), click on it. | It is sharp (no blurring); a click hits the right element. | ☐ |
| TC-DPI-05 | Watch the 3D image. | The 3D world is still **at full resolution** (not softer than before). | ☐ |
| TC-DPI-06 | Preferences → Display: untick “Automatic (follows the display)”, drag the slider (releasing applies it). | The interface scales live; the saved scale stays after a restart. Your earlier value (125%) is kept as the **manual** value. | ☐ |
| TC-DPI-07 | Drag the window to a **second display with a different scale** (if available), “Automatic (follows the display)” on. | After up to about a second the scale adapts. (only note it if it does not work) | ☐ |
| TC-DPI-08 | Use a window of a **mirror / minimap / snapshot** feature. | Size values in the snapshot match **physical pixels**; no shifted mouse picking in the 3D world (a click on an object hits the right object). | ☐ |

---

## TC-LAND — Land info on OpenSim and special cases (FEAT-LAND-01…05)

World menu → “About Land...”. When you change parcels, the window follows.

| ID | Steps | Expectation | Result |
|---|---|---|---|
| TC-LAND-01 | **OpenSim:** on a parcel, **General** tab. | Name, owner, description and area are correct. Traffic shows “0” or “–” (OpenSim may deliver 0). No crash, no empty form. | ☐ |
| TC-LAND-02 | **OpenSim:** **Covenant** tab. | Either the covenant text, “There is no Covenant provided for this Estate.” or a clearly named failure (“could not be loaded”); **never** a silent blank. | ☐ |
| TC-LAND-03 | **OpenSim:** **Objects** tab on **your own** land, then on **someone else's**. | Own land: counters and owner list. Someone else's land: after about 10 s “The sim did not answer.” (OpenSim does not answer at all without the right). | ☐ |
| TC-LAND-04 | **OpenSim:** **Options**, **Media**, **Sound** tabs. | The checkmarks match the settings in the owner window (Firestorm); everything is read-only. | ☐ |
| TC-LAND-05 | **Second Life:** parcel with **media** and **music** (e.g. club, beach). Media and Sound tabs. | Type, Address, Description, Size (web content only), Loop (video/audio only) and the Music URL appear. | ☐ |
| TC-LAND-06 | **Second Life:** parcel with **group objects** (Objects tab). | Group owners have the type “Group”. Sorting by count is descending. | ☐ |
| TC-LAND-07 | **Second Life:** Objects tab on land where you have **no right**. | Either the list, “The sim did not list the owners.” or “The sim did not answer.” – **not** “None found.” if the counters show objects. | ☐ |
| TC-LAND-08 | A **large** parcel with more than about 54 owners. | The list is complete or fills in later (several reply packets). Note whether owners are missing. | ☐ |
| TC-LAND-09 | **Sound** tab, row “Restrict MOAP to this parcel”. | Shown as “unknown” **without a checkbox** (cannot be read), never as “off”. No vertically wrapping text. | ☐ |
| TC-LAND-10 | Walk between parcels, leave the window open. | The display follows the parcel; the **Objects** tab only re-queries when it is currently visible. | ☐ |
| TC-LAND-11 | Region with a **covenant** on Second Life, Covenant tab. | Text, “Last modified” in **your local time** (as Firestorm does it), estate name and owner. | ☐ |

---

## TC-ANGEBOT — Teleport and friendship requests, special cases (BUG-NET-27 / -28)

A second account or a second person is needed.

| ID | Steps | Expectation | Result |
|---|---|---|---|
| TC-ANGEBOT-01 | Have a teleport **offer** sent to you, accept it, then decline it a second time. | Window “TELEPORT OFFER” with the buttons *Teleport* / *Decline*; accepting teleports with the normal progress display. | ☐ |
| TC-ANGEBOT-02 | Accept a teleport offer **only after some time** (e.g. after 10 minutes or after the offerer has left). | A clear error message, **no hanging**, no second teleport display. | ☐ |
| TC-ANGEBOT-03 | Receive a teleport **request** (someone wants to come to you). | Window with *Offer teleport* / *Decline*; “Offer teleport” sends the person a teleport. | ☐ |
| TC-ANGEBOT-04 | Receive a friendship request, accept it. | The friend appears in the friends list **without a restart**. | ☐ |
| TC-ANGEBOT-05 | Receive a request, decline it. | It disappears; the sender is not told (that is how the viewer does it). | ☐ |
| TC-ANGEBOT-06 | Request **to an offline person** who accepts later (or the other way round: receive a request while you were offline, then log in and accept). | The flow works. If accepting fails: note the log (suspicion: missing parameter `agent_name` on the offline route). | ☐ |
| TC-ANGEBOT-07 | Your own request to someone who accepts. | Entry in notifications (“Denise Resident accepted your friendship offer.”). | ☐ |
| TC-ANGEBOT-08 | Someone removes you as a friend in Firestorm. | The friend **disappears** from your list; entry under **System** (“Denise Resident removed you as a friend.”). If you remove someone yourself, **no** message appears. | ☐ |
| TC-ANGEBOT-09 | Open a friend's profile. | Button “Remove Friend” (instead of “Friend ✓”); after confirmation it changes to “Add Friend”. | ☐ |
| TC-ANGEBOT-10 | Receive an offer from a **muted** person. | Known gap: SLNG does not decline automatically, as the viewer would. Only note what happens. | ☐ |
| TC-ANGEBOT-11 | Send an IM to an **offline friend** (Second Life). | Exactly **one** message (“User not online…” from the grid). With a grid that stays silent (OpenSim without an offline module), SLNG's own message appears after about 2.5 s. | ☐ |

---

## TC-AVATAR — “Mannequin” at login (BUG-AVATAR-05)

The bug was not reproduced, only inferred from the log. Observe, do not force it.

| ID | Steps | Expectation | Result |
|---|---|---|---|
| TC-AVATAR-01 | Log in **repeatedly** (5 times) on a full region (Millenium hub), especially with an empty object cache (Preferences → Network: Clear Asset & Object Cache) and with a slow connection. | Your own avatar has **skin, eyes and hair** (no white mannequin). | ☐ |
| TC-AVATAR-02 | On failure: search the log for `[SelfBake] channels`. | After the line with `(null)` a line **with channel numbers** (`8=…`) should follow. If it is missing, note the log and the time until the avatar appears, and reopen the ticket. | ☐ |

---

## TC-WARN — Engine warnings in the full hub (BUG-RENDER-40)

The goal is a log that names the cause.

| ID | Steps | Expectation | Result |
|---|---|---|---|
| TC-WARN-01 | Log in at the **arrival point** on Millenium (camera at about 133/120/28, many avatars) and stand still for about a minute. | The log contains **no** or only a few lines “Vector3 cannot be normalized”. If there is a flood, `[NonFiniteScan]` appears with it (with a count by node class). | ☐ |
| TC-WARN-02 | If warnings occurred: copy the lines `[EngineWarnings]`, `[NonFiniteScan]`, `[MeshGuard]` and `[ParticlesGuard]` from the log and send them. | This makes the cause determinable (“many CpuParticles3D” = particles; “Skeleton3D” = bones; “nothing found” = tangents in the engine). | ☐ |

---

## TC-EQ — Dead EventQueue (BUG-NET-20)

Only happens when a **neighboring region** restarts while you stay connected (or your own region with estate
rights, without logging out). Not an artificially reproducible test; only observe.

| ID | Steps | Expectation | Result |
|---|---|---|---|
| TC-EQ-01 | On a suitable event, search the log for `[EventQueue] … not recovering`. | Exactly **one** such line (not a flood of 65 repetitions) and a message in the client that the event queue is dead. | ☐ |

---

## TC-KAT — Friend categories, view switches, online dot (FEAT-UI-65)

**Friends** tab in the communication window. Best with at least five friends, two of them online.

| ID | Steps | Expectation | Result |
|---|---|---|---|
| TC-KAT-01 | Look at the list with no existing category. Then press the **+** next to the filter field, enter “Family”, confirm. | Before: a plain list without headings. After: there is the heading **Family** with “(0/0)”, below it (new) **No category** with all friends. | ☐ |
| TC-KAT-02 | Right-click a friend → **Family**. The same via the **Category...** button on the right. Then right-click → **New category…** → “Work”. | The friend is under the chosen category (in the menu the current one is ticked). “New category…” creates “Work” and sorts the friend into it **right away**. **No category** takes them out again. | ☐ |
| TC-KAT-03 | Click the heading of a category. Click again. Log out, log in again. | A click collapses (▶) and expands (▼); after the name it says “(online/total)”. The state is **the same** after the restart. | ☐ |
| TC-KAT-04 | Right-click the heading → **Rename…** (new name), then again with the name of **another** category. | The first name is accepted, the friends stay in it. With the duplicate name the prompt **comes back** and says that the name already exists. | ☐ |
| TC-KAT-05 | Right-click the heading → **Delete category**, first cancel, then confirm. | Cancelling changes nothing. Confirming removes the category; its friends stay in the list under **No category**. | ☐ |
| TC-KAT-06 | Create three categories. Drag one heading by the **grip** (six dots on the left) onto another — once upward, once downward. Also drag by the name. | The grip is visible, the mouse pointer turns into a hand. While dragging, a small blue card hangs on the pointer, and on the target heading a light bar shows **at the top** (lands above it) or **at the bottom** (lands below it). After releasing, the category sits there; the order stays after a restart. | ☐ |
| TC-KAT-07 | Drag a category onto **No category**. | It slides to the **end** of the categories. If it is already the last one, nothing happens and no bar appears. | ☐ |
| TC-KAT-08 | Right-click a heading → **Move up** / **Move down**. | Moves it by one place. On the topmost one “Move up” is greyed out, on the bottommost one “Move down”. | ☐ |
| TC-KAT-09 | Switch the **Show categories** checkbox off, on again. While it is off, create a new category with **+**. | Off: a plain list without headings (online first), the grouping is kept. Creating a category switches the checkbox back on **by itself**. | ☐ |
| TC-KAT-10 | Switch the **Show only online** checkbox on. Collapse a category, then switch the checkbox off and on again. After that (with a second account) leave all friends offline. | Offline friends and categories with nobody online disappear; collapsed ones stay collapsed. If nobody is online, it says “None of your friends are online.” The state stays after a restart. | ☐ |
| TC-KAT-11 | Type something into the **filter field** (part of a name), also with a collapsed category that contains a match. | Only categories with matches are visible, **all expanded**. After clearing the text the old state is back. A click on a heading does nothing meanwhile. | ☐ |
| TC-KAT-12 | Log in with a **second account** on the same machine. | This account has **none** of the first one's categories; what is created there does not appear for the first one. | ☐ |
| TC-KAT-13 | Look at the **online dot** in front of a friend, hold the mouse over it. | A clearly visible circle (green = online, grey = offline) the size of the icons next to it; the tooltip says “Online” / “Offline”. | ☐ |

---

## TC-RECHTE — Friend rights as icons, username behind the name (FEAT-UI-35)

A second account is needed, ideally with **Firestorm**, to see the other side. The three rights are called: **Online** (eye),
**Map** (pin), **Edit** (pencil). Blue = what **you** allow the friend (clickable), green = what the friend allows **you**
(not clickable). A right that is switched on is colored, a switched-off one is a pale icon.

| ID | Steps | Expectation | Result |
|---|---|---|---|
| TC-RECHTE-01 | Look at the friends list, move the mouse over the column headers; many friends, so that the list scrolls. | Five columns on the right: under **Friend may…** Online/Map/Edit, under **I may…** Map/Edit. The icons sit exactly **under** their headers, even with a scroll bar. Each header has a tooltip. | ☐ |
| TC-RECHTE-02 | Click a friend's blue **Map** icon. Check on the friend's side in Firestorm (friends list → your name → rights). | The icon turns blue, and on the friend's side it says that they may see you **on the map**. Clicking again takes it back. | ☐ |
| TC-RECHTE-03 | The same with **Online** (eye). | The friend may see / no longer see your online status. (Do not confuse it with the “Map” right.) | ☐ |
| TC-RECHTE-04 | Click the blue **Edit** icon, first **cancel**, then again and confirm. | A confirmation prompt appears (“Allow … to edit, delete and take your objects …”). Cancelling leaves the icon pale. Confirming colors it, and on the friend's side it says that they may edit your objects. **Taking it back does not prompt.** | ☐ |
| TC-RECHTE-05 | **Check the direction:** Give a friend only the **Map** right, nothing else. Check on this friend's side in Firestorm. | There **only** “can see me on the map” is set — not Online, not Edit. In **your** list only the blue pin is on. | ☐ |
| TC-RECHTE-06 | In Firestorm the friend gives you the right to see them on the map. | The green **Map** icon under **I may…** turns on **without a restart**, and in the notifications window under **System** it says “Denise Resident now lets you see where they are on the map.” | ☐ |
| TC-RECHTE-07 | The friend takes a right away from you again. | The green icon turns pale; entry under **System** (“… no longer …”). Your own changes to the blue icons trigger **no** such entry. | ☐ |
| TC-RECHTE-08 | Click a **green** icon, hold the mouse over it. | The click does nothing; the tooltip explains the right and says that only the friend can change it. | ☐ |
| TC-RECHTE-09 | Change two different rights of the same friend in quick succession. Log out, log in again. | After the restart **both** changes are still there (neither cancels the other). | ☐ |
| TC-RECHTE-10 | Look at a friend with a display name; then hold the mouse over the name; then under `Preferences` → `Display` switch off the option for usernames. | The username stands **behind** the display name, pale, in brackets, on the same line. Switched off, it appears only in the tooltip. Square brackets in the name are **not** read as formatting. | ☐ |

---

## TC-REITER — Tabs in the communication window, unread counter on the chat tab (FEAT-UI-66)

| ID | Steps | Expectation | Result |
|---|---|---|---|
| TC-REITER-01 | In turn click the **icon**, the **name** and the **edge** of a tab (Chat / Friends / Groups / Recent). | Every click switches the tab. Under the mouse the tab gets lighter, the mouse pointer is the hand. | ☐ |
| TC-REITER-02 | Open the **Friends** tab. Someone writes in **nearby chat** (Main selected). | A **red number** appears on the **Chat** tab; icon and name are tinted warm. | ☐ |
| TC-REITER-03 | Click back to **Chat**. | The number disappears as soon as the message is visible. | ☐ |
| TC-REITER-04 | Select an IM conversation, then open **Friends**; the person writes. | The Chat tab counts the message (also for the selected conversation). When you switch back it is read. | ☐ |
| TC-REITER-05 | Collect more than nine unread messages. | It shows “9+”. The number matches the one on the Chat button in the toolbar. | ☐ |
| TC-REITER-06 | **Close** the window (or minimize it) while someone writes in the **selected** conversation. | Unchanged from before: this line does not count as unread (known gap, see roadmap `FEAT-UI-66`). Only observe. | ☐ |

---

## TC-CHATFOKUS — Input field stays active after Enter (BUG-UI-27)

| ID | Steps | Expectation | Result |
|---|---|---|---|
| TC-CHATFOKUS-01 | In chat type a line, **Enter**, immediately type the next one — at least ten times in a row, with the keyboard only. | Every line lands in the input field; the cursor keeps blinking; the avatar does **not** start walking. You never have to click back in. | ☐ |
| TC-CHATFOKUS-02 | The same in an **IM** conversation and in group chat. | As above. | ☐ |
| TC-CHATFOKUS-03 | Press **Escape**, then W/A/S/D. | Escape leaves the chat bar; the avatar walks again. | ☐ |
| TC-CHATFOKUS-04 | Send with the **Send button**, then type. | Works as before. | ☐ |
| TC-CHATFOKUS-05 | In the **world map** type a name into the search field, **Enter**, then immediately type another name. | The second input lands in the search field without clicking in first. | ☐ |

---

## TC-PRAESENZ — “… is online” notices with names (BUG-UI-28)

A second account is needed; for TC-PRAESENZ-01 at least two friends who are online at login.

| ID | Steps | Expectation | Result |
|---|---|---|---|
| TC-PRAESENZ-01 | Log in while at least two friends are online. | The notices at the top right give **names** and never “Someone”. If the friend has a **display name**, **that** is shown, not the username (e.g. “Zora” instead of “anna.resident”). They appear up to a few seconds late if the names first have to arrive (the display name waits 2 s at most). | ☐ |
| TC-PRAESENZ-02 | Click a notice. | The IM with this friend opens. | ☐ |
| TC-PRAESENZ-03 | A friend logs in and out later (after your login, the names are long known). | Notices “Denise Resident is online.” / “Denise Resident is offline.” immediately and with names. | ☐ |
| TC-PRAESENZ-04 | Under `Preferences` → `Display` switch the notice off, a friend logs in. | No notice; the grey line in the open IM tab stays. | ☐ |

---

## After the test

Per group: If all cases are ✅, the roadmap ticket can be set to ✅ (the line in `docs/ROADMAP.md`
gets the sentence “Confirmed in-world <date>”). For ❌: send the log, the observation and, if possible, a screenshot to
the developer; the ticket stays 🧪 / 🚧.
