# Feature: FEAT-UI-14 (Teleport History)

## Context
Users frequently teleport across the grid. A "Teleport History" (TP History) log allows them to easily backtrack and return to recently visited locations without needing to manually create a landmark for every stop.

## Requirements
1. **Location Tracking:**
   - Hook into the client's teleport and region-crossing events.
   - Record the Region Name, Global/Local Coordinates, and the Timestamp of arrival.
2. **Persistence:**
   - Save the history list locally (e.g., in a JSON file or user config) so it persists across client restarts.
   - Implement a rolling limit (e.g., the last 100 teleports) to prevent the file from growing indefinitely.
3. **UI Integration:**
   - Add a "History" or "Recent" tab to the existing World Map / Places window (`MVP2-2`).
   - Display the list chronologically (newest first).
   - Allow users to double-click an entry to initiate a teleport back to that location.
   - Add a context menu option to "Save as Landmark" or "Clear History".

## Acceptance Criteria
- [ ] Every successful teleport appends an entry to the local TP History.
- [ ] The history list persists across client sessions.
- [ ] The UI accurately displays the history and allows triggering a teleport from an entry.
- [ ] A maximum history limit is enforced.
