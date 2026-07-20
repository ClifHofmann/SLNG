# Handover for Claude (UI Updates)

## Unified Window System (`SLNGWindow`)
We have successfully implemented a unified window system based on a modern React UI mockup. 

**Important Rules & Context:**
1. **Inheritance:** ALL floating, draggable UI windows (e.g. `CameraHUD`, `InventoryPanel`, `ItemPropertiesWindow`, etc.) **MUST** inherit from `SLNG.App.UI.SLNGWindow`. 
2. **Never** use native Godot `Window` nodes or bare `PanelContainer`s for in-game popups anymore. 
3. **Styling:** `SLNGWindow` automatically provides:
   - A consistent glassmorphism background (`bg-black/50`, blurred-like shadow, thin border).
   - A modern header with a title and close button (perfectly aligned).
   - Built-in drag logic via the title bar.
   - Built-in resize logic via a subtle resize handle in the bottom right corner.
4. **Usage:** 
   - Set the title via the `Title` property (e.g. `Title = "Inventory";`). It automatically converts to uppercase.
   - Add your UI elements to the `ContentContainer` (a `MarginContainer`), NOT directly to the window via `AddChild`.
   - Call `base._Ready()` in your overrides.

The canonical rule has also been added to `AGENTS.md`. Happy coding!
