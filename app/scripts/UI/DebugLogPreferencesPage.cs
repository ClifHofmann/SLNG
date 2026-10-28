using System.IO;
using Godot;
using SLNG.App.UI.Prefs;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-76: the "Debug log" section of the Help &amp; info category (FEAT-UI-79). One switch (it takes effect at once), a
/// plain statement of what the log contains and what has been taken out of it, and the way to the file: the folder, the
/// current file selected in the file manager, and the path to copy. The file is the thing a person is asked to send when
/// something is wrong, so finding it must not take a search through <c>%APPDATA%</c>.
///
/// This is a Node, not a plain object: <see cref="DebugLog.StateChanged"/> can be raised off the main thread and comes back
/// through <c>CallDeferred</c>, and the size line is kept current by a <see cref="Timer"/>. <see cref="Build"/> puts it in
/// the section so it is in the tree.
/// </summary>
public partial class DebugLogPreferencesPage : Node
{
    private PrefsSection _section = null!;
    private CheckBox _enabled = null!;
    private Label _status = null!;
    private LineEdit _path = null!;
    private Button _showFile = null!;
    private bool _refreshing;

    /// <summary>Fills <paramref name="section"/> (Help &amp; info, "debug_log"), which Boot has already put in its place.
    /// Call once, after the section is in the window's tree.</summary>
    public void Build(PrefsSection section)
    {
        _section = section;
        section.AddChild(this);
        string prefix = $"{section.CategoryId}.{section.Id}";

        _enabled = section.AddCheck($"{prefix}.enable", "ui.debuglog.enable", false, on =>
            {
                if (_refreshing) return;
                DebugLogSettings.SetEnabled(on);
                Refresh();
            })
            .ControlAs<CheckBox>()!;

        section.AddNote($"{prefix}.what", "ui.debuglog.what");
        section.AddNote($"{prefix}.privacy", "ui.debuglog.privacy");
        section.AddNote($"{prefix}.limits", "ui.debuglog.limits");

        // A read-only field rather than a label: it can be selected and copied by hand as well.
        _path = new LineEdit { Editable = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        section.AddControl($"{prefix}.folder", "ui.debuglog.folder", _path);

        var buttons = new HFlowContainer(); // wraps rather than pushing the page wider than a narrow window
        buttons.AddThemeConstantOverride("h_separation", PrefsStyle.ColumnGap);
        buttons.AddThemeConstantOverride("v_separation", PrefsStyle.RowSeparation);
        var openFolder = new Button { Text = L10n.Tr("ui.debuglog.open_folder"), FocusMode = Control.FocusModeEnum.None };
        openFolder.Pressed += OpenFolder;
        _showFile = new Button { Text = L10n.Tr("ui.debuglog.show_file"), FocusMode = Control.FocusModeEnum.None };
        _showFile.Pressed += ShowFile;
        var copy = new Button { Text = L10n.Tr("ui.debuglog.copy_path"), FocusMode = Control.FocusModeEnum.None };
        copy.Pressed += () => DisplayServer.ClipboardSet(DebugLog.CurrentFile ?? DebugLog.DirectoryPath);
        buttons.AddChild(openFolder);
        buttons.AddChild(_showFile);
        buttons.AddChild(copy);
        section.AddBlock($"{prefix}.buttons", buttons, null, false,
            "ui.debuglog.open_folder", "ui.debuglog.show_file", "ui.debuglog.copy_path");

        _status = PrefsStyle.MakeHint("");
        section.AddBlock($"{prefix}.status", _status);

        // The size grows while the log is on; keep the line current while the section is on screen.
        var timer = new Timer { WaitTime = 2.0, Autostart = true };
        timer.Timeout += () => { if (_section.IsVisibleInTree()) Refresh(); };
        AddChild(timer);

        DebugLog.StateChanged += () => { if (IsInsideTree()) CallDeferred(MethodName.Refresh); };
        Refresh();
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
