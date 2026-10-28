namespace SLNG.App.UI.Prefs;

/// <summary>
/// When a changed setting takes effect (FEAT-UI-79). <see cref="Immediately"/> is the norm and shows nothing; the
/// other three put a small badge after the row's label so the person is not left wondering why nothing happened.
/// </summary>
public enum PrefsApply
{
    /// <summary>Applied the moment the control is changed. No badge.</summary>
    Immediately,

    /// <summary>Needs a restart of the viewer.</summary>
    Restart,

    /// <summary>Applied at the next login.</summary>
    NextLogin,

    /// <summary>Applied to windows opened from now on; open ones keep the old value (the language).</summary>
    ReopenWindows,
}
