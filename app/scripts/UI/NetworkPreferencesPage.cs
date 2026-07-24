using System.IO;
using Godot;

namespace SLNG.App.UI;

public partial class NetworkPreferencesPage : VBoxContainer
{
    private string _cacheDir = null!;
    private Label _sizeLabel = null!;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 10);
    }

    public void Initialize(string cacheDir)
    {
        _cacheDir = cacheDir;

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
        if (string.IsNullOrEmpty(_cacheDir) || !Directory.Exists(_cacheDir)) return;

        try
        {
            var files = Directory.GetFiles(_cacheDir);
            foreach (var file in files)
            {
                try { File.Delete(file); } catch { }
            }
            _sizeLabel.Text = GetCacheSizeText();
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[NetworkPreferences] Failed to clear cache: {ex.Message}");
        }
    }

    private string GetCacheSizeText()
    {
        if (string.IsNullOrEmpty(_cacheDir) || !Directory.Exists(_cacheDir)) return "0 MB";
        
        long size = 0;
        try
        {
            var files = Directory.GetFiles(_cacheDir);
            foreach (var file in files)
            {
                size += new FileInfo(file).Length;
            }
        }
        catch { }

        return $"{size / (1024 * 1024)} MB";
    }
}
