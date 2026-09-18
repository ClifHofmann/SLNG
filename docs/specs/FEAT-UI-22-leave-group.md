# Feature: FEAT-UI-22 (Leave Group & Fee Warning)

## Context
Currently, the `GroupsPanel` displays a user's joined groups, but the "Leave" (Verlassen) button is functionally just a placeholder. The user requested the ability to actually leave groups, combined with a crucial economic safety feature: a warning if the group has an enrollment fee. Leaving a paid group means the user would have to pay the fee again to rejoin, so they must be explicitly warned before confirming the departure.

## Requirements
1. **Leave Group Action:**
   - Wire up the "Leave" button in the `GroupsPanel` to trigger a group departure request via the LibreMetaverse `GroupManager` (e.g., `LeaveGroup` / `LeaveGroupAsync`).
2. **Confirmation Modal:**
   - When the user clicks "Leave", display a confirmation dialog modal (e.g., "Möchtest du die Gruppe '[Gruppenname]' wirklich verlassen?").
   - Do not allow the user to leave a group without confirming first.
3. **Enrollment Fee Warning:**
   - Before displaying the modal, evaluate the group's profile properties (specifically the `EnrollmentFee` or `MembershipFee`). If not currently cached, perform a quick fetch.
   - If the fee is greater than 0, the confirmation modal MUST prominently display a warning: "Achtung: Der erneute Beitritt zu dieser Gruppe kostet L$ [Fee]."
4. **State Update:**
   - Upon successful departure, automatically remove the group from the local active groups list and update the `GroupsPanel` UI to reflect the change immediately.

## Acceptance Criteria
- [ ] Users can click "Leave" on a group to initiate departure.
- [ ] A confirmation modal prevents accidental clicks.
- [ ] The modal explicitly warns the user of the exact L$ cost if they ever wish to rejoin a paid group.
- [ ] Leaving the group successfully removes it from the UI without requiring a viewer relog.
