namespace SLNG.Core.ChatLogs;

/// <summary>How a P2P IM log file is named after the other person. Firestorm's setting
/// <c>UseLegacyIMLogNames</c> (global, <c>user_settings/settings.xml</c>; default ON in the source,
/// OFF on the maintainer's machine).</summary>
public enum ImLogNameStyle
{
    /// <summary><c>First Last</c> -- the legacy name, case kept, " Resident" dropped
    /// (<c>llimview.cpp</c> <c>buildHistoryFileName</c>).</summary>
    Legacy,

    /// <summary><c>first_last</c> -- <c>LLCacheName::buildUsername</c>: lower case, <c>.</c> between
    /// the names, then <c>cleanFileName</c> turns the <c>.</c> into <c>_</c>.</summary>
    Account,
}
