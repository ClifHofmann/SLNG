namespace SLNG.Core;

/// <summary>
/// How the radar map looks and behaves, as the player set it (FEAT-UI-39). Defaults and limits only:
/// reading and writing the preferences file belongs to the app, so this can be tested without one.
/// The defaults match Firestorm's mini-map: all three chat rings shown, north at the top, the view
/// returning to the avatar after a pan.
/// </summary>
public sealed class RadarViewSettings
{
    private float _visibleRangeMetres = RadarZoom.DefaultMetres;

    /// <summary>The master switch for the chat rings; each ring also has its own.</summary>
    public bool ChatRings { get; set; } = true;

    public bool WhisperRing { get; set; } = true;

    public bool SayRing { get; set; } = true;

    public bool ShoutRing { get; set; } = true;

    /// <summary>False = north at the top; true = whatever the camera looks at is at the top.</summary>
    public bool CameraUp { get; set; }

    /// <summary>True = a panned view eases back to the avatar once released.</summary>
    public bool AutoCenter { get; set; } = true;

    /// <summary>The zoom the player last chose, in metres across the shorter side of the map. Always
    /// within <see cref="RadarZoom.MinMetres"/> to <see cref="RadarZoom.MaxMetres"/>.</summary>
    public float VisibleRangeMetres
    {
        get => _visibleRangeMetres;
        set => _visibleRangeMetres = RadarZoom.Clamp(value);
    }
}
