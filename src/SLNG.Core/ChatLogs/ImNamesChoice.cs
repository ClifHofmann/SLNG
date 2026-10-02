namespace SLNG.Core.ChatLogs;

/// <summary>How IM log files are named.</summary>
public enum ImNamesChoice
{
    /// <summary>As Firestorm is set up on this machine (<c>UseLegacyIMLogNames</c>; its default
    /// when it is not installed or the setting is untouched: legacy names).</summary>
    Auto,
    Legacy,
    Account,
}
