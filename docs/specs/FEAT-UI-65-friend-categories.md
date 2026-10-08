# [FEAT-UI-65] Friends list: your own categories, which can be folded up; show only online

- **Feature ID:** `FEAT-UI-65`
- **Track:** `ui` / `core`
- **Status:** `✅ Done`
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
- **Show only online.** A checkbox under the filter hides offline friends (asked 2026-10-05, v0.26.67-alpha). Categories
  with nobody online are hidden too, like in a filter, but unlike a filter it leaves the fold state alone: it narrows the
  list, it does not search it. It combines with the text filter. With nobody online the list says so instead of
  "no match". Saved per account, off by default (`FriendCategoryStore.LoadOnlyOnline`). The `(online/total)` count in a
  header then counts the shown friends only.
- **Reordering** (asked 2026-10-05, v0.26.68-alpha). Drag a header onto another header: the dragged category takes the
  place the target has now -- dropped upwards it lands above the target, downwards below it, the rule the conversation
  list already follows. *No category* cannot be dragged and is always last; a category dropped on it goes to the end.
  Right-click a header for *Move up* / *Move down* (off at the top and bottom), for a list too long to drag across.
  The order is part of what is saved. **Seeing that it can be moved** (asked 2026-10-05, v0.26.74-alpha): each category
  header carries a six-dot grip on its left with a hand cursor and a tooltip, the grip being as much a drag source as the
  name; the *No category* header has none but keeps the same indent. While a category is held over another header, that
  header is tinted and a 3 px bar marks its top edge (the dragged one would land above it) or its bottom edge (below it);
  over *No category* the bar is on its top edge (it lands just above it) and nothing shows when the category is already
  last. The bar is a border inside fixed content margins, so showing it moves nothing. The preview is a small accent card
  beside the cursor. `DragSortButton` (the button both this list and the conversation list use) carries
  the drag; the payload is prefixed `slng-friend-category:` so no other drag is taken for one.
- **Show categories** (asked 2026-10-05, v0.26.70-alpha). A checkbox next to *Show only online*; on by default, saved
  per account. Off draws one plain list (online first, then A-Z) without headers; the categories and who is in them
  are untouched, and the friend and header menus still work. Creating a category while it is off turns it on, so
  the new category is not invisible.
- **Online dot** (asked 2026-10-05, v0.26.71-alpha). It was a 9 pt "●" glyph, a speck beside the 20 px rights boxes. It
  is now a drawn 14 px disc (green online, grey offline, a thin lighter rim) with an Online / Offline tooltip; the colour
  alone told a colour-blind person nothing. `FriendsPanel` only -- the chat's conversation list keeps its own dot.
- **Within a category** the order is the list's own: online first, then A-Z by shown name.
- **Removing a friend** drops their filing.

## Acceptance Criteria
- [x] Create, rename, delete a category; delete keeps the friends
- [x] Move a friend into a category, between categories, and out again
- [x] Header click folds and opens; fold state survives a restart
- [x] Categories and filing survive a restart, per account
- [x] Show categories: off gives one plain list, categories kept, survives a restart
- [x] Show only online: hides offline friends and empty categories, survives a restart
- [x] Reorder a category by dragging its header, or with Move up / Move down; the order survives a restart
- [x] Unit tests for the model and its JSON (38 cases, `FriendCategoryBookTests`)
- [ ] Seen working in the client (needs an in-world look; the selftest was not run where this was written)

## Technical Specs & Affected Files
- `src/SLNG.Core/FriendCategoryBook.cs` -- the model: categories, filing, fold state, `Group(...)` into blocks, JSON.
  Engine-agnostic, no Godot.
- `tests/SLNG.Core.Tests/FriendCategoryBookTests.cs`
- `app/scripts/UI/DragSortButton.cs` -- the shared drag-to-sort button (moved out of `ChatWindow`, which uses it too); `DropHover` tells the owner where a drop would land. `app/scripts/UI/DragGrip.cs` -- the drawn six-dot handle.
- `app/scripts/UI/FriendCategoryStore.cs` -- `user://preferences.cfg`, section `friend_categories_<agentId>`, one JSON
  string. Per account because friends and the way to sort them differ between accounts. `Persist` switch for a selftest.
- `app/scripts/UI/FriendsPanel.cs` -- headers, menus, prompts, the only-online checkbox (`TextPromptWindow`, `ConfirmWindow`).
- `app/i18n/en-US.json`, `de-DE.json` -- `ui.friend_category.*`, `ui.friend_view.*`.
- `app/scripts/Boot.cs` -- `AppVersion` v0.26.74-alpha.

## Not done
- Drag and drop of a friend onto a header (the menu does it).
- Sharing categories with another viewer or Firestorm.
