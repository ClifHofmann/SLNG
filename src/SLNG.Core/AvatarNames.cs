namespace SLNG.Core;

/// <summary>
/// How an avatar's name reads in a list (FEAT-UI-39). Second Life gives every account a legacy
/// last name; accounts made without a choice of one get "Resident", and the reference viewers leave
/// it off ("Oz", not "Oz Resident"). A list that prints it is noise on every row.
/// </summary>
public static class AvatarNames
{
    /// <summary>The legacy last name an account gets when it never chose one.</summary>
    public const string DefaultLastName = "Resident";

    /// <summary>
    /// The name to show for an avatar whose parts are known. A Display Name the resident chose is
    /// shown as it is; the default one (which is just the legacy name) and the legacy name itself
    /// lose a "Resident" last name. Empty when there is nothing to show, so the caller can fall back
    /// to something else (the agent id).
    /// </summary>
    public static string ForList(string? displayName, string? firstName, string? lastName)
    {
        string first = firstName?.Trim() ?? "";
        string last = lastName?.Trim() ?? "";
        string legacy = last.Length == 0 || last == DefaultLastName ? first : $"{first} {last}".Trim();

        string display = displayName?.Trim() ?? "";
        if (display.Length == 0) return legacy;

        // The grid reports the legacy name as the Display Name when none was ever set; that must
        // read the same as the legacy name, not keep the "Resident" the legacy form drops.
        return display == $"{first} {last}".Trim() ? legacy : display;
    }

    /// <summary>
    /// For a name that is only a string (a cached name for an avatar we hold no entity for): drops a
    /// trailing "Resident" when the name is exactly one word followed by it. A name of any other
    /// shape is left alone -- "Local Resident" cannot be told from a Display Name someone chose, and
    /// an unwanted word is a smaller harm than a mangled name.
    /// </summary>
    public static string WithoutDefaultLastName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var parts = name.Split(' ');
        return parts.Length == 2 && parts[0].Length > 0 && parts[1] == DefaultLastName ? parts[0] : name;
    }
}
