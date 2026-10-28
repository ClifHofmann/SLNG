using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SLNG.App.UI.Prefs;
using SLNG.Core.ChatLogs;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-41: the "Chat logs" section of the Chat &amp; IM category (FEAT-UI-79). For each saved account (the login
/// screen's list): the base folder its chat logs go into -- picked with the OS folder picker, or SLNG's default -- and the
/// folder actually in effect, including the account sub-folder named the way Firestorm names it. Under Advanced: how IM
/// files are named, and the explicit, user-triggered import of logs older SLNG builds wrote. Point an account at the
/// folder Firestorm uses for it and both viewers share one history; SLNG does not look at Firestorm to decide anything.
///
/// This is a Node, not a plain object: the import runs on a worker thread and comes back to the main thread through
/// <c>CallDeferred</c>, which needs a Godot object. <see cref="Build"/> puts it in the section so it is in the tree.
/// </summary>
public partial class ChatLogPreferencesPage : Node
{
    private static readonly ImLogNameStyle[] NameStyles = { ImLogNameStyle.Legacy, ImLogNameStyle.Account };

    private Func<IReadOnlyList<ChatLogAccount>> _accounts = () => Array.Empty<ChatLogAccount>();
    private Func<ChatLogTarget?> _activeTarget = () => null;
    private Func<string?> _activeKey = () => null;
    private Func<ChatLogImportContext?> _importContext = () => null;

    private OptionButton _account = null!;
    private Label _folder = null!;
    private Button _browse = null!;
    private Button _useDefault = null!;
    private Label _effective = null!;
    private PrefsRow _relogRow = null!;
    private Label _relogNotice = null!;
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

    public override void _ExitTree()
    {
        ChatLogSettings.Changed -= RefreshFolder;
    }

    /// <summary>Fills <paramref name="section"/> (Chat &amp; IM, "chat_logs"), which Boot has already put in its place. Call
    /// once, after the section is in the window's tree.</summary>
    /// <param name="accounts">The saved accounts, in the order the login screen lists them.</param>
    /// <param name="activeKey">Key of the account logged in right now, or null.</param>
    /// <param name="activeTarget">Where this session's logs go, or null before a login.</param>
    /// <param name="importContext">What the import needs, or null before a login.</param>
    public void Build(PrefsSection section, Func<IReadOnlyList<ChatLogAccount>> accounts, Func<string?> activeKey,
        Func<ChatLogTarget?> activeTarget, Func<ChatLogImportContext?> importContext)
    {
        _accounts = accounts;
        _activeKey = activeKey;
        _activeTarget = activeTarget;
        _importContext = importContext;
        section.AddChild(this);
        string prefix = $"{section.CategoryId}.{section.Id}";

        section.AddNote($"{prefix}.intro", "ui.preferences.chat_logs_intro");
        section.AddNote($"{prefix}.one_viewer", "ui.preferences.chat_logs_one_viewer");

        _account = new OptionButton { ClipText = true, FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _account.ItemSelected += index =>
        {
            _selectedKey = (index >= 0 && index < _rows.Count) ? _rows[(int)index].Key : null;
            RefreshFolder();
        };
        section.AddControl($"{prefix}.account", "ui.preferences.chat_logs_account", _account);

        // The folder this account's logs go into, with the buttons that change it. A change takes effect at the next login.
        // Firestorm has a "Choose..." button here; typing a path is not an acceptable way to pick a folder.
        _folder = PrefsStyle.MakeLabel("");
        var buttons = new HFlowContainer(); // wraps rather than pushing the page wider than a narrow window
        buttons.AddThemeConstantOverride("h_separation", PrefsStyle.ColumnGap);
        buttons.AddThemeConstantOverride("v_separation", PrefsStyle.RowSeparation);
        _browse = new Button { Text = L10n.Tr("ui.preferences.chat_logs_browse"), FocusMode = Control.FocusModeEnum.None };
        _browse.Visible = DisplayServer.HasFeature(DisplayServer.Feature.NativeDialogFile);
        _browse.Pressed += OnBrowsePressed;
        buttons.AddChild(_browse);
        _useDefault = new Button { Text = L10n.Tr("ui.preferences.chat_logs_use_default"), FocusMode = Control.FocusModeEnum.None };
        _useDefault.Pressed += () => { if (_selectedKey != null) ChatLogSettings.ClearFolder(_selectedKey); };
        buttons.AddChild(_useDefault);
        var folderSide = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        folderSide.AddThemeConstantOverride("separation", PrefsStyle.RowSeparation);
        folderSide.AddChild(_folder);
        folderSide.AddChild(buttons);
        section.AddControl($"{prefix}.folder", "ui.preferences.chat_logs_folder", folderSide)
            .WithApply(PrefsApply.NextLogin)
            .WithLiteralTexts(L10n.Tr("ui.preferences.chat_logs_browse"), L10n.Tr("ui.preferences.chat_logs_use_default"));

        _effective = PrefsStyle.MakeHint("");
        section.AddBlock($"{prefix}.in_effect", _effective);
        // Shown only while the logged-in account's folder differs from the one this login is writing to:
        // a change takes effect at the next login, and a person who changed it and kept chatting wondered
        // why nothing arrived in the new folder (reported 2026-10-03).
        _relogNotice = PrefsStyle.MakeNotice("");
        _relogRow = section.AddBlock($"{prefix}.relog_notice", _relogNotice);
        _relogRow.Visible = false;

        // Advanced: how IM files are named.
        var styleLabels = new[] { L10n.Tr("ui.preferences.chat_logs_im_legacy"), L10n.Tr("ui.preferences.chat_logs_im_account") };
        section.AddOption($"{prefix}.im_names", "ui.preferences.chat_logs_im_names", styleLabels,
            Math.Max(0, Array.IndexOf(NameStyles, ChatLogSettings.ImStyle)),
            index => ChatLogSettings.SetImStyle(NameStyles[index]),
            advanced: true,
            optionKeys: new[] { "ui.preferences.chat_logs_im_legacy", "ui.preferences.chat_logs_im_account" });

        // Advanced: the explicit import of what older SLNG builds wrote.
        var importBox = new VBoxContainer();
        importBox.AddThemeConstantOverride("separation", PrefsStyle.RowSeparation);
        importBox.AddChild(PrefsStyle.MakeHint(L10n.Tr("ui.preferences.chat_logs_import_hint")));
        _importButton = new Button
        {
            Text = L10n.Tr("ui.preferences.chat_logs_import"),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            FocusMode = Control.FocusModeEnum.None,
        };
        _importButton.Pressed += OnImportPressed;
        importBox.AddChild(_importButton);
        _importStatus = PrefsStyle.MakeHint("");
        importBox.AddChild(_importStatus);
        section.AddBlock($"{prefix}.import", importBox, "ui.preferences.chat_logs_import_heading", true,
            "ui.preferences.chat_logs_import_hint", "ui.preferences.chat_logs_import");

        ChatLogSettings.Changed += RefreshFolder;
        // The list is read again whenever the section is shown: a login saved since the last time is in it.
        section.VisibilityChanged += () => { if (section.IsVisibleInTree()) RefreshAccounts(); };
        RefreshAccounts();
    }

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
        UpdateRelogNotice(account, chosen ?? defaultBase);
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

    /// <summary>Warns when the account that is logged in now has a folder set that this login is not using
    /// yet. The running session keeps writing where it started; the new folder applies from the next login.</summary>
    private void UpdateRelogNotice(ChatLogAccount account, string newBase)
    {
        var running = _activeTarget();
        bool isRunningAccount = running != null && account.Key == _activeKey();
        if (!isRunningAccount)
        {
            _relogRow.Visible = false;
            return;
        }

        string accountFolder = Path.GetFileName(running!.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string wanted = Path.GetFullPath(Path.Combine(newBase, accountFolder));
        string current = Path.GetFullPath(running.Directory);
        bool differs = !string.Equals(wanted.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

        _relogRow.Visible = differs;
        if (differs) _relogNotice.Text = L10n.TrFormat("ui.preferences.chat_logs_relog_notice", wanted, current);
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
        var host = GetTree()?.Root?.GetNodeOrNull<CanvasLayer>("Boot/HudLayer") ?? GetParent() ?? this;
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
}
