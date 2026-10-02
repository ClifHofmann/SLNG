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

> **v0.26.16 (the maintainer's correction):** SLNG gets *compatibility* with Firestorm -- the layout, the names,
> the line format, the encoding -- and does **not** detect, read or follow a Firestorm installation. Rules 1, 5 and 7
> below describe what *Firestorm* does with *its* settings; SLNG no longer reads those settings (no
> `settings_per_account.xml`, no `user_settings/settings.xml`, no `grids.*.xml`). Where a person wants both viewers
> on one history, they pick, per saved account, the folder Firestorm uses for it.

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

**Folder (b), per saved account.** Preferences -> **Chat logs**. A dropdown lists the saved accounts exactly as the
login screen does (`Clifton Howlett -- Second Life`, `-- OSGrid`, ...), plus first, the account logged in right now
when it was never saved. For the selected one: the chosen **base folder** (read-only text), **Choose...** (the OS
folder picker; there is no typing of paths) and **Use default**, and the folder **in effect** -- the base plus the
account's own sub-folder named Firestorm's way, which is created inside it. An account with nothing chosen uses
SLNG's own viewer-neutral folder (`%APPDATA%\SLNG\logs\chat`), in Firestorm's layout. To share a history with
Firestorm, choose the folder Firestorm uses for that account (Firestorm's own "Logs and transcripts location"); the
sub-folder name is the same, so the two meet in one folder. A change applies at the next login. There is no
"Firestorm folder" mode and no first-login default: SLNG never looks at Firestorm.

*Where the value lives:* `preferences.cfg`, section `chat_log_dirs`, key = `ChatLogAccountKey.Of(login URI, first,
last)` (`<grid slug>__<account slug>`, built from the BUG-GRID-01 identities, so two spellings of one login URI are
one account and the same name on two grids is two). Not `logins.cfg`: that file is Boot's, holds the password
hashes, is rewritten whole in several places, and treats every section except `Settings`/`Window` as a login
profile (a new section would appear in the login screen's list); it also only knows *saved* accounts, while a
one-off login needs a folder too. Nothing is written on the boot path: `ChatLogSettings.Load` only reads, a value
is written when the person chooses. Boot resolves the folder at login from that account's key, nothing else.

**IM file names (c).** A global choice (Firestorm's own is global too): *legacy* `First Last.txt` (the default --
Firestorm's built-in default, and what SLNG's old files looked like) or *account* `first_last.txt`. It is a plain
setting now; the earlier "as Firestorm is set up" option, which read Firestorm's `settings.xml`, is gone. The
maintainer's Firestorm uses account names, so he sets this once. The date suffix (rule 7) is not written; an
existing dated file is still *found* when reading (`FirestormLogLayout.ResolveExisting`).

**Grid label (a) -- without Firestorm's files.** SLNG only knows the login URI. Order: (1) a built-in table of four
constants: the two Linden hosts (`Second Life`, `Second Life Beta`), OSGrid (hosts `hg.osgrid.org` and
`login.osgrid.org` -> `OSGrid`) and a local OpenSim on port 9000 (`127.0.0.1`/`localhost` -> `localhost`);
(2) the grid's `get_grid_info` `<gridname>`, one GET with a 3 s timeout at login, only for a grid not in the table
(`GridInfoProbe`); (3) the host (and a non-default port), the viewer's own fallback when a grid reports no name.
`GridIdentity` slugs are SLNG's own and are **not** used for these folder names. A folder name Windows rejects
(a label `host:port`) has `:` and the like replaced by `_`.

How that compares for the maintainer's real grids: Second Life -> no suffix, same; `hg.osgrid.org` -> `.osgrid`
(both `hg.` and `login.osgrid.org/get_grid_info` answer `OSGrid`; same as Firestorm's `clifton_howlett.osgrid`);
Alife Virtual -> `www.alifevirtual.com:8002/get_grid_info` answers `<gridname>Alife Virtual</gridname>` (checked
2026-10-02), so `.alife_virtual`, same as `cilian_dupont.alife_virtual`; a local grid on `127.0.0.1:9000` ->
`.localhost`, same as `test_user.localhost` -- **but only because of the built-in `localhost` constant**: Firestorm
gets that label from its own shipped grid list, and a local OpenSim's `get_grid_info` reports whatever its
configuration says (default `gridname` is not `localhost`), so a local grid on another port, or one the maintainer
renamed, can land in a different folder than Firestorm's. That is the one case where the label can differ.
Another viewer's list of grids the maintainer added by hand is not consulted, so a grid whose `get_grid_info`
is down at login gets the host as its label (`.<host>`), where Firestorm, having stored the label once, would
still say its name.

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
setting of those. Nothing of Firestorm's settings is mirrored: the choices that decide where and under which name a file lives
(rules 1, 5) are SLNG's own settings now (the per-account folder, the IM file-name style).

## Layering

- **`SLNG.Core/ChatLogs/`** (pure, tested): `FirestormLogLayout` (folder and file names), `FirestormLogFormat` +
  `ChatLogEntry` (line format, the one parser), `SecondLifeTime`, `GridLabels`,
  `ChatLogAccountKey` + `ChatLogFolderMap` (the per-account choice), `ChatLogLocator` (+ `ImLogNameStyle`,
  `ChatLogNaming`, `ChatLogTarget`; pure, reads nothing), `ChatLogImporter` (+ plan types). `ChatLogger` (`Services/`) is the file IO.
- **`SLNG.Net/GridInfoProbe`**: the one HTTP GET; no LibreMetaverse type.
- **`app/`**: `ChatLogSettings` (preferences.cfg, per account), `ChatLogPaths` (feeds the account's choice and
  SLNG's default in), `Boot` (resolves at login from the profile's key, before the login request; supplies the
  saved accounts to the page), `ChatLogPreferencesPage` + `ChatLogAccount`, `ChatWindow` (preload uses `GetTail`;
  system notices have an empty sender), `GridData.LegacyChatLogDirectory` (import source only).
- Nothing in `src/` references Godot.

## Acceptance Criteria

- [x] Folder per login by Firestorm's rule, verified against `lldir.cpp` / `llstartup.cpp` / `lllogchat.cpp` /
      `fsgridhandler.cpp`, incl. the grid label for OpenSim grids (rules 2, 3; the label lookup above).
- [x] The base folder is a setting per saved account in Preferences (OS folder picker, "Use default"), default
      SLNG's own folder; no Firestorm detection or settings are read.
- [x] History viewer and preload read Firestorm's files and SLNG's older ones through one parser; what SLNG
      writes has Firestorm's names, line format, encoding, line ends; append only.
- [x] Old SLNG logs found and importable on request (append + marker, idempotent), never moved or deleted.
- [x] `conversation.log` left alone, documented, with the statement above.
- [x] A unit test per format rule using real (anonymised) lines, a writer/reader round trip, appended bytes
      unchanged, import idempotence; selftest check `chat log paths`.
- [x] `AppVersion` v0.26.16-alpha; manual (German) and BUG-GRID-01 spec updated.
- [ ] **Firestorm run against a file SLNG wrote** (open the conversation in Firestorm and check the lines and the
      times) -- needs the maintainer.
- [ ] **In-world**: log in on Second Life and OSGrid, chat, check the files appear in the folders Firestorm uses,
      and that SLNG shows an old Firestorm conversation.

## Verified vs not

Verified here: every rule above against source and real bytes; the account-folder names for the maintainer's real
accounts (`clifton_howlett`, `.osgrid`, `.alife_virtual`, `.localhost`, `_resident`; each equals a folder that exists on
his disk; the base folders are now chosen, not detected -- v0.26.14 had resolved them from Firestorm's settings,
read-only, and they matched); reading the real files (a 24 MB IM log, a 10 MB `chat.txt`, a 1.9 MB group log), the tail in a few
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
  `app/scripts/UI/ChatLogImportContext.cs`, `app/scripts/UI/ChatLogAccount.cs` (new); `app/scripts/Boot.cs`, `app/scripts/UI/ChatWindow.cs`,
  `app/scripts/GridData.cs`, `app/scripts/SelfTest.cs`, `app/i18n/en-US.json`, `app/i18n/de-DE.json`
- `tests/SLNG.Core.Tests/ChatLogs/*` (new), `tests/SLNG.Core.Tests/Services/ChatLoggerTests.cs`
- `docs/BENUTZERHANDBUCH.md`, `docs/ROADMAP.md`, `docs/specs/BUG-GRID-01-per-grid-data-separation.md`

## Sub-tasks / Progress

- [x] Study of real files and Firestorm/Linden source (rule table)
- [x] Pure rules + tests (layout, format, time, grid labels, settings reader, locator, importer)
- [x] `ChatLogger` in Firestorm's format; one parser for History and preload
- [x] Preferences page, login wiring, import button, `--selftest` check
- [x] v0.26.16: Firestorm detection removed; per saved account folder
- [x] AppVersion, manual, BUG-GRID-01 spec
- [ ] Firestorm run against SLNG's output; in-world confirmation (maintainer)
