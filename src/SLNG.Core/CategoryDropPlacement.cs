namespace SLNG.Core;

/// <summary>Where a category being dragged would land relative to the category it is held over.</summary>
public enum CategoryDropPlacement
{
    /// <summary>Nowhere: the drop would change nothing (onto itself, an unknown name, already last).</summary>
    None,

    /// <summary>Above the target.</summary>
    Above,

    /// <summary>Below the target.</summary>
    Below,
}
