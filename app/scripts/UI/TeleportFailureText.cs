using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// The sentence for a teleport failure. The simulator sends an id (<c>no_host</c>); the reference viewer shows its table's sentence, and
/// so does this: <see cref="TeleportFailureReasons"/> names the key, <see cref="L10n"/> says it in the current language. Text that is not
/// a known id (a timeout, a sentence the grid wrote) is returned unchanged.
/// </summary>
public static class TeleportFailureText
{
    public static string For(string? message)
    {
        string? key = TeleportFailureReasons.KeyFor(message);
        return key != null ? L10n.Tr(key) : message ?? "";
    }
}
