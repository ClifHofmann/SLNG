# [FEAT-UI-65] Friends list: your own categories, which can be folded up

- **Feature ID:** `FEAT-UI-65`
- **Track:** `ui` / `core`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Asked 2026-10-05: sort the friends into categories of one's own, and fold a category up. The Friends tab was a flat
list (online first, then A-Z). With a long list that is hard to scan; people file friends as "Family", "Builders",
"Club" and want to hide what they are not looking at.

The grid has no friend categories, so this is purely local: it is not sent anywhere and another viewer does not see it.

## Behaviour
- **No category yet = no change.** The list is flat, without headers, exactly as before.
- **Header.** Once a category exists, every category is a header line above its friends: fold arrow, name,
  `(online/total)`. Friends in no category come last under *No category* (only while someone is in it).
  Click the header to fold or open it; right-click it for *Rename...* / *Delete category*.
- **Filing.** Right-click a friend, or select one and press *Category...*: *No category*, one entry per category
  (the current one ticked), *New category...* (creates it and files the friend into it). A friend is in one category.
- **Creating.** The **+** beside the filter creates an empty category. Names are trimmed, cut at 40 characters, unique
  ignoring case. A name that is already taken in *New* means that category; in *Rename* the prompt comes back saying so.
- **Deleting** asks first. The friends stay on the list, they just have no category any more.
- **Filter.** With text in the filter only categories with a match are shown, and all of them open -- a hit inside a
  folded category would otherwise look like no hit. The saved fold state is left alone and a header click does nothing
  meanwhile.
- **Within a category** the order is the list's own: online first, then A-Z by shown name.
- **Removing a friend** drops their filing.

## Acceptance Criteria
- [x] Create, rename, delete a category; delete keeps the friends
- [x] Move a friend into a category, between categories, and out again
- [x] Header click folds and opens; fold state survives a restart
- [x] Categories and filing survive a restart, per account
- [x] Unit tests for the model and its JSON (28 cases, `FriendCategoryBookTests`)
- [ ] Seen working in the client (needs an in-world look; the selftest was not run where this was written)

## Technical Specs & Affected Files
- `src/SLNG.Core/FriendCategoryBook.cs` -- the model: categories, filing, fold state, `Group(...)` into blocks, JSON.
  Engine-agnostic, no Godot.
- `tests/SLNG.Core.Tests/FriendCategoryBookTests.cs`
- `app/scripts/UI/FriendCategoryStore.cs` -- `user://preferences.cfg`, section `friend_categories_<agentId>`, one JSON
  string. Per account because friends and the way to sort them differ between accounts. `Persist` switch for a selftest.
- `app/scripts/UI/FriendsPanel.cs` -- headers, menus, prompts (`TextPromptWindow`, `ConfirmWindow`).
- `app/i18n/en-US.json`, `de-DE.json` -- `ui.friend_category.*`.
- `app/scripts/Boot.cs` -- `AppVersion` v0.26.66-alpha.

## Not done
- Reordering categories (they stay in the order they were made).
- Drag and drop of a friend onto a header (the menu does it).
- Sharing categories with another viewer or Firestorm.
