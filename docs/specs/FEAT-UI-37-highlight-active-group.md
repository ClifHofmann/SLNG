# Polish: FEAT-UI-37 (Highlight Active Group in Groups List)

## Context
In the current `GroupsPanel` (implemented in `M5-3`), the list displays all joined groups equally. There is no visual indication showing which group is currently set as the "Active Group" (the one providing the active title over the avatar's head). The user requested a QoL improvement: *"das man die gruppe die aktiv ist in der Gruppenauswahl auch sieht"*.

## Requirements
1. **Identify Active Group:**
   - Read the avatar's active group ID (provided by `AvatarManager` / `GroupManager` during login or group fetching).
2. **Visual Highlight in `GroupsPanel`:**
   - Update the row rendering for the groups list to emphasize the active group.
   - Approaches: Render the group name in **bold** text, append a localized `(Aktiv)` label, or display a specific badge/icon.
3. **Dynamic Update:**
   - Ensure the visual indicator refreshes if the active group is changed (e.g., when the advanced group management from FEAT-UI-19 is implemented, or if changed via another viewer).

## Acceptance Criteria
- [ ] The active group is instantly recognizable in the Groups window.
- [ ] The highlight updates correctly if the group list is refreshed.
