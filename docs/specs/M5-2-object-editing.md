# [M5-2] In-World Object Selection & Editing

- **Feature ID:** `M5-2`
- **Track:** `render` / `ui` / `net`
- **Status:** `🚧 In Progress`
- **Owner:** `gemini`
- **Branch:** `feature/M5-2-object-editing`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md#L123)

---

## 1. Goal & Scope


Enable users to interact with and edit 3D objects directly within the rendered viewport. Right-clicking an object in the world displays a context menu (e.g., *Touch*, *Edit*, *Inspect*, *Delete*). Selecting **Edit** opens a floating Object Editing window (*Build / Inspector*) allowing users to view and modify transform parameters, object properties, textures, and prim parameters, syncing changes back to the Second Life / OpenSim simulator via `SLNG.Net`.

---

## 2. User Workflow & Experience

1. **Object Raycasting & Selection:**
   - User right-clicks (or left-clicks with selection tool active) on an object in the 3D viewport.
   - Godot performs a 3D raycast from screen coordinates to detect the targeted `CollisionObject3D` / prim mesh.
   - The targeted object highlights with a selection bounding box / outline gizmo.

2. **In-World Context Menu:**
   - A context menu pops up at the cursor position (styled per SLNG UI standards using `SLNGWindow` floating panel or unified popup control).
   - **Menu Actions:**
     - ✏️ **Edit**: Opens the Object Edit Inspector Window.
     - ✋ **Touch / Use**: Triggers a touch action on the object (sends `ObjectSelect` / `ObjectGrab` / script touch event).
     - 🔍 **Inspect**: Opens object properties (Owner, Creator, Group, Prim Count, Script Info).
     - 🗑️ **Delete**: Deletes the object if permissions allow.
     - 📦 **Take / Take Copy**: Takes the object to inventory if permissions allow.     

3. **Object Edit Inspector Window (Based on Firestorm/SL Standard):**
   - Inherits from `SLNG.App.UI.SLNGWindow` to maintain dark glassmorphism styling and window behavior.
   - **Tabs & Prioritization:**
     - **General (Allgemein) - [High Priority]**:
       - Basic Object Info: Name (Editable), Description (Editable).
       - Read-only IDs: Creator, Owner, Previous Owner, Group.
       - *Low Priority / Later:* On-Click Action, Permissions (Modify/Copy/Transfer), For Sale / Price.
     - **Object (Objekt) - [High Priority]**:
       - State Checkboxes: Locked, Physical, Temporary, Phantom (Read/Write).
       - Transform Inputs (Spinboxes): Position (X, Y, Z), Size/Scale (X, Y, Z), Rotation (X, Y, Z in Euler degrees).
       - *Low Priority / Later:* Mesh-Information (LOD triangle counts), Object-LOD-Behavior settings.
     - **Features (Eigensch.) - [Medium Priority]**:
       - Material dropdown (Wood, Metal, Glass, etc.).
       - Light properties (Checkbox + Intensity, Radius, Falloff, Color) - *Important for engine testing*.
       - Physics Shape Type (Prim, Convex Hull, None).
       - *Low Priority / Later:* Flexiprim (Flexible Path) parameters, advanced physics (Gravity, Friction, Restitution).
     - **Texture (Textur) - [High Priority for rendering]**:
       - Support for PBR / Blinn-Phong sub-tabs.
       - Base Color / Diffuse map viewer, Normal map, ORM/Specular map, Emissive map.
       - Texture mapping: Scale (U, V), Offset (U, V), Rotation.
       - Alpha, Transparency modes (Opaque, Alpha Blending, Alpha Masking).
     - **Content (Inhalt) - [Medium Priority]**:
       - Fetch and display the inventory tree of items inside the prim (Scripts, animations, etc.).
       - Buttons: New Script, Permissions.

4. **Transform Gizmos (3D Handles):**
   - 3-axis translation, rotation, and scaling gizmos rendered in 3D around the selected object in the Godot scene.
   - Dragging handles updates the object transform live in-world and emits updates to the server.

---

## 3. Architecture & Layering

Following the SLNG core boundaries (`app` → `SLNG.Assets` / `SLNG.Net` → `SLNG.Core`):

```
┌─────────────────────────────────────────────────────────────┐
│                       app (Godot 4)                         │
│  - ObjectSelectionController (Raycast 3D + Viewport input)  │
│  - InWorldContextMenu (UI Popup / SLNGWindow)               │
│  - ObjectEditWindow (SLNGWindow tabbed inspector)          │
│  - SelectionGizmo3D (Translation / Rotation / Scale)        │
└──────────────────────────────┬──────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────┐
│                         SLNG.Core                           │
│  - World Entity Selection State (SelectedLocalID, UUID)    │
│  - PrimTransformComponent & PrimShapeComponent updates      │
└──────────────────────────────┬──────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────┐
│                          SLNG.Net                           │
│  - ObjectSelect / ObjectDeselect packets                   │
│  - MultipleObjectPosition / Rotation / Scale updates       │
│  - ObjectShape / ObjectFlag packets                        │
└─────────────────────────────────────────────────────────────┘
```

---

## 4. Acceptance Criteria

- [ ] **Raycast Selection:** Right-clicking or left-clicking any prim in the viewport accurately identifies the entity LocalID and UUID.
- [ ] **Context Menu:** Right-click displays a context menu inheriting from or aligned with `SLNGWindow` design standards, offering *Touch*, *Edit*, *Inspect*, *Delete*.
- [ ] **Selection Highlight:** Selected object shows a visual bounding box / outline in 3D space.
- [ ] **Edit Window:** Clicking *Edit* opens a tabbed `SLNGWindow` populated with the selected object's name, transform, and prim details.
- [ ] **Transform Syncing:** Editing Position/Rotation/Scale input fields updates the Godot scene node and sends the updated transform over `SLNG.Net` to OpenSim/SL.
- [ ] **Server Deselect:** Closing the edit window or clicking outside deselects the object and sends `ObjectDeselect`.

---

## 5. Next Steps & Milestones

1. **Step 1:** Add Raycast selection logic in `app/` and raise selection events.
2. **Step 2:** Build `InWorldContextMenu` and `ObjectEditWindow` UI components based on `SLNGWindow`.
3. **Step 3:** Implement `SLNG.Net` methods for `SelectObject`, `DeselectObject`, and `UpdateObjectTransform`.
4. **Step 4:** Add 3D Selection Gizmo in Godot.
