using System.Text.Json.Serialization;

namespace SLNG.Core;

/// <summary>
/// BUG-PERF-22: record of a render-thread stall that triggered fallback to the main thread.
/// </summary>
public sealed class RenderThreadFallbackMarker
{
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("stalled_seconds")]
    public double StalledSeconds { get; set; }
}

