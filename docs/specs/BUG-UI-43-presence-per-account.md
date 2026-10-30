# [BUG-UI-43] Away & auto-response are shared between avatars -- every login is its own tenant

- **Feature ID:** `BUG-UI-43`
- **Track:** `ui/core`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Reported 2026-10-30: the Away message, the busy / auto-response texts **and** their switches and the idle timeout set while
logged in as one avatar showed up -- and were used to answer instant messages -- when logging in as another. The maintainer
runs several avatars (and a second account for testing) and switches between them daily.

Cause: FEAT-UI-75 (v0.28.24-alpha) kept the whole setup in one shared `[presence]` section of `preferences.cfg`
("Settings are global, not per account" was even listed as a difference from Firestorm), and `PresenceController` read it
in `Attach(session)`, which runs **before** the login, when the agent is not known yet. BUG-GRID-02 had scoped the other
per-avatar stores but predates FEAT-UI-75.

The rule, from the maintainer: **a login is a tenant.** Per-account data never leaks from one login into another.

## The scope key (defined once)
`SLNG.Core.AccountScope` -- grid slug (`GridIdentity.Slug(login URI)`) + agent id, lower case; null when either is missing or
the agent id is the empty UUID. A store keyed by it lives in the section `<prefix>_<grid>_<agentId>` of `preferences.cfg`
(the pattern BUG-UI-41 / BUG-GRID-02 already used). Both halves are stable from the moment the login response arrives and
differ for the same avatar name on another grid (different UUID **and** different slug). The agent id is not known before the
login, so a keyed store is loaded **after** it and dropped at logout; a null key means "read nothing, write nothing", never
"fall back to a shared section". The six hand-copied `GetSection` helpers (group mute, friend categories, favorites bar,
avatar hover, profile notes x2) now call it.

## Fix
- `SLNG.Core.Presence.PresenceSettingsStore` (new, engine-free, over `IConfigStore`): `Load` / `Save` for section
  `presence_<grid>_<agentId>` -- the nine values the page edits (4 texts, 3 switches, timeout, away-on-focus-loss).
- **Migration:** the first time a login has no section of its own, the old shared `[presence]` values are copied into it, once.
  The shared section is **kept** (nothing is ever written to it again) so the next login without a section of its own starts
  from the same values everybody was using; a login that has its own section is never touched. Consequence: a brand-new
  avatar also starts from the old shared values until it changes them.
- `PresenceSettings` is now only the Godot `ConfigFile` adapter (`GodotConfigStore`) and holds **no state** (the static
  `AwayTimeoutSeconds` / `AwayOnFocusLoss` that outlived a login are gone).
- `PresenceController`: `Attach(session)` starts from the defaults (nothing answered), `BindAccount(grid, agentId)` loads the
  login's setup right after the login succeeded (`Boot`, next to the "Login SUCCESS" line), `Detach()` at logout and
  disconnect puts the defaults back. `AccountChanged` makes the Preferences page drop an unapplied draft typed for the previous
  avatar. A controller that is not bound to a login applies a change in memory and writes nothing.
- Re-login without restarting the viewer works: logout -> `Detach`, next login -> `Attach` + `BindAccount`.

## Acceptance Criteria
- [x] Two avatars on one grid keep independent texts, switches and timeout; logging in as B after A shows none of A's values.
- [x] The old shared values are copied into a login's section once; an existing section is never overwritten; the shared
      section is never written again.
- [x] The same avatar on another grid is another tenant.
- [x] No login bound -> nothing read, nothing written.
- [x] Unit tests `PresenceSettingsStoreTests` (14) and selftest "presence per account" (real controller, real `ConfigFile`
      adapter, throwaway file -- the "user data untouched" check does not see it).
- [ ] Confirmed in-world by the maintainer with two avatars.

## Audit: what else is per account but stored globally
Reference list: `scratch/slviewer/indra/newview/app_settings/settings_per_account.xml` (`gSavedPerAccountSettings`, 41 keys;
the viewer also keeps `toolbars.xml`, camera presets and the floater layout in a per-account user-settings directory).
`preferences.cfg` sections of this client, checked one by one:

| Section / store | Per account in the reference viewer? | State |
|---|---|---|
| `presence` (Away, busy, autorespond texts + switches, timeout) | `DoNotDisturbModeResponse`, Firestorm `FSAutorespond*` / `FSAwayAvatarResponse` yes; `AFKTimeout` is a global viewer setting there | **fixed here** (the timeout and focus-loss switch are scoped with the rest on purpose: one page, one tenant) |
| `favorites_bar_*`, `friend_categories_*`, `group_mute_*`, `avatar_hover_*`, `avatar_notes_*` | `AvatarHoverOffsetZ`, favorites, mute list yes | already scoped (BUG-UI-33/41, BUG-GRID-02); now share `AccountScope` |
| `chat_log_dirs`, `recent_conversations/` | `InstantMessageLogPath` yes | already per account (grid + name key / hash of the log folder) |
| inventory / display-name / object caches | -- | already per grid (`GridData`) |
| `chat_logs` `im_names` (IM file naming) | `LogFileNamewithDate` yes | **follow-up** -- one global value, cheap but loaded pre-login |
| `chat_window` name-list width | `ConversationsListPaneWidth` / `*Collapsed` yes | **follow-up** -- cosmetic, loaded at window build |
| `snapshot` `output_dir` | `SnapshotBaseDir` / `SnapshotBaseName` yes | **follow-up** -- a folder path; two avatars saving to one folder is rarely wrong |
| `toolbar` order, `radar`, `animation` (always run, typing animation, head follows camera), `camera`, `media`, `dof`, `display`, `graphics*`, `network`, `key_bindings`, `window_geometry` | global `gSavedSettings` there (toolbar / camera presets live in the per-account directory) | left global: hardware, rendering and layout preferences |
| `ui` switches in `UiSettings` (legacy names, display names, group titles, friend presence toasts) | global | left global |
| Mute / block list, friends, group membership, landmarks, inventory | server side, per account | nothing to do |
| `avatar_notes` (un-scoped, pre-BUG-GRID-02) | -- | dead section still in old files; harmless, could be erased |
| Do Not Disturb / Away state | per session | not persisted by design |

Not done and not needed for this ticket: `ShowFavoritesOnLogin`, `TranslatingEnabled`, `VoiceCallsFriendsOnly`, the
`*UploadFolder` and `Last*` keys have no counterpart in this client yet.

## Technical Specs & Affected Files
- `src/SLNG.Core/AccountScope.cs`, `src/SLNG.Core/IConfigStore.cs`, `src/SLNG.Core/Presence/PresenceSettingsStore.cs`
- `app/scripts/PresenceSettings.cs`, `app/scripts/GodotConfigStore.cs`, `app/scripts/PresenceController.cs`
- `app/scripts/UI/AutoresponsePreferencesPage.cs`, `app/scripts/Boot.cs`
- `app/scripts/UI/{GroupMuteSettings,FriendCategoryStore,LandmarkFavoritesStore,AvatarHoverSettings,UserProfileWindow}.cs` (section helper only)
- `tests/SLNG.Core.Tests/Presence/PresenceSettingsStoreTests.cs`, `app/scripts/SelfTest.PresenceScope.cs`
