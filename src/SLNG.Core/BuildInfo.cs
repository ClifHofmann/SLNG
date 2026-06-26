namespace SLNG.Core;

/// <summary>
/// Static metadata about the SLNG build. This is a seed type for the M0-1
/// scaffold so the solution has something real to compile and test against;
/// the world model proper arrives in task M1-1.
/// </summary>
public static class BuildInfo
{
    /// <summary>Product name.</summary>
    public const string Name = "SLNG";

    /// <summary>Current development stage.</summary>
    public const string Stage = "pre-alpha";
}
