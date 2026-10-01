namespace SLNG.Core;

/// <summary>The three chat ranges the radar can draw as rings around the local avatar
/// (FEAT-UI-39). The defaults of Second Life; a region's own values (OpenSim's per-grid
/// say/whisper/shout range) replace them later.</summary>
public static class RadarChatRings
{
    public const float WhisperMetres = 10f;
    public const float SayMetres = 20f;
    public const float ShoutMetres = 100f;
}
