using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SLNG.Core.ChatLogs;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-41: "Chat logs" tab of Preferences. For each saved account (the login screen's list): the
/// base folder its chat logs go into -- picked with the OS folder picker, or SLNG's default -- and the
/// folder actually in effect, including the account sub-folder named the way Firestorm names it. Also
/// how IM files are named, and the explicit, user-triggered import of logs older SLNG builds wrote.
/// Point an account at the folder Firestorm uses for it and both viewers share one history; SLNG does
/// not look at Firestorm to decide anything.
/// </summary>
public partial class ChatLogPreferencesPage : VBoxContainer
{
    private Func<IReadOnlyList<ChatLogAccount>> _accounts = () => Array.Empty<ChatLogAccount>();
    private Func<ChatLogTarget?> _activeTarget = () => null;
    private Func<string?> _activeKey = () => null;
    private Func<ChatLogImportContext?> _importContext = () => null;

    private OptionButton _account = null!;
    private Label _folder = null!;
    private Button _browse = null!;
    private Button _useDefault = null!;
    private Label _effective = null!;
    private OptionButton _names = null!;
    private Button _importButton = null!;
    private Label _importStatus = null!;

    private List<ChatLogAccount> _rows = new();
    private string? _selectedKey;

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
        ChatLogSettings.Changed -= RefreshFolder;
    }

    /// <param name="accounts">The saved accounts, in the order the login screen lists them.</param>
    /// <param name="activeKey">Key of the account logged in right now, or null.</param>
    /// <param name="activeTarget">Where this session's logs go, or null before a login.</param>
    /// <param name="importContext">What the import needs, or null before a login.</param>
    public void Initialize(Func<IReadOnlyList<ChatLogAccount>> accounts, Func<string?> activeKey,
        Func<ChatLogTarget?> activeTarget, Func<ChatLogImportContext?> importContext)
    {
        _accounts = accounts;
        _activeKey = activeKey;
        _activeTarget = activeTarget;
        _importContext = importContext;

        AddChild(Heading(L10n.Tr("ui.preferences.chat_logs_heading")));
        AddChild(Hint(L10n.Tr("ui.preferences.chat_logs_intro")));

        AddChild(Heading(L10n.Tr("ui.preferences.chat_logs_account")));
        _account = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _account.ItemSelected += index =>
        {
            _selectedKey = (index >= 0 && index < _rows.Count) ? _rows[(int)index].Key : null;
            RefreshFolder();
        };
        AddChild(_account);

        AddChild(Heading(L10n.Tr("ui.preferences.chat_logs_folder")));
        _folder = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        AddChild(_folder);

        // Firestorm has a "Choose..." button here; typing a path is not an acceptable way to pick a folder.
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        _browse = new Button { Text = L10n.Tr("ui.preferences.chat_logs_browse") };
        _browse.Visible = DisplayServer.HasFeature(DisplayServer.Feature.NativeDialogFile);
        _browse.Pressed += OnBrowsePressed;
        row.AddChild(_browse);
        _useDefault = new Button { Text = L10n.Tr("ui.preferences.chat_logs_use_default") };
        _useDefault.Pressed += () => { if (_selectedKey != null) ChatLogSettings.ClearFolder(_selectedKey); };
        row.AddChild(_useDefault);
        AddChild(row);

        _effective = Hint("");
        AddChild(_effective);
        AddChild(Hint(L10n.Tr("ui.preferences.chat_logs_apply_hint")));

        AddChild(Heading(L10n.Tr("ui.preferences.chat_logs_im_names")));
        _names = new OptionButton();
        _names.AddItem(L10n.Tr("ui.preferences.chat_logs_im_legacy"), (int)ImLogNameStyle.Legacy);
        _names.AddItem(L10n.Tr("ui.preferences.chat_logs_im_account"), (int)ImLogNameStyle.Account);
        _names.Selected = _names.GetItemIndex((int)ChatLogSettings.ImStyle);
        _names.ItemSelected += index => ChatLogSettings.SetImStyle((ImLogNameStyle)_names.GetItemId((int)index));
        AddChild(_names);

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

        ChatLogSettings.Changed += RefreshFolder;
        VisibilityChanged += () => { if (IsVisibleInTree()) RefreshAccounts(); };
        RefreshAccounts();
    }

    // The list is read again whenever the tab is shown: a login saved since the last time is in it.
    private void RefreshAccounts()
    {
        _rows = _accounts().ToList();
        _account.Clear();
        foreach (var a in _rows) _account.AddItem(a.Label);

        int index = _selectedKey == null ? -1 : _rows.FindIndex(a => a.Key == _selectedKey);
        if (index < 0) index = _rows.FindIndex(a => a.Key == _activeKey());
        if (index < 0 && _rows.Count > 0) index = 0;
        if (index >= 0) _account.Selected = index;
        _selectedKey = index >= 0 ? _rows[index].Key : null;
        RefreshFolder();
    }

    private void RefreshFolder()
    {
        if (_folder == null) return;

        var account = _rows.Find(a => a.Key == _selectedKey);
        _browse.Disabled = _useDefault.Disabled = account == null;
        if (account == null)
        {
            _folder.Text = L10n.Tr("ui.preferences.chat_logs_no_accounts");
            _effective.Text = "";
            return;
        }

        string? chosen = ChatLogSettings.FolderFor(account.Key);
        string defaultBase = SLNG.Core.Services.ChatLogger.DefaultLogDirectory();
        _useDefault.Disabled = chosen == null;
        _folder.Text = chosen ?? L10n.TrFormat("ui.preferences.chat_logs_default_folder", defaultBase);

        // The folder in effect: the active session's own when this is the logged-in account (its grid
        // name is known), else the base plus the account folder. A grid whose name is only learned by
        // asking it at login shows a placeholder -- opening Preferences does not contact a grid.
        var active = _activeTarget();
        if (active != null && account.Key == _activeKey())
        {
            _effective.Text = L10n.TrFormat("ui.preferences.chat_logs_in_effect", active.Directory);
            return;
        }

        string baseDir = chosen ?? defaultBase;
        string? label = GridLabels.BuiltInLabel(account.GridUri);
        string folderName = label != null
            ? FirestormLogLayout.AccountFolderName(account.FirstName, account.LastName, label)
            : FirestormLogLayout.AccountFolderName(account.FirstName, account.LastName, null)
              + L10n.Tr("ui.preferences.chat_logs_grid_name_placeholder");
        _effective.Text = L10n.TrFormat("ui.preferences.chat_logs_in_effect", Path.Combine(baseDir, folderName));
    }

    // The OS folder picker. The chosen folder is the BASE: the account's own folder (first_last, with a
    // grid suffix off Second Life) is created inside it, exactly like Firestorm's "Logs and transcripts
    // location" -- so choosing the folder Firestorm uses for this account makes both viewers share it.
    private void OnBrowsePressed()
    {
        if (_selectedKey == null) return;
        string key = _selectedKey;
        string? chosen = ChatLogSettings.FolderFor(key);
        string start = chosen != null && Directory.Exists(chosen) ? chosen : SLNG.Core.Services.ChatLogger.DefaultLogDirectory();
        DisplayServer.FileDialogShow(
            L10n.Tr("ui.preferences.chat_logs_browse_title"), start, "", false,
            DisplayServer.FileDialogMode.OpenDir, Array.Empty<string>(),
            Callable.From((bool ok, string[] paths, long filter) =>
            {
                if (!ok || paths.Length == 0 || string.IsNullOrWhiteSpace(paths[0])) return;
                ChatLogSettings.SetFolder(key, paths[0]);
            }));
    }

    // ---- import ----------------------------------------------------------------------------

    // The import writes into the folder of the account that is LOGGED IN: the older logs are looked
    // up by that account and grid (the per-grid folder of v0.26.13), and the history is filed under it.
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
