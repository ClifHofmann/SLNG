namespace SLNG.Core;

/// <summary>
/// Which chat range a distance falls in (FEAT-UI-39) -- the same three bands Firestorm tints the
/// range cell by: whisper-to-say distance, shout distance, and out of earshot.
/// </summary>
public enum RangeBand
{
    /// <summary>Within normal chat range: this avatar can read what you say.</summary>
    Say,
    /// <summary>Beyond normal chat but within shout range.</summary>
    Shout,
    /// <summary>Beyond shout range.</summary>
    Far,
}
