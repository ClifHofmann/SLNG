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
    private NetworkSettings _networkSettings = null!;
    private System.Action<float>? _onMaxBandwidthChanged;
    private bool _dragging;

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
    /// <param name="networkSettings">FEAT-NET-05: holds the persisted maximum bandwidth.</param>
    /// <param name="onMaxBandwidthChanged">Applies a new maximum bandwidth to the running session (there
    /// may be none yet). Every call makes the viewer resend its throttle to every simulator, so the
    /// slider only calls it once a drag is over, not on each tick.</param>
    public void Initialize(string cacheDir, NetworkSettings networkSettings,
        System.Action<float>? onMaxBandwidthChanged = null,
        System.Func<IReadOnlyList<string>>? objectCacheDirs = null,
        System.Action? clearObjectCache = null)
    {
        _cacheDir = cacheDir;
        _networkSettings = networkSettings;
        _onMaxBandwidthChanged = onMaxBandwidthChanged;
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

        AddMaxBandwidthRow();
    }

    /// <summary>FEAT-NET-05: the bandwidth slider. The value label follows the drag, but the setting
    /// is saved and applied only when a drag ends (or on a keyboard / wheel step, which has no drag):
    /// applying it resends the throttle to every simulator, which must not happen on every tick.</summary>
    private void AddMaxBandwidthRow()
    {
        var caption = new Label { Text = L10n.Tr("ui.preferences.max_bandwidth") };
        caption.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.75f));
        AddChild(caption);

        var sliderRow = new HBoxContainer();
        sliderRow.AddThemeConstantOverride("separation", 12);
        AddChild(sliderRow);

        var initial = _networkSettings.MaxBandwidthKbps;
        var slider = new HSlider
        {
            MinValue = NetworkSettings.MinBandwidthKbps,
            MaxValue = NetworkSettings.CeilingBandwidthKbps,
            Step = 100,
            Value = initial,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(200, 0),
        };
        sliderRow.AddChild(slider);

        var valueLabel = new Label
        {
            Text = FormatKbps(initial),
            CustomMinimumSize = new Vector2(80, 0),
        };
        valueLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        sliderRow.AddChild(valueLabel);

        slider.DragStarted += () => _dragging = true;
        slider.DragEnded += valueChanged =>
        {
            _dragging = false;
            if (valueChanged) ApplyMaxBandwidth((float)slider.Value);
        };
        slider.ValueChanged += value =>
        {
            valueLabel.Text = FormatKbps(value);
            if (!_dragging) ApplyMaxBandwidth((float)value);
        };

        var hint = new Label
        {
            Text = L10n.Tr("ui.preferences.max_bandwidth_hint"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
        hint.AddThemeFontSizeOverride("font_size", 12);
        AddChild(hint);
    }

    private void ApplyMaxBandwidth(float kbps)
    {
        _networkSettings.SetMaxBandwidthKbps(kbps);
        _onMaxBandwidthChanged?.Invoke(_networkSettings.MaxBandwidthKbps);
    }

    private static string FormatKbps(double kbps) => $"{kbps:0} kbps";

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
