namespace SLNG.Core.ChatLogs;

/// <summary>Where chat logs are kept. The setting in Preferences.</summary>
public enum ChatLogMode
{
    /// <summary>Follow Firestorm: its profile folder, or the log folder the person set in Firestorm
    /// for that account.</summary>
    Firestorm,

    /// <summary>SLNG's own folder (<c>%APPDATA%\SLNG\logs\chat</c>), in Firestorm's layout inside.</summary>
    Slng,

    /// <summary>A folder the person chose, in Firestorm's layout inside.</summary>
    Custom,
}
