# Feature: FEAT-UI-19 (Advanced Group Management)

## Context
While `M5-3` provides the foundation for group communication (group chat, invites, and basic listing), the viewer lacks the advanced management capabilities that define communities in Second Life/OpenSim. The user requested a comprehensive specification for full group management, encompassing roles, titles, notices, and land.

## Requirements
1. **Group Profile Window (General):**
   - A dedicated UI for group details, displaying the Insignia (texture), Charter, Founder, and Membership Fee.
   - Edit functionality for these fields (dependent on the user holding the correct group abilities).
2. **Members & Roles Management:**
   - **Members:** List all group members. Support inviting, ejecting, or altering role assignments.
   - **Roles:** Create and delete roles. Assign granular abilities to roles (e.g., `GroupNoticeSend`, `LandDeed`, `RoleProperties`).
   - **Titles:** Allow the user to select their active Group Title from their assigned roles (which updates the overhead avatar text in-world via `SetGroupActive`).
3. **Group Notices:**
   - **Receive/Read:** A tab to browse historical group notices and read incoming ones.
   - **Send:** A composition UI for authorized members to send new notices, including support for inventory item attachments.
4. **Land & Economy:**
   - Display group land area, tier contribution, and available credits.
   - UI for members to donate or reclaim their tier (Land Use) allowance to/from the group.

## Acceptance Criteria
- [ ] Authorized users can edit group metadata and manage roles/abilities through a native Godot UI.
- [ ] Users can successfully change their active Group Title.
- [ ] Group notices can be sent, received, and read (with attachments).
- [ ] Land tier contributions can be managed directly from the group window.
