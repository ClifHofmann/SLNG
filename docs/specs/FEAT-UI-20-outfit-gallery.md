# Feature: FEAT-UI-20 (Outfit Gallery)

## Context
The user requested an "Outfit Gallery" feature similar to Firestorm (*"in FS kann man zu jedem outfit ein bild speichern"*). This feature allows users to associate a visual snapshot with their saved outfits, making it much easier to browse and swap looks visually rather than reading through a list of text-based folder names.

## Requirements
1. **Outfit Image Capture:**
   - Provide a UI button within the Outfit/Inventory panel to "Take Snapshot for Outfit".
   - The captured image should be associated with the specific outfit folder (either saved locally in a `user://outfits/` directory keyed by the folder UUID, or uploaded as a texture if we want to sync it across machines, though local is faster and free).
2. **Gallery View UI:**
   - Introduce a visual grid layout (Gallery View) in the "Outfits" tab of the inventory.
   - Display the saved thumbnail for each outfit folder. 
   - Display a generic avatar placeholder icon for outfits that don't have a custom image yet.
3. **Interaction:**
   - Clicking or double-clicking an outfit thumbnail should trigger the "Wear Outfit" (Replace) command.
   - Right-click context menu should offer options to "Update Image", "Delete Outfit", or "Wear (Add)".

## Acceptance Criteria
- [ ] Users can capture and assign a thumbnail image to any outfit folder.
- [ ] The Outfits tab features a visual grid gallery mode displaying these thumbnails.
- [ ] Outfits can be equipped directly by interacting with their gallery thumbnail.
