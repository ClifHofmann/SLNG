# [FEAT-UI-35] Friend rights: shown in the friends list, changeable, incoming changes notified

- **Feature ID:** `FEAT-UI-35`
- **Track:** `ui` / `net`
- **Status:** `✅ Done`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Asked 2026-09-21 whether rights information arrives, and again 2026-10-05: the Friends list should have columns like
Firestorm's -- online status, sees where I am, I see where the person is, may edit my objects, I may edit theirs. In the
same message: the login name belongs behind the Display Name, not under it.

Until now `FriendEntry` carried only id, name and presence; no friend right was read, shown, changed or reported.

## The direction trap (read from the library, not guessed)
LibreMetaverse names a right by who **holds** it:

| LMV | meaning | SLNG |
|---|---|---|
| `FriendInfo.TheirFriendRights` | what the friend may do with **us** (read by `CanSeeMeOnline`, `CanSeeMeOnMap`, `CanModifyMyObjects`) | `GrantedByMe` |
| `FriendInfo.MyFriendRights` | what we may do with **them** (`CanSeeThemOnMap`, `CanModifyTheirObjects`) | `GrantedToMe` |

Its `ChangeUserRights` handler fills them the same way (a friend found as `AgentRelated` = we granted: `TheirFriendRights`;
`AgentRelated == us` = they granted: `MyFriendRights`). `GrantRights(friend, rights)` sends `RelatedRights` = what we
grant, i.e. the friend's `TheirFriendRights`, and **only sends the packet**: LMV does not update its own copy, so
`SetFriendPermissions` does. A swap would show the person's grants as the friend's and make the checkboxes grant the
wrong thing; `FriendPermissionsTests` pins the direction with a different set on each side.

## Behaviour
- **Icons, not boxes** (asked 2026-10-05, v0.26.75-alpha: boxes were too heavy; the reference viewer draws icons that are
  active or inactive). Each cell is a Material Symbols icon -- an eye (online), a pin (map), a pencil (edit) -- in colour
  when the right is on (blue for what the person grants, green for what the friend granted, so the two sides read apart) and
  a faint ghost when it is off. The person's three are flat buttons with a hand cursor (a click flips the right; hover
  brightens); the friend's two are labels, since a disabled button would lose its tooltip. The tooltip is the column's
  explanation. (The font has `place`, not `location_on`; every glyph name is checked against it.)
- **Columns**, left to right, under two group labels. *Friend may...* (what we granted; boxes to click): **Online**
  (see when I am online), **Map** (see where I am), **Edit** (edit, delete and take my objects). *I may...* (what the
  friend granted; not clickable -- only they can change it): **Map** (see where they are), **Edit** (edit their
  objects). The header stays put above the scrolling list; every label has the full explanation as its tooltip.
- **Changing a right** sends the friend's whole set (`GrantUserRights` takes the set, never a bit), built from the
  session's current state, not from the row as drawn, so two quick clicks cannot undo each other. A change survives a
  relog because the simulator holds it.
- **Granting Edit asks first** ("Allow X to edit, delete and take your objects?", danger button); a no or a dismissal puts
  the box back. Revoking it, and changing Online / Map, do not ask.
- **Incoming change.** When a friend gives us a right over them or takes one back, a System notification says which
  ("X now lets you see where they are on the map."). The "before" is the value seen at login (`OnLoginResponseSeedFriendRights`)
  or, for a friend added later, at the first `GetFriends`. A first sight reports nothing, and our own grant echoing back
  changes the other half and reports nothing.
- **Login name.** Behind the Display Name, muted, on the same line (`Anna Display  (anna.resident)`), unless the "show
  usernames" preference is off; always in the tooltip. Drawn by a `RichTextLabel` inside the row's button, because two
  colours on one line are not a Button's own text. Names are BBCode-escaped.
- **Not a column:** `CanSeeThemOnline` (whether the friend lets us see them online) -- it is the green/grey dot already.

## Acceptance Criteria
- [x] The panel shows both directions without mixing them (tested: distinct sets per side)
- [x] A change is sent with `GrantRights`, with LibreMetaverse's copy updated so the list shows it at once
- [x] An incoming change appears as a notification
- [x] Granting edit rights asks first
- [x] Login name behind the Display Name
- [ ] Seen working in the client and against a grid, with a second account (not done where this was written: no Godot,
  no grid; the selftest was not run)

## Technical Specs & Affected Files
- `src/SLNG.Core/FriendPermissions.cs` -- `[Flags] FriendPermissions` (the grid's bits 1/2/4) and `With(...)`.
- `src/SLNG.Core/FriendEntry.cs` -- `GrantedByMe`, `GrantedToMe` (optional, so existing callers are unchanged).
- `src/SLNG.Core/GridEvents.cs` -- `FriendRightsChangedEvent` (`Before`, `After`, `Gained`, `Lost`).
- `src/SLNG.Net/GridSession.Chat.cs` (`GetFriends`), `GridSession.Friendship.cs` (`SetFriendPermissions`,
  `OnFriendRightsUpdate`, `OnLoginResponseSeedFriendRights`, `FriendRightsChanged`), `GridSession.cs` (subscriptions).
- `app/scripts/UI/FriendsPanel.cs` -- header, cells, confirmation, name text; event bursts are now folded into one
  rebuild per frame (`RefreshSoon`), since a row has five more controls than before.
- `app/scripts/Boot.cs` -- `OnFriendRightsChanged` (notifications); `AppVersion` v0.26.75-alpha.
- `app/i18n/en-US.json`, `de-DE.json` -- `ui.friend_rights.*`, `ui.notifications.friend_right_*`.
- Tests: `tests/SLNG.Net.Tests/FriendPermissionsTests.cs` (15), `tests/SLNG.Core.Tests/FriendPermissionsWithTests.cs`.

## Not done
- A window for a friend's rights on their profile (the list is the only place).
- Per-cell tooltips on the dimmed boxes (the header explains them).
- Showing `CanSeeThemOnline` separately from the presence dot.
