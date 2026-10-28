using System.IO;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-76: the Preferences tab for the debug log. One switch (it takes effect at once), a plain statement of what
/// the log contains and what has been taken out of it, and the way to the file: the folder, the current file selected in
/// the file manager, and the path to copy. The file is the thing a person is asked to send when something is wrong, so
/// finding it must not take a search through <c>%APPDATA%</c>.
/// </summary>
public partial class DebugLogPreferencesPage : VBoxContainer
{
    private CheckBox _enabled = null!;
    private Label _status = null!;
    private LineEdit _path = null!;
    private Button _showFile = null!;
    private bool _refreshing;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 8);
    }

    /// <summary>Builds the page. Call once, right after PreferencesWindow.AddTab.</summary>
    public void Initialize()
    {
        _enabled = new CheckBox { Text = L10n.Tr("ui.debuglog.enable"), FocusMode = FocusModeEnum.None };
        _enabled.Toggled += on =>
        {
            if (_refreshing) return;
            DebugLogSettings.SetEnabled(on);
            Refresh();
        };
        AddChild(_enabled);

        AddChild(Wrapped(L10n.Tr("ui.debuglog.what"), 12, new Color(0.85f, 0.85f, 0.85f)));
        AddChild(Wrapped(L10n.Tr("ui.debuglog.privacy"), 11, new Color(0.7f, 0.7f, 0.7f)));
        AddChild(Wrapped(L10n.Tr("ui.debuglog.limits"), 11, new Color(0.7f, 0.7f, 0.7f)));

        AddChild(new Label { Text = L10n.Tr("ui.debuglog.folder") });
        // A read-only field rather than a label: it can be selected and copied by hand as well.
        _path = new LineEdit { Editable = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(_path);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        var openFolder = new Button { Text = L10n.Tr("ui.debuglog.open_folder"), FocusMode = FocusModeEnum.None };
        openFolder.Pressed += OpenFolder;
        _showFile = new Button { Text = L10n.Tr("ui.debuglog.show_file"), FocusMode = FocusModeEnum.None };
        _showFile.Pressed += ShowFile;
        var copy = new Button { Text = L10n.Tr("ui.debuglog.copy_path"), FocusMode = FocusModeEnum.None };
        copy.Pressed += () => DisplayServer.ClipboardSet(DebugLog.CurrentFile ?? DebugLog.DirectoryPath);
        buttons.AddChild(openFolder);
        buttons.AddChild(_showFile);
        buttons.AddChild(copy);
        AddChild(buttons);

        _status = Wrapped("", 11, new Color(0.7f, 0.7f, 0.7f));
        AddChild(_status);

        // The size grows while the log is on; keep the line current while the page is on screen.
        var timer = new Timer { WaitTime = 2.0, Autostart = true };
        timer.Timeout += () => { if (IsVisibleInTree()) Refresh(); };
        AddChild(timer);

        DebugLog.StateChanged += () => { if (IsInsideTree()) CallDeferred(MethodName.Refresh); };
        Refresh();
    }

    private static Label Wrapped(string text, int fontSize, Color color)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(300, 0), // a width floor: without it the wrapped height is measured at ~0 px
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    /// <summary>Shows the live state: the switch, the path and how much has been written.</summary>
    public void Refresh()
    {
        _refreshing = true;
        _enabled.ButtonPressed = DebugLog.IsRunning;
        _refreshing = false;

        string? file = DebugLog.CurrentFile;
        _path.Text = file ?? DebugLog.DirectoryPath;
        _showFile.Disabled = file == null || !File.Exists(file);
        _status.Text = file == null
            ? L10n.Tr("ui.debuglog.status_off")
            : L10n.TrFormat("ui.debuglog.status_on", Path.GetFileName(file), FormatSize(DebugLog.BytesWritten));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };

    private static void OpenFolder()
    {
        string dir = DebugLog.DirectoryPath;
        Directory.CreateDirectory(dir); // shown even before the log was ever switched on
        OS.ShellShowInFileManager(dir);
    }

    private static void ShowFile()
    {
        if (DebugLog.CurrentFile is { } file && File.Exists(file)) OS.ShellShowInFileManager(file);
    }
}
