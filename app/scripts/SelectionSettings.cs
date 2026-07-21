namespace SLNG.App;

/// <summary>Viewer-wide UI toggles for object selection/editing behavior. Deliberately a small
/// static (like RenderConfig.DrawDistance) rather than plumbed through a shared reference: both
/// ObjectSelectionController (click routing) and ObjectEditWindow (EditObject's own
/// root-resolution) need the same current value and have no other shared reference to each
/// other.</summary>
public static class SelectionSettings
{
    /// <summary>SL/Firestorm's "Edit Linked Parts" toggle (see FEAT-UI-06). Off (default):
    /// clicking any part of a linkset selects/edits the root, matching pre-existing behavior.
    /// On: clicking a part selects/edits that SPECIFIC part directly.</summary>
    public static bool EditLinkedParts = false;
}
