# [FEAT-UI-41] Chat logs in Firestorm's folder layout and line format

- **Feature ID:** `FEAT-UI-41`
- **Track:** `ui` | `core`
- **Status:** `🧪 Review` (built and verified offline against the maintainer's real Firestorm files; not yet run in-world, and Firestorm has not been run against a file SLNG wrote)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

The maintainer, 2026-10-02: "the logging must be compatible with Firestorm so I can switch between the two
viewers and still see the old logs and chats". So SLNG writes **the same folder, the same file names, the same
line format** as Firestorm, and reads what Firestorm wrote, in the History viewer and in the preload of an open
conversation. This supersedes the chat-log layout of [BUG-GRID-01](BUG-GRID-01-per-grid-data-separation.md)
(`<chat root>/<grid slug>/<account>/`): Firestorm's folder name already carries the account and the grid.

Sources: Firestorm `FirestormViewer/phoenix-firestorm` `master` as fetched 2026-10-02 (cited as `file:line`), the
Linden viewer in `scratch/slviewer` for the parts Firestorm did not change, and **real files** from the
maintainer's Firestorm profile (`%APPDATA%\Firestorm_x64`, and `C:\Users\cid80\OneDrive\Firestorm`, see below).
Real lines used in tests have their people and ids replaced; nothing from those folders is in the repo.

## The rules (each verified against source and against real files)

| # | Rule | Source | Real evidence |
|---|---|---|---|
| 1 | **Base folder**: the account's setting `InstantMessageLogPath` (per-account `settings_per_account.xml`); empty means the profile folder `%APPDATA%\Firestorm_x64`. A configured path that does not exist is ignored (FIRE-18247). | `llstartup.cpp:1601-1616` | The maintainer's Second Life account points at `C:\Users\cid80\OneDrive\Firestorm` (5332 files there, written today); the OSGrid and other accounts use the profile folder. |
| 2 | **Account folder** = `<first>_<last>` lower-cased, spaces as `_`; plus `.<label>` (label lower-cased, spaces as `_`) unless the label is empty or `second_life`. A single-name account is `<name> Resident`. | `llstartup.cpp:1547,1629`; `lldir.cpp:1054-1075`; userid in `llsechandler_basic.cpp` `userID()` (Linden: `llsechandler_basic.cpp:1870`) | `clifton_howlett`, `clifton_howlett.osgrid`, `reamon_bullmer.osgrid`, `cilian_dupont.alife_virtual`, `test_user.localhost`, `denise1976_resident` |
| 3 | The suffix is the grid **label** = the grid's `gridname`, **not** its nick. | `fsgridhandler.cpp:367-372` (`gridname` -> `GRID_LABEL_VALUE`), `:673-677` (no gridname -> the grid text, lower-cased), `:951` `getGridLabel` | `grids.user.xml`: "Alife Virtual" has nick `AV`, the folder is `.alife_virtual`; "OSGrid"/`osgrid`; "localhost"/`localhost`. Linden: "Second Life" (no suffix), "Second Life Beta" (`.second_life_beta`, from `grids.remote.xml`; not seen on disk). |
| 4 | **Nearby chat** is `chat.txt`; **group chat** is `<group name> (group).txt`; **IM** is named after the partner (rule 5); `.txt` added after cleaning. | `lllogchat.cpp:60` `GROUP_CHAT_SUFFIX`, `:298-335` `makeLogFileName`; `llimview.cpp:1617-1683` | `Firestorm Support Deutsch (group).txt`, `Howletts (group).txt`, `chat.txt` |
| 5 | **IM file stem** (`UseLegacyIMLogNames`, a *global* setting in `user_settings/settings.xml`, **default ON** in `settings.xml`): ON = the legacy name with ` Resident` cut off (`Pink Ice`); OFF = `buildUsername` = `first.last` lower-cased (`pink.ice`). | `llimview.cpp:1646-1672`; `llcachename.cpp` `buildUsername` (Linden `llmessage/llcachename.cpp:526`); setting in `app_settings/settings.xml` | The maintainer's `settings.xml` has `UseLegacyIMLogNames = 0` (stored because it differs from the default): his logs are `pink_ice.txt`, `bronte_aichi.txt`, `neelep.txt` (Resident dropped). His **older** SL files (2017-2020) are legacy style (`AdalynHope.txt`, `Agatha McMahn.txt`). |
| 6 | **`cleanFileName`**: every one of `"'\/?*:.<>|[]{}~` becomes `_`. The **dot** is why `pink.ice` is `pink_ice`. Parentheses and spaces survive. | `lllogchat.cpp:359-369` | `pink_ice.txt`; `Firestorm Support Deutsch (group).txt`; `! The Couple_s Society ! (group).txt` (an apostrophe became `_`) |
| 7 | **Optional date suffix** (per-account `LogFileNamewithDate`, default off): `name-YYYY-MM` for conversations, `chat-YYYY-MM-DD` for nearby chat, local time, before cleaning; the reader falls back to the newest dated file, then the plain one. | `lllogchat.cpp:310-324`, `:721-` `oldLogFileName` | `Adora86-2018-01.txt`, `chat-2018-02-04.txt` in the OneDrive folder (an old setting; current accounts have it off). |
| 8 | **Line** = `[<stamp>]  <sender>: <text>`: stamp in brackets, **two spaces**, the sender with every `:` written `%3A`, `: `, the text. A system line is sent by `SYSTEM_FROM`. | `lllogchat.cpp:1041-1097` (`LLChatLogFormatter::format`), `:461-475` | `[2026/04/11 08:07]  Zeta Resident: ...` |
| 9 | **System sender**: `Second Life` on a Linden grid, **`Grid`** on an OpenSim grid. | `llinstantmessage.cpp:51`; `llworld.cpp:200` (the OpenSim branch) | `Second Life: ... ist online.` in a `_resident` folder; `Grid: ... Freundschaft an` in an `.osgrid` folder |
| 10 | **Stamp**: `[yyyy/MM/dd HH:mm]`, 24 h, minute precision by default (`LogTimestamp`, `LogTimestampDate` on; `FSSecondsinChatTimestamps` off; `Use24HourClock` on). The reader accepts the date or not, `:ss`, and `AM`/`PM`. | `lllogchat.cpp:371-413`, regexes `:87-93` | every real line |
| 11 | **The stamp is Second Life time (Pacific), not local time.** Every `TimeHour`, `TimeMin`, `TimeYear` ... string is `...,datetime,slt`. Summer time comes from the login flag `daylight_savings`. | `xui/en/language_settings.xml:38-47`; `llstring.cpp` `formatDatetime` (the `slt` branch subtracts the Pacific offset); `llstartup.cpp:4025-4033` | A group line `08:07` beside a `conversation.log` entry of `1775920079` = 15:07:59 UTC, written on a machine in Germany: UTC-7. Same on OSGrid (`1782735928` = 12:25 UTC, line `05:2x`). |
| 12 | **Multi-line message**: every `\n` in the text is written `\n ` -- each continuation line starts with one space, a blank paragraph line is a single space. The reader strips that space and appends `\n` + the line to the previous message. | `lllogchat.cpp:1094`, `:590-595` | `...an.` CRLF ` ` CRLF ` Will you be my friend?` CRLF ` ` CRLF ` (Standard...)` |
| 13 | **Encoding and line ends**: UTF-8 **without BOM** (a BOM is tolerated and stripped per line on read); the file is written through a text-mode stream, so on Windows **every** newline, including the ones inside a multi-line message, is **CRLF**. | `lllogchat.cpp:436` (`llofstream`, `std::ios_base::app`), `:477` `std::endl`, `remove_utf8_bom` `:140`, `:575` | `0d 0a` throughout, `ü` is `c3 bc`, no `ef bb bf` |
| 14 | **Append, never rewrite**: open for append per message, close. Reading: the last 20480 bytes (`LOG_RECALL_SIZE`), first partial line dropped; a file smaller than that is read whole. | `lllogchat.cpp:55`, `:436`, `:554-573` | the biggest real file is 23 MB (`bronte_aichi.txt`) |
| 15 | **A name is everything before the first `:`** (percent-decoded); a line with no colon has no name; a nameless line containing a colon reads as a sender. | `lllogchat.cpp:100` `NAME_AND_TEXT`, `:1099-` `parse` | |
| 16 | **`conversation.log`**: `[<unix seconds>] <type> 0 <offline> <name%20encoded>| <partner uuid> <session uuid> <file name encoded>|` (type 0 = IM, 1 = group, 2 = ad-hoc). **Read once at login into memory, rewritten whole (`"wb"`) when logging is switched off, on `cache()`, and when an entry is older than `FSConversationLogLifetime`.** | `llconversationlog.cpp:517-568` (`saveToFile`), `:569-664` (`loadFromFile`), `:232-255` | e.g. `[1782735928] 0 0 0 Pink| <uuid> <uuid> pink.ice|` |

## Decisions

**Folder (b).** Preferences -> **Chat logs** (new tab; the Network tab is about caches). Three choices:
*Firestorm's folder* (rule 1, read from the other viewer's own settings, so SLNG ends up in the exact folder
Firestorm uses for that account, incl. the maintainer's OneDrive one), *SLNG's own folder*
(`%APPDATA%\SLNG\logs\chat`, Firestorm's layout inside), *a folder of the person's choice*. The page shows the
folder in use for the running session, or the base folder it will use. **Default decided once, at the first login**
(`ChatLogSettings.DecideIfNeeded`): Firestorm's profile folder if it exists, else SLNG's own. It is written down
then, so installing Firestorm later does not make SLNG silently switch to a second history. Nothing is written on
the boot path (`--selftest` boots against the real data): until then the default is only computed.

**IM file names (c).** A second setting: *as Firestorm is set up* (default: reads `UseLegacyIMLogNames` from
`user_settings/settings.xml`; absent = legacy, as the source's default), *legacy* or *account*. SLNG's own folder
without Firestorm gets legacy names, which is also what SLNG's own old files looked like.

**Grid label (a).** SLNG only knows the login URI. Order: the two Linden hosts (`Second Life`, `Second Life
Beta`); **Firestorm's own grid lists** (`user_settings/grids.user.xml`, `grids.remote.xml`), matching the URI's
host+port against each entry's key, `loginuri`, `gatekeeper` and `name` (so SLNG's `hg.osgrid.org` finds OSGrid
through the gatekeeper, and `127.0.0.1:9000` finds `localhost:9000`); the grid's `get_grid_info` `<gridname>`,
one GET with a 3 s timeout at login and only for a grid in neither list (`GridInfoProbe`); finally the host (and
a non-default port), which is the viewer's own fallback. Checked: `hg.osgrid.org/get_grid_info` and
`login.osgrid.org/get_grid_info` both answer `<gridname>OSGrid</gridname>`. `GridIdentity` slugs are SLNG's own and
are **not** used for these folder names. Where the viewer would produce a folder name Windows rejects (a label
`host:port`), `:` and the like become `_`.

**Writer (c).** `ChatLogger` appends exactly Firestorm's record: stamp in Second Life time (US Pacific rule, see
`SecondLifeTime`), minute precision, two spaces, `%3A`-encoded sender, continuation lines with a leading space,
UTF-8 without BOM, the platform's newline for **every** newline. One write per record; the file is never
rewritten. Safety beyond the viewer: if a file ends without a newline (a crash), a newline is written first.
An empty sender is a system line and is written under `Second Life` / `Grid`.

**Reader (c).** `FirestormLogFormat.Parse` is the **only** parser. `GetPage` (History viewer) and `GetTail`
(preload of an open tab) both use it. It reads Firestorm's lines, multi-line messages as one message, and SLNG's
older shapes (`[2026/09/12 18:58:51] Name: text`, `[2026.07.21 13:19:00] ...`). The preload reads only the tail
of the file (real logs are tens of megabytes); the History viewer parses the whole file once (649 ms for the 24 MB
file, about 300 000 messages) and keeps the last parse until the file changes. Differences from the viewer, on
purpose: an empty line is skipped (the viewer makes an empty message).

**Old logs (d).** Never deleted or moved. **Preferences -> Chat logs -> "Import old SLNG logs..."** (needs a login:
the history belongs to the logged-in account) reads the loose files in `%APPDATA%\SLNG\logs\chat` and v0.26.13's
`<grid>\<account>\` folder, shows how many messages would be imported (confirm window), then **appends** to the
Firestorm-layout files. Decided against merging by timestamp: that rewrites a file the other viewer owns. So:
append, and when the destination already has content put **one marker line first** (`Second Life: SLNG: n
message(s) imported from an older SLNG log (from to).`). Idempotent: a message already in the destination (same
stamp, sender, text; counted per occurrence so two identical messages in a minute survive) is not added again, and
nothing is written (not even the marker) when nothing is new. Times are converted from the machine's local zone
to Second Life time, to the minute; `System` becomes the grid's system name. An old file becomes nearby chat
(`chat.txt`), an IM when its name equals one of its senders, else a group named `<stem> (group).txt` -- **a guess**;
an IM in which the partner never spoke is filed as a group. Files named by a bare UUID are skipped and counted:
these are group tabs whose name was not yet known when the tab opened (`ChatWindow.GetOrCreateGroupTab` falls back
to the group id), so the name is not in the file. The date suffix is not applied: imported history goes to the
plain file name.

**`conversation.log` (e).** SLNG has no conversation list, so it neither reads nor **writes** the file. **Leaving it
alone cannot corrupt Firestorm**: the viewer loads it into memory at login and rewrites the whole file itself, so a
file SLNG never touched is simply the viewer's own. The cost: conversations held only in SLNG are not in
Firestorm's conversation list (their transcript files are in the folder; from the source, Firestorm finds a
transcript by file name when the conversation is opened -- not run). Writing it would mean reproducing the encoded
names and both ids for every conversation and racing the viewer's in-memory copy: deliberately not done.

**Both viewers at once (f).** Not supported; documented in the manual and on the Preferences page, no lock file
(a lock only helps if the other viewer honours it; whether Firestorm does was not checked, and a check on SLNG's side alone
would not stop Firestorm). Each viewer opens, appends and closes per
message, so alternating is safe; running both on one folder can interleave appends to the same file and loses
the in-memory `conversation.log` state of whichever exits first.

**Not mirrored from Firestorm's settings (on purpose).** `LogNearbyChat`, `KeepConversationLogTranscripts`,
`LogTimestamp`, `LogTimestampDate`, `FSSecondsinChatTimestamps`, `Use24HourClock`: SLNG always logs (its own
`ChatLogger.Enabled` switch, not yet in Preferences) with the default stamp, which Firestorm reads under every
setting of those. Only what changes *where* and *under which name* a file lives is mirrored (rules 1, 5, 7).

## Layering

- **`SLNG.Core/ChatLogs/`** (pure, tested): `FirestormLogLayout` (folder and file names), `FirestormLogFormat` +
  `ChatLogEntry` (line format, the one parser), `SecondLifeTime`, `GridLabels`, `LlsdSettingsFile`,
  `ChatLogLocator` (+ `ChatLogMode`, `ImNamesChoice`, `ImLogNameStyle`, `ChatLogNaming`, `ChatLogTarget`),
  `ChatLogImporter` (+ plan types). `ChatLogger` (`Services/`) is the file IO.
- **`SLNG.Net/GridInfoProbe`**: the one HTTP GET; no LibreMetaverse type.
- **`app/`**: `ChatLogSettings` (preferences.cfg), `ChatLogPaths` (feeds the machine's folders in), `Boot`
  (resolves at login, before the login request), `ChatLogPreferencesPage`, `ChatWindow` (preload uses `GetTail`;
  system notices have an empty sender), `GridData.LegacyChatLogDirectory` (import source only).
- Nothing in `src/` references Godot.

## Acceptance Criteria

- [x] Folder per login by Firestorm's rule, verified against `lldir.cpp` / `llstartup.cpp` / `lllogchat.cpp` /
      `fsgridhandler.cpp`, incl. the grid label for OpenSim grids (rules 2, 3; the label lookup above).
- [x] Base folder is a setting in Preferences; default Firestorm's folder when it exists, SLNG's own otherwise;
      decided once at the first login, never silently a second history.
- [x] History viewer and preload read Firestorm's files and SLNG's older ones through one parser; what SLNG
      writes has Firestorm's names, line format, encoding, line ends; append only.
- [x] Old SLNG logs found and importable on request (append + marker, idempotent), never moved or deleted.
- [x] `conversation.log` left alone, documented, with the statement above.
- [x] A unit test per format rule using real (anonymised) lines, a writer/reader round trip, appended bytes
      unchanged, import idempotence; selftest check `chat log paths`.
- [x] `AppVersion` v0.26.13-alpha -> v0.26.14-alpha; manual (German) and BUG-GRID-01 spec updated.
- [ ] **Firestorm run against a file SLNG wrote** (open the conversation in Firestorm and check the lines and the
      times) -- needs the maintainer.
- [ ] **In-world**: log in on Second Life and OSGrid, chat, check the files appear in the folders Firestorm uses,
      and that SLNG shows an old Firestorm conversation.

## Verified vs not

Verified here: every rule above against source and real bytes; the folder resolution for the maintainer's six
real accounts (read-only; each resolves to the folder that exists, the Second Life one to the OneDrive folder);
reading the real files (a 24 MB IM log, a 10 MB `chat.txt`, a 1.9 MB group log), the tail in a few
milliseconds; the whole unit-test suite and `--selftest` (which also proves the boot path wrote nothing).

**Not verified:** that Firestorm accepts a file SLNG appended to (nothing writes into the real folder from a test;
the format is the viewer's own, but it has not been run); the Second Life Beta label (taken from
`grids.remote.xml`, no such folder on disk); what an OpenSim grid puts in `daylight_savings` (SLNG applies the US
rule; two real samples agree); a grid-visitor name's file (`Name.Surname @grid:port`) is derived from the rules,
not seen; the History viewer's first open of a very large file is ~0.6 s on the main thread.

## Out of scope / left out

- Firestorm's per-account switches for logging (see above), the seconds-in-stamps option, 12-hour stamps on write.
- `conversation.log`, `teleport_history.txt` and the other files in an account folder: not read, not written.
- Ad-hoc conference logs (`<name> Conference hash<uuid>.txt`): SLNG has no ad-hoc conferences.
- Group tabs whose name was unresolved when they opened still log under the group's id (`<uuid> (group).txt`),
  splitting that group's history from Firestorm's `<name> (group).txt`. A pre-existing weakness of the tab
  (`ChatWindow.GetOrCreateGroupTab`); now more visible. Not fixed here.
- An IM tab opened from the Friends list is named by `FriendEntry.Name`; whether that is always the legacy name
  (it must be, for the file name to match) was not checked.
- The live view shows local `HH:mm`, a preloaded line shows the log's own Second Life stamp.

## Affected Files

- `src/SLNG.Core/ChatLogs/*` (new), `src/SLNG.Core/Services/ChatLogger.cs`, `src/SLNG.Net/GridInfoProbe.cs` (new)
- `app/scripts/ChatLogSettings.cs`, `app/scripts/ChatLogPaths.cs`, `app/scripts/UI/ChatLogPreferencesPage.cs`,
  `app/scripts/UI/ChatLogImportContext.cs` (new); `app/scripts/Boot.cs`, `app/scripts/UI/ChatWindow.cs`,
  `app/scripts/GridData.cs`, `app/scripts/SelfTest.cs`, `app/i18n/en-US.json`, `app/i18n/de-DE.json`
- `tests/SLNG.Core.Tests/ChatLogs/*` (new), `tests/SLNG.Core.Tests/Services/ChatLoggerTests.cs`
- `docs/BENUTZERHANDBUCH.md`, `docs/ROADMAP.md`, `docs/specs/BUG-GRID-01-per-grid-data-separation.md`

## Sub-tasks / Progress

- [x] Study of real files and Firestorm/Linden source (rule table)
- [x] Pure rules + tests (layout, format, time, grid labels, settings reader, locator, importer)
- [x] `ChatLogger` in Firestorm's format; one parser for History and preload
- [x] Preferences page, login wiring, import button, `--selftest` check
- [x] AppVersion, manual, BUG-GRID-01 spec
- [ ] Firestorm run against SLNG's output; in-world confirmation (maintainer)
