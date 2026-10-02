# [BUG-GRID-01] Per-grid data separation

- **Feature ID:** `BUG-GRID-01`
- **Track:** `core` | `net` | `ui`
- **Status:** `🚧 In Progress` (implemented and verified offline; no live login on two grids yet)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Reported 2026-10-02: the maintainer's account "Clifton Howlett" exists on OSGrid (`hg.osgrid.org`) and on
Second Life (agni). Logging into Second Life showed the login background of the OSGrid session.
`Boot.cs` built `user://last_session_bg_{first}_{last}.png` -- keyed by the account **name** alone, and a
name exists on every grid.

The maintainer's rule: such things must be **strictly** separated. This spec audits every persisted store
and every piece of in-memory state that survives a re-login, states which ones can mix two grids, and
fixes them with **one** notion of grid identity and **one** path builder instead of per-store patches.

**Short answer to "are there more places?":** yes, three more, of different weight.

1. **Object cache** (`cache/objects/*.slobj`): one directory for every grid, and the pruning in
   `ObjectCacheDisk.DeleteOtherGenerations` deletes every file with the same region handle but another cache
   id. Second Life and OpenSim place regions on the same grid coordinates (OSGrid's default region sits at
   1000,1000, also a real Second Life region), so two grids wiped each other's files whenever a handle
   repeated. Pinned by a test.
