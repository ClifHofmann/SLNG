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

    /// <summary>Reads and deletes the marker. FEAT-UI-81: with <paramref name="keepCopyPath"/> the marker is also written
    /// there first, so the Preferences page can keep saying why the render thread is off after the one-time toast.</summary>
    public static bool TryConsumeMarker(string markerFilePath, out RenderThreadFallbackMarker? marker, string? keepCopyPath = null)
    {
        marker = null;
        if (!File.Exists(markerFilePath)) return false;

        try
        {
            string json = File.ReadAllText(markerFilePath);
            marker = JsonSerializer.Deserialize<RenderThreadFallbackMarker>(json);
            if (keepCopyPath != null && marker != null) File.WriteAllText(keepCopyPath, json);
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

    /// <summary>The copy <see cref="TryConsumeMarker"/> kept, or null when there is none or it cannot be read.</summary>
    public static RenderThreadFallbackMarker? ReadKept(string keepCopyPath)
    {
        try
        {
            return File.Exists(keepCopyPath)
                ? JsonSerializer.Deserialize<RenderThreadFallbackMarker>(File.ReadAllText(keepCopyPath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Removes the kept copy (the person switched the separate thread back on). No error when there is none.</summary>
    public static void ClearKept(string keepCopyPath)
    {
        try { File.Delete(keepCopyPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

