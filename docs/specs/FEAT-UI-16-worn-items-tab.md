# Feature: FEAT-UI-16 (Worn Items Inventory Tab)

## Context
To make managing the avatar's appearance easier, the user requested a dedicated "Worn" (Angezogen) tab in the Inventory window. Instead of hunting through regular inventory folders for highlighted worn items, all currently attached or worn items should be aggregated in one clear, easily accessible list.

## Requirements
1. **Dedicated UI Tab:**
   - Within the Inventory Browser (`MVP3-1`), introduce a new tab specifically for "Worn" items.
2. **Dynamic Data Filtering:**
   - The tab must dynamically display all inventory items currently attached to the avatar (mesh attachments, HUDs) or worn as system clothing/body parts.
   - It must listen to equipment update events (e.g., from `AgentManager`) to automatically refresh the list when the avatar's appearance changes.
3. **Contextual Interactions:**
   - Items in this tab should support context menu actions, primarily "Detach" or "Take Off".
   - Detaching an item via this tab immediately removes it from the avatar and, consequently, from the "Worn" tab.

## Acceptance Criteria
- [ ] A dedicated "Worn" tab is visible in the Inventory UI.
- [ ] It accurately displays a list of all currently worn items and attachments.
- [ ] The list updates instantly when items are worn or detached (via any method).
- [ ] Users can easily detach items directly from this new tab via a context menu.