2. **Chat logs** (`%APPDATA%\SLNG\logs\chat\`): one `chat.txt` plus one file per conversation name for **every
   account on every grid**. The History viewer and the Main tab's preloaded tail showed other accounts' and
   other grids' chat. (The reference viewer logs per account:
   `LLDir::setPerAccountChatLogsDir`, `indra/llfilesystem/lldir.cpp:927`, called from `llstartup.cpp:1127`.)
3. **Region environment** (in memory; from reading the code, not observed live): `EnvironmentDriver` is replaced only
   when a region *sends* an environment (`GridSession.Environment.cs` raises the event only for a non-null result). A grid that sends none (plain OpenSim) never replaced it, so after Second Life -> OSGrid in
   one run the Second Life sky stayed. Same class: the chat window kept the previous session's tabs and lines.

Map tiles were already keyed by map-server host and were safe in practice; they now also sit behind the
per-grid rule (see the table).

## Acceptance Criteria

- [x] One tested `GridIdentity.Slug(loginUri)` in `SLNG.Core`: the two Linden grids are distinct; URL spellings of
      one grid (`http://hg.osgrid.org/`, `hg.osgrid.org:80`, `HG.OSGRID.ORG`) give one slug; two OpenSim grids
      give two; hostile input (separators, `..`, unicode, empty, control characters, very long) gives one safe,
      stable, non-empty path segment; a host cannot impersonate a Linden grid.
- [x] One path builder (`GridDataPaths`, wrapped for the app by `GridData`); every store classed unsafe goes
      through it: last-session background, object cache, map tiles, chat logs.
- [x] Same account name + same region handle on two grids give different directories (unit tests, including
      the real `ObjectCacheDisk`, and a `--selftest` check).
- [x] The object cache no longer prunes across grids.
- [x] In-memory leaks found by the audit that are cheap and safe are closed (region environment, chat window).
- [x] No existing user data is deleted, moved or guessed at; the boot path (`--selftest`) writes nothing new.
- [x] `AppVersion` v0.26.12-alpha -> v0.26.13-alpha; user manual (German) updated.
- [ ] Confirmed in-world on two grids (needs the maintainer).

## Technical Specs & Affected Files

### Grid identity (`src/SLNG.Core/GridIdentity.cs`)

Placement: **`SLNG.Core`**. It is pure string logic, has no protocol or engine type, and `Core` may not depend
on `Net`/`Assets` while both may depend on `Core`. No LibreMetaverse type is involved.

The slug is a function of **host and port only** (scheme, path, query, user-info, case, a trailing dot and a
default port are ignored):

| Input | Slug |
|---|---|
| `https://login.agni.lindenlab.com/cgi-bin/login.cgi` | `agni` |
| `https://login.aditi.lindenlab.com/cgi-bin/login.cgi` | `aditi` |
| `http://hg.osgrid.org/`, `hg.osgrid.org:80`, `HG.OSGrid.org` | `hg.osgrid.org` |
| `http://localhost:9000/` | `localhost~p9000` |
| `http://localhost/`, `http://agni/` (a plain host) | `localhost~h`, `agni~h` |
| empty / unreadable / not http(s) | `unknown~u<16 hex of SHA-256>` |

Why it cannot collide:

- Every character outside `[a-z0-9.-]` is written as `~` + hex of its UTF-8 bytes, and `~` itself is always
  escaped, so the escaping is reversible: two different hosts cannot produce one slug. `~p<port>`, `~h`, `~t`
  (truncation) and `~u` (unknown) are markers no escape sequence can produce (`p`, `h`, `t`, `u` are not hex).
- A Linden slug is purely `[a-z0-9]`. A plain host that would also be purely `[a-z0-9]` gets `~h`, so a machine
  named `agni` can never read or write Second Life's data.
- Windows device names (`con`, `nul.example.com`, ...) get their first letter escaped; segments over 96 characters
  are cut to a prefix plus a hash of the whole; a leading dot is escaped. Result: always exactly one legal path
  segment.
- `AccountSlug(first, last)` = `first_last`, lower case, same escaping (`_` is escaped inside a name, so
  `a_b`+`c` and `a`+`b_c` differ). It always contains `_`, so an account directory can never equal `cache`.

Two login URIs of one grid (`login.osgrid.org` and `hg.osgrid.org`) are two slugs. That costs a cache refill,
never a wrong answer -- the side to err on.

### Path builder (`src/SLNG.Core/GridDataPaths.cs`, `app/scripts/GridData.cs`)

```
<root>/<slug>/cache/<kind>/                 CacheDirectory(uri, kind)
<root>/<slug>/<base>_<account><ext>         AccountFile(uri, first, last, base, ext)
<root>/<slug>/<account>/                    AccountDirectory(uri, first, last)
```

Two roots, one rule: `user://grids` (resolved to a real directory) and the chat-log root
`%APPDATA%\SLNG\logs\chat`. The grid is the one in the login form (`LoginCredentials.GridLoginUri`), so paths
are chosen **per login**, never at startup. Building a path touches nothing on disk; the writer creates the
directory. `kind` and `base` are fixed lower-case names chosen by the caller, validated against path tricks.

### Audit

"Key" is what a file or entry is named by. "Collide?" asks whether two grids can reach the same entry.

| Store | Where (written by) | Key today | Can two grids collide? | Verdict | Fix |
|---|---|---|---|---|---|
| Login / loading background | `user://last_session_bg_<first>_<last>.png` (`Boot.cs` logout save; read at login click and after logout) | account name | **Yes** -- the reported bug | UNSAFE | `user://grids/<slug>/last_session_bg_<account>.png`; saved with the account+grid captured at login |
| Object cache | `user://cache/objects/<handle>-<cacheId>.slobj` (`ObjectCacheDisk`) | region handle + the simulator's cache id (file name and header both checked on read) | Serving the wrong grid's objects needs handle **and** cache id to match -- not observed, and I did not verify what OpenSim sends as cache id. **Mutual deletion is certain**: a save removes every file of the same handle under another cache id, trimming and "Clear" are directory-wide. Test: `ObjectCachePerGridTests` | UNSAFE | `user://grids/<slug>/cache/objects` |
| Chat logs | `%APPDATA%\SLNG\logs\chat\chat.txt`, `<conversation name>.txt` (`ChatLogger`) | none / conversation display name | **Yes** -- every account on every grid appends to the same files; History and the Main tab preload read them back | UNSAFE | `<chat root>/<slug>/<account>/` attached at login (`ChatLogger.UseDirectory`); window forgets the previous session's tabs |
| Map tiles | `user://cache/maptiles/<map host[_port]>/map-1-<x>-<y>-objects.jpg` (`MapTileService`) | map-server host + grid coordinates | Only if two grids share a map host. One real gap: `ChooseMapServerUrl` falls back to `map.secondlife.com` for **any** Linden grid whose login lacks `map-server-url`, i.e. Aditi would read Agni's tiles | SAFE in practice, moved anyway | `user://grids/<slug>/cache/maptiles` (keeps the host folder inside) |
| Asset cache | `user://cache/assets/<uuid>.mesh`, `<uuid>_v5.j2c`, `<uuid>.anim` (`AssetService`) | asset UUID | Only if one UUID means different bytes on two grids | SAFE -- **assumption, see below** | unchanged |
| LibreMetaverse asset cache (wearables, animations, gestures, sounds) | `%LOCALAPPDATA%\SLNG\lmv-asset-cache\<uuid>` | asset UUID (file name per LMV `AssetCache.FileName`; read from the vendored v3.0.2 source, not the pinned 3.1.6) | as above | SAFE -- same assumption | unchanged |
| Inventory cache | `user://cache/inventory/<agent>.inv.cache` | agent UUID of the account logging in | An agent UUID is issued by one grid; two grids issue the same one only for a copied database | SAFE | unchanged |
| Display-name cache | `user://cache/displaynames/<agent>.names.json` | agent UUID | as above (contents are UUID -> name) | SAFE | unchanged |
| Self-appearance fallback | `%LOCALAPPDATA%\SLNG\self-appearance\<agent>.bin` | agent UUID, re-checked in the header | as above | SAFE | unchanged |
| Saved logins | `user://logins.cfg`, section `[first last @ <login URI>]`; `Settings/last_profile` holds that key | account + grid URI | No | SAFE (two spellings of one URI are two profiles: cosmetic) | unchanged |
| Window layouts, toolbar, graphics, camera, UI scale, radar columns, media/animation toggles | `user://preferences.cfg` | `PersistId` / setting name | They describe the screen and the person, not a grid | SAFE by design | unchanged |
| Local avatar notes (`notes`, `notes_imported`), group chat mute | `user://preferences.cfg` | agent UUID / group UUID | UUIDs are per grid; a visiting avatar keeps its UUID across grids and then it *is* the same person | SAFE | unchanged |
| Snapshots | `user://snapshots/snapshot_<timestamp>.png` | timestamp | The person's own pictures | SAFE | unchanged |
| Logs | `user://logs/godot*.log`, `client-output` | timestamp | Diagnostics | n/a | unchanged |
| Leftovers | `user://last_session_bg.png`, `facetex-debug.log`, `debug_textures/`, `logs/environment-*.llsd` | -- | No writer exists in the current code (grep) | n/a | not touched |
| Bake preview dump | `%TEMP%\slng_bake\<layer>.png` (`DumpPreview`) | layer name | Overwritten, never read back; refused on Linden grids | n/a | unchanged |

| In-memory state across a re-login in one run | Re-created or cleared per login? | Verdict |
|---|---|---|
| `GridSession`, `World`, `WorldSimulation`, `AssetService`, `GpuCache`, `MapTileTextures`, profile / invite / edit windows, pending notification actions | Yes (`OnLoginPressed`) | OK |
| Maturity page, friends and groups panels | Rebound by `BindSession` / `Initialize` | OK |
| Login-form password | Forgotten when the grid text changes | OK |
| **`EnvironmentDriver` region cycle** | **No** -- replaced only when a region sends an environment | **LEAK, fixed**: reset to the default cycle at each login (a sky preset the user chose stays) |
| **`ChatWindow` tabs and lines** | **No** | **LEAK, fixed**: `ResetForNewSession` |
| Notification history (`NotificationStore`) | No, by design ("the entries stay -- they are a record") | LEAK, **not fixed** -- see out of scope |
| `GroupMuteSettings._muted` (static) | Loaded once | OK (group UUIDs) |
| `RenderConfig` origin, `SelectionOutline._hulls`, `AvatarBodyMeshService._cache` | Reset on region entry / Godot instance ids / grid-neutral client assets | OK |

#### The assumption behind "asset UUID = safe"

The two UUID-keyed content caches (`cache/assets`, `lmv-asset-cache`) stay **shared on purpose**: UUIDs are random,
so unrelated content never collides, and OpenSim grids ship the Linden library under the **original** UUIDs, so
sharing is what makes a second grid cheap. This holds as long as *one UUID never means two different assets*.
It breaks if a grid administrator replaces a library asset in place, or a grid reuses a Linden UUID for something
else -- I found no evidence of either and could not rule it out for the many OpenSim grids. If the maintainer wants
the strict rule here too, it is a one-line change per store (`AssetService` takes its directory from the caller;
the LibreMetaverse cache directory is set once in `GridSession`'s constructor) at the cost of one full re-download
per grid and the library sharing.

### Migration

**Nothing is deleted, renamed or guessed.** Existing data carries no grid, so it cannot be assigned to one:

- `user://last_session_bg_*.png` (four files on the maintainer's machine, ~14 MB): not migrated, no longer read. The
  picture regenerates on the next logout per grid.
- `user://cache/objects`: left in place, unused; per-grid directories start empty and fill as regions are visited.
  The Preferences -> Network "Clear cache" button now also removes it (user-initiated), and reports the size of
  all object caches including the old one.
- `%APPDATA%\SLNG\logs\chat\*.txt` written by older builds: left in place, no longer shown. A person who wants an
  old log in an account's history can copy the file into `<chat root>/<slug>/<account>/`.
- `GridData.LogFirstUse` creates the empty grid directory and prints one `[GridData]` line the first time a grid is
  logged into -- on the login path, never on the boot path. Everything else only builds strings.

`--selftest` boots `Boot._Ready` against the developer's real `user://`; no boot-path file operation was added, the
existing "user data untouched" check passes (12 files unchanged), and the new `per-grid paths` check asserts that
building a path creates no directory.

### Affected files

- `src/SLNG.Core/GridIdentity.cs`, `src/SLNG.Core/GridDataPaths.cs` (new)
- `src/SLNG.Core/Services/ChatLogger.cs` (`UseDirectory`; starts detached instead of in the shared directory)
- `app/scripts/GridData.cs` (new), `app/scripts/Boot.cs`, `app/scripts/UI/ChatWindow.cs`,
  `app/scripts/UI/NetworkPreferencesPage.cs`, `app/scripts/SelfTest.cs`
- `tests/SLNG.Core.Tests/GridIdentityTests.cs`, `GridDataPathsTests.cs`, `Services/ChatLoggerTests.cs`,
  `tests/SLNG.Net.Tests/ObjectCachePerGridTests.cs`
- `docs/BENUTZERHANDBUCH.md`, `docs/ROADMAP.md`

## Out of scope (found, not fixed)

- `NotificationStore` keeps the previous session's entries across a re-login, so notifications of another
  account/grid are visible in the next session's window.
- Local avatar notes and group mutes are keyed by UUID, not by account: two accounts on one grid share them.
- IM log files are named by the conversation's display name: two people with the same name share a file; some
  files are named by a bare UUID (seen on the maintainer's machine; cause not looked into).
- `ChooseMapServerUrl` hard-codes `map.secondlife.com` as the fallback for every Linden grid (wrong for Aditi if its
  login ever omits the URL); harmless now that tiles are per grid.
- `logins.cfg` keys profiles by the raw URI text, so `http://hg.osgrid.org/` and `http://hg.osgrid.org` are two
  profiles.
- The inventory, display-name and self-appearance caches grow one file per agent forever (no cleanup).
- Leftovers from older builds that no current code writes: `user://last_session_bg.png`, `facetex-debug.log`,
  `user://debug_textures` (in the old `SLNG` user dir), `logs/environment-*.llsd`; the legacy shared caches and
  background pictures described under Migration are safe to delete by hand.

## Sub-tasks / Progress

- [x] Audit of every persisted store and re-login state
- [x] `GridIdentity` + `GridDataPaths` + tests
- [x] Background, object cache, map tiles and chat logs behind the builder
- [x] Region environment and chat window reset on login
- [x] `--selftest` check, AppVersion, manual, roadmap
- [ ] In-world confirmation: log into the same account name on Second Life and OSGrid, log out of each, check each
      login screen shows its own picture; look at the `[GridData]` line in `godot.log`.
