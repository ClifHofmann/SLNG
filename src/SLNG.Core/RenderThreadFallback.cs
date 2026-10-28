using System;
using System.IO;
using System.Text.Json;

namespace SLNG.Core;

/// <summary>
/// BUG-PERF-22: marker file management for render-thread fallback.
/// </summary>
public static class RenderThreadFallback
{
    public static bool WriteMarker(string markerFilePath, double stalledSeconds)
    {
        try
        {
            string dir = Path.GetDirectoryName(markerFilePath) ?? string.Empty;
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var marker = new RenderThreadFallbackMarker
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                StalledSeconds = Math.Round(stalledSeconds, 1)
            };

            string json = JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true });
            string tempFile = Path.Combine(dir, $"{Path.GetFileName(markerFilePath)}.{Guid.NewGuid():N}.tmp");

            File.WriteAllText(tempFile, json);
            File.Move(tempFile, markerFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RenderThreadFallback] Failed to write marker {markerFilePath}: {ex.Message}");
            return false;
        }
    }

    public static bool TryConsumeMarker(string markerFilePath, out RenderThreadFallbackMarker? marker)
    {
        marker = null;
        if (!File.Exists(markerFilePath)) return false;

        try
        {
            string json = File.ReadAllText(markerFilePath);
            marker = JsonSerializer.Deserialize<RenderThreadFallbackMarker>(json);
            File.Delete(markerFilePath);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RenderThreadFallback] Failed to read/delete marker {markerFilePath}: {ex.Message}");
            try { File.Delete(markerFilePath); } catch { }
            return false;
        }
    }
}

