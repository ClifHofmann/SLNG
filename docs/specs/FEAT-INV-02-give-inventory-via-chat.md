# Feature Specification: FEAT-INV-02 — Give Inventory Items via Chat / IM Window

**Feature ID:** `FEAT-INV-02`  
**Status:** 🚧 In Progress  
**Owner:** `gemini`  
**Target Milestone:** Post-MVP3 / Utility Enhancements  

---

## 1. Overview
Enables avatars to give inventory items or folders to other avatars via the active IM Chat tab in `ChatWindow` and via context menu in `InventoryPanel`, respecting Second Life / OpenSim permission rules (No-Transfer enforcement).

---

## 2. Requirements & Behavior

### 2.1 Permission Rules Enforcement
- **Transfer Permission Check (`CanTransfer`)**:
  - Only items/folders with `CanTransfer == true` can be given.
  - If `CanTransfer == false` (No-Transfer), the "Weitergeben / Give" context menu item in `InventoryPanel` MUST be disabled.
  - Drag-and-drop of a No-Transfer item onto a chat window will be rejected with an error message in the chat log: `"[System] 'ItemName' kann nicht übertragen werden (keine Transfer-Rechte)."`

### 2.2 Methods of Giving Inventory Items
1. **Drag-and-Drop to Chat Window / IM Tab**:
   - User drags an item or folder from `InventoryPanel` into an open IM tab in `ChatWindow`.
   - `ChatWindow` checks if the active tab is an IM conversation with a target avatar (`TargetAgentId != null`).
   - On drop, permission is checked and `GridSession.GiveItemAsync` / `GridSession.GiveFolderAsync` is invoked.
2. **Context Menu "Weitergeben / Give..." in Inventory Tree**:
   - User right-clicks an item in `InventoryPanel`.
   - Context menu shows "Weitergeben / Give..." (disabled if `CanTransfer == false`).
   - If clicked, it sends the item to the currently active IM conversation target in `ChatWindow` (or prompts if no IM tab is active).

### 2.3 Visual Feedback & Log Messages
- When an item is successfully offered:
  - Beam effect / packet sent to recipient via `LibreMetaverse`.
  - Chat log in the IM tab shows system notice: `"[System] 'ItemName' an AvatarName angeboten."`

---

## 3. Protocol & Architecture

```
[InventoryPanel] (Drag / Context Menu)
      │
      ▼ (Checks CanTransfer)
[ChatWindow] (IM Tab DropTarget)
      │
      ▼
[GridSession] (GiveItemAsync / GiveFolderAsync)
      │
      ▼
[LibreMetaverse.InventoryManager] (GiveItem / GiveFolder InstantMessage packet)
```
