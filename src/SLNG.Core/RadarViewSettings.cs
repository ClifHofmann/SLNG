namespace SLNG.Core;

/// <summary>
/// How the radar map looks and behaves, as the player set it (FEAT-UI-39). Defaults and limits only:
/// reading and writing the preferences file belongs to the app, so this can be tested without one.
/// The defaults match Firestorm's mini-map: all three chat rings shown, objects shown, north at the top,
/// the view returning to the avatar after a pan.
/// </summary>
public sealed class RadarViewSettings
{
    private float _visibleRangeMetres = RadarZoom.DefaultMetres;
    private float _objectMinSizeMetres = RadarObjects.DefaultMinSizeMetres;

    /// <summary>The master switch for the chat rings; each ring also has its own.</summary>
    public bool ChatRings { get; set; } = true;

    public bool WhisperRing { get; set; } = true;

    public bool SayRing { get; set; } = true;

    public bool ShoutRing { get; set; } = true;

    /// <summary>Whether the map shows the prims you own and the big ones as squares (the viewer's "Show
    /// objects"). On by default, as in Firestorm; off, the radar neither scans nor draws them.</summary>
    public bool ShowObjects { get; set; } = true;

    /// <summary>How long a prim you do not own must be (its footprint, in metres) to be shown. Always one of
    /// <see cref="RadarObjects.MinSizePresetsMetres"/>: anything else is moved to the nearest.</summary>
    public float ObjectMinSizeMetres
    {
        get => _objectMinSizeMetres;
        set => _objectMinSizeMetres = RadarObjects.ClampMinSize(value);
    }

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
