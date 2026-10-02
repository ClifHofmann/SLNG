using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SLNG.Core.ChatLogs;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-41: "Chat logs" tab of Preferences. Where logs are kept (Firestorm's folder, SLNG's own,
/// or one the person chooses), how IM files are named, which folder is in use right now, and the
/// explicit, user-triggered import of logs older SLNG builds wrote. Logs are in Firestorm's layout
/// whatever the choice, so either viewer reads what the other wrote.
/// </summary>
public partial class ChatLogPreferencesPage : VBoxContainer
{
    private Func<ChatLogTarget?> _activeTarget = () => null;
    private Func<ChatLogImportContext?> _importContext = () => null;

    private OptionButton _location = null!;
    private LineEdit _custom = null!;
    private Button _browse = null!;
    private OptionButton _names = null!;
    private Label _active = null!;
    private Button _importButton = null!;
    private Label _importStatus = null!;

    // Handed from the worker thread to the main thread; only read after CallDeferred.
    private ChatLogImportPlan? _plan;
    private ChatLogImportContext? _planContext;
    private string? _error;
    private ChatLogImportPlan? _applied;
    private ChatLogImportContext? _appliedContext;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 10);
    }

    public override void _ExitTree()
    {
        ChatLogSettings.Changed -= RefreshActive;
    }

    /// <param name="activeTarget">Where this session's logs go, or null before a login.</param>
    /// <param name="importContext">What the import needs, or null before a login.</param>
    public void Initialize(Func<ChatLogTarget?> activeTarget, Func<ChatLogImportContext?> importContext)
    {
        _activeTarget = activeTarget;
        _importContext = importContext;

        AddChild(Heading(L10n.Tr("ui.preferences.chat_logs_heading")));
        AddChild(Hint(L10n.Tr("ui.preferences.chat_logs_intro")));

        AddChild(Heading(L10n.Tr("ui.preferences.chat_logs_location")));
        _location = new OptionButton();
        _location.AddItem(L10n.Tr("ui.preferences.chat_logs_loc_firestorm"), (int)ChatLogMode.Firestorm);
        _location.AddItem(L10n.Tr("ui.preferences.chat_logs_loc_slng"), (int)ChatLogMode.Slng);
        _location.AddItem(L10n.Tr("ui.preferences.chat_logs_loc_custom"), (int)ChatLogMode.Custom);
        _location.Selected = _location.GetItemIndex((int)ChatLogSettings.EffectiveMode);
        _location.ItemSelected += OnLocationSelected;
        AddChild(_location);

        _custom = new LineEdit
        {
            Text = ChatLogSettings.CustomFolder,
            PlaceholderText = L10n.Tr("ui.preferences.chat_logs_custom_placeholder"),
            Editable = ChatLogSettings.EffectiveMode == ChatLogMode.Custom,
        };
        _custom.TextSubmitted += text => ChatLogSettings.SetCustomFolder(text);
        _custom.FocusExited += () => ChatLogSettings.SetCustomFolder(_custom.Text);
        _custom.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        // Firestorm has a "Choose..." button here; typing a path is not an acceptable way to pick a folder.
        var customRow = new HBoxContainer();
        customRow.AddThemeConstantOverride("separation", 6);
        customRow.AddChild(_custom);
        _browse = new Button { Text = L10n.Tr("ui.preferences.chat_logs_browse") };
        _browse.Visible = DisplayServer.HasFeature(DisplayServer.Feature.NativeDialogFile);
        _browse.Pressed += OnBrowsePressed;
        customRow.AddChild(_browse);
        AddChild(customRow);

        AddChild(Heading(L10n.Tr("ui.preferences.chat_logs_im_names")));
        _names = new OptionButton();
        _names.AddItem(L10n.Tr("ui.preferences.chat_logs_im_auto"), (int)ImNamesChoice.Auto);
        _names.AddItem(L10n.Tr("ui.preferences.chat_logs_im_legacy"), (int)ImNamesChoice.Legacy);
        _names.AddItem(L10n.Tr("ui.preferences.chat_logs_im_account"), (int)ImNamesChoice.Account);
        _names.Selected = _names.GetItemIndex((int)ChatLogSettings.ImNames);
        _names.ItemSelected += index => ChatLogSettings.SetImNames((ImNamesChoice)_names.GetItemId((int)index));
        AddChild(_names);

        _active = Hint("");
        AddChild(_active);
        AddChild(Hint(L10n.Tr("ui.preferences.chat_logs_apply_hint")));

        AddChild(new HSeparator());

        AddChild(Heading(L10n.Tr("ui.preferences.chat_logs_import_heading")));
        AddChild(Hint(L10n.Tr("ui.preferences.chat_logs_import_hint")));
        _importButton = new Button { Text = L10n.Tr("ui.preferences.chat_logs_import") };
        _importButton.Pressed += OnImportPressed;
        AddChild(_importButton);
        _importStatus = Hint("");
        AddChild(_importStatus);

        AddChild(new HSeparator());
        AddChild(Hint(L10n.Tr("ui.preferences.chat_logs_one_viewer")));

        ChatLogSettings.Changed += RefreshActive;
        VisibilityChanged += () => { if (IsVisibleInTree()) RefreshActive(); };
        RefreshActive();
    }

    // The OS folder picker. The chosen folder is the BASE: the account's own folder (first_last, with a
    // grid suffix off Second Life) is created inside it, exactly like Firestorm's "Logs and transcripts
    // location". Picking one switches the location to "chosen folder" -- choosing a folder and then
    // having it ignored would be the one wrong outcome.
    private void OnBrowsePressed()
    {
        string start = _custom.Text.Length > 0 && Directory.Exists(_custom.Text)
            ? _custom.Text
            : SLNG.Core.Services.ChatLogger.DefaultFirestormProfileDirectory();
        DisplayServer.FileDialogShow(
            L10n.Tr("ui.preferences.chat_logs_browse_title"), start, "", false,
            DisplayServer.FileDialogMode.OpenDir, Array.Empty<string>(),
            Callable.From((bool ok, string[] paths, long filter) =>
            {
                if (!ok || paths.Length == 0 || string.IsNullOrWhiteSpace(paths[0])) return;
                _custom.Text = paths[0];
                ChatLogSettings.SetCustomFolder(paths[0]);
                _location.Selected = _location.GetItemIndex((int)ChatLogMode.Custom);
                _custom.Editable = true;
                ChatLogSettings.SetMode(ChatLogMode.Custom);
            }));
    }

    private void OnLocationSelected(long index)
    {
        var mode = (ChatLogMode)_location.GetItemId((int)index);
        _custom.Editable = mode == ChatLogMode.Custom;
        ChatLogSettings.SetMode(mode);
    }

    private void RefreshActive()
    {
        if (_active == null) return;

        var target = _activeTarget();
        if (target != null)
        {
            _active.Text = L10n.TrFormat("ui.preferences.chat_logs_active", target.Directory, target.Why);
            return;
        }

        string baseFolder = ChatLogSettings.EffectiveMode switch
        {
            ChatLogMode.Slng => SLNG.Core.Services.ChatLogger.DefaultLogDirectory(),
            ChatLogMode.Custom when ChatLogSettings.CustomFolder.Length > 0 => ChatLogSettings.CustomFolder,
            _ => SLNG.Core.Services.ChatLogger.DefaultFirestormProfileDirectory(),
        };
        _active.Text = L10n.TrFormat("ui.preferences.chat_logs_active_none", baseFolder);
    }

    // ---- import ----------------------------------------------------------------------------

    private void OnImportPressed()
    {
        var ctx = _importContext();
        if (ctx == null)
        {
            _importStatus.Text = L10n.Tr("ui.preferences.chat_logs_import_needs_login");
            return;
        }

        _importButton.Disabled = true;
        _importStatus.Text = L10n.Tr("ui.preferences.chat_logs_import_working");

        // Reading the old files and the destinations can be many megabytes: off the main thread.
        Task.Run(() =>
        {
            ChatLogImportPlan plan;
            try { plan = ChatLogImporter.Plan(ctx.Sources, ctx.Directory, ctx.Naming, DateTime.UtcNow); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _error = ex.Message;
                CallDeferred(nameof(OnPlanFailed));
                return;
            }
            _plan = plan;
            _planContext = ctx;
            CallDeferred(nameof(OnPlanReady));
        });
    }

    private void OnPlanFailed()
    {
        _importButton.Disabled = false;
        _importStatus.Text = L10n.TrFormat("ui.preferences.chat_logs_import_failed", _error ?? "");
    }

    private void OnPlanReady()
    {
        var plan = _plan;
        var ctx = _planContext;
        _plan = null;
        _planContext = null;
        if (plan == null || ctx == null) { _importButton.Disabled = false; return; }

        string summary = L10n.TrFormat("ui.preferences.chat_logs_import_summary",
            plan.Files.Count, plan.MessagesToImport, plan.MessagesAlreadyThere, plan.FilesSkipped);

        if (plan.Appends.Count == 0)
        {
            _importButton.Disabled = false;
            _importStatus.Text = summary + " " + L10n.Tr("ui.preferences.chat_logs_import_nothing");
            return;
        }

        var win = new ConfirmWindow();
        var host = GetTree()?.Root?.GetNodeOrNull<CanvasLayer>("Boot/HudLayer") ?? (Node?)GetParent() ?? this;
        host.AddChild(win);
        win.Initialize(
            L10n.Tr("ui.preferences.chat_logs_import_title"),
            summary + "\n\n" + L10n.TrFormat("ui.preferences.chat_logs_import_confirm", ctx.Directory),
            L10n.Tr("ui.preferences.chat_logs_import_ok"));
        win.Confirmed += () => ApplyImport(plan, ctx);
        win.Closed += () =>
        {
            // A cancel leaves the button usable; a confirm re-enables it when the import is done.
            if (!_applying) { _importButton.Disabled = false; _importStatus.Text = ""; }
        };
    }

    private bool _applying;

    private void ApplyImport(ChatLogImportPlan plan, ChatLogImportContext ctx)
    {
        _applying = true;
        _importStatus.Text = L10n.Tr("ui.preferences.chat_logs_import_working");

        _applied = plan;
        _appliedContext = ctx;
        _error = null;
        Task.Run(async () =>
        {
            try
            {
                foreach (var a in plan.Appends)
                    await ctx.Logger.AppendBlockAsync(ctx.Directory, a.DestinationPath, a.Block).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _error = ex.Message;
            }
            CallDeferred(nameof(OnApplyFinished));
        });
    }

    private void OnApplyFinished()
    {
        _applying = false;
        _importButton.Disabled = false;
        _importStatus.Text = _error != null || _applied == null || _appliedContext == null
            ? L10n.TrFormat("ui.preferences.chat_logs_import_failed", _error ?? "")
            : L10n.TrFormat("ui.preferences.chat_logs_import_done",
                _applied.MessagesToImport, _applied.Appends.Count, _appliedContext.Directory);
    }

    // ---- small helpers ---------------------------------------------------------------------

    private static Label Heading(string text)
    {
        var label = new Label { Text = text };
        label.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        return label;
    }

    private static Label Hint(string text)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeFontSizeOverride("font_size", 11);
        label.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        return label;
    }
}
