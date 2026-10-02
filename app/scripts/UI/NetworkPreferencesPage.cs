using System.Collections.Generic;
using System.IO;
using Godot;

namespace SLNG.App.UI;

public partial class NetworkPreferencesPage : VBoxContainer
{
    private string _cacheDir = null!;
    private System.Func<IReadOnlyList<string>> _objectCacheDirs = () => System.Array.Empty<string>();
    private System.Action? _clearObjectCache;
    private Label _sizeLabel = null!;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 10);
    }

    /// <param name="cacheDir">The asset cache directory.</param>
    /// <param name="objectCacheDirs">FEAT-NET-04: where the object cache keeps one file per region.
    /// BUG-GRID-01: there is one such directory per grid, so this is asked each time rather than
    /// fixed at startup, before any login has said which grid.</param>
    /// <param name="clearObjectCache">Forgets what the running session holds of the object cache in
    /// memory (there may be no session yet); the files are removed here either way.</param>
    public void Initialize(string cacheDir, System.Func<IReadOnlyList<string>>? objectCacheDirs = null,
        System.Action? clearObjectCache = null)
    {
        _cacheDir = cacheDir;
        if (objectCacheDirs != null) _objectCacheDirs = objectCacheDirs;
        _clearObjectCache = clearObjectCache;

        var heading = new Label { Text = L10n.Tr("ui.preferences.network_heading") };
        heading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        AddChild(heading);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        AddChild(row);

        var clearBtn = new Button { Text = L10n.Tr("ui.preferences.clear_cache") };
        clearBtn.Pressed += ClearCache;
        row.AddChild(clearBtn);

        _sizeLabel = new Label { Text = GetCacheSizeText() };
        row.AddChild(_sizeLabel);
    }

    private void ClearCache()
    {
        try
        {
            DeleteFiles(_cacheDir, "*");

            // FEAT-NET-04: the object cache goes with it. The running session first, so it does not
            // write back what it still holds in memory; then whatever is on disk.
            _clearObjectCache?.Invoke();
            foreach (var dir in _objectCacheDirs())
                DeleteFiles(dir, "*.slobj*");

            _sizeLabel.Text = GetCacheSizeText();
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[NetworkPreferences] Failed to clear cache: {ex.Message}");
        }
    }

    private static void DeleteFiles(string directory, string pattern)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

        foreach (var file in Directory.GetFiles(directory, pattern))
        {
            try { File.Delete(file); } catch { }
        }
    }

    private string GetCacheSizeText()
        => L10n.TrFormat("ui.preferences.cache_size",
            FormatSize(DirectorySize(_cacheDir, "*")),
            FormatSize(ObjectCacheSize()));

    private long ObjectCacheSize()
    {
        long total = 0;
        foreach (var dir in _objectCacheDirs())
            total += DirectorySize(dir, "*.slobj");
        return total;
    }

    private static long DirectorySize(string directory, string pattern)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return 0;

        long size = 0;
        try
        {
            foreach (var file in Directory.GetFiles(directory, pattern))
                size += new FileInfo(file).Length;
        }
        catch { }
        return size;
    }

    /// <summary>Whole megabytes once it is large, one decimal while it is small: an object cache of
    /// a few regions is a few megabytes, and "0 MB" for it would read as "empty".</summary>
    private static string FormatSize(long bytes)
        => bytes < 10L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.0} MB" : $"{bytes / (1024 * 1024)} MB";
}
