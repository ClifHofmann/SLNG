using Godot;
using SLNG.App.UI.Prefs;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// The "Content rating" section of the General category (FEAT-UI-79; FEAT-SL-02): Second Life's content-rating preference
/// (General/Moderate/Adult), the equivalent of the reference viewer's Preferences → General maturity checkboxes. Nothing
/// here is about age -- it is the grid's own rating system, gating which regions and content the account is shown.
///
/// Session-bound like <see cref="InventoryPanel"/>: constructed once at boot and built into its section (see
/// Boot.SetupHud), then rebound to the current <see cref="GridSession"/> after every successful login via
/// <see cref="BindSession"/> (mirrors <c>_inventoryPanel?.Initialize(_session)</c> in Boot.OnLoginPressed) -- the account's
/// own verified ceiling is only known once a login response has actually arrived.
///
/// OpenSim exposes none of this: there is no <c>UpdateAgentInformation</c> capability, so
/// <see cref="GridSession.SupportsMaturityPreference"/> is false there and the section shows an explanatory note instead of
/// a dropdown that would silently do nothing.
///
/// <see cref="Refresh"/> is called again every time Preferences opens (Boot's <c>OnOpenPreferences</c>), not just from
/// <see cref="BindSession"/> -- see its own doc comment for why: measured live on Aditi, this page said "grid doesn't
/// support it" for an entire session because <see cref="GridSession.SupportsMaturityPreference"/> read false at the one
/// moment it was checked (right after login, before the region's capability seed had actually resolved) and nothing asked
/// again once it settled.
///
/// This is a Node, not a plain object: <see cref="GridSession"/> raises its event on whatever thread the triggering call ran
/// on, and the way back to the main thread is <c>CallDeferred</c> on a Godot object. <see cref="Build"/> puts it in the
/// section so it is in the tree.
/// </summary>
public partial class MaturityPreferencesPage : Node
{
    private static readonly MaturityLevel[] Levels =
        { MaturityLevel.General, MaturityLevel.Moderate, MaturityLevel.Adult };

    private static readonly string[] LevelKeys =
    {
        "ui.preferences.maturity_general",
        "ui.preferences.maturity_moderate",
        "ui.preferences.maturity_adult",
    };

    private GridSession? _session;
    private PrefsRow _dropdownRow = null!;
    private OptionButton _dropdown = null!;
    private PrefsRow _unsupportedRow = null!;
    private Label _statusLabel = null!;

    /// <summary>True while <see cref="Refresh"/> is repopulating the dropdown, so its own
    /// <c>ItemSelected</c> handler does not read the change as a user action and re-POST it.</summary>
    private bool _applyingRefresh;

    /// <summary>Fills <paramref name="section"/> (General, "content_rating"), which Boot has already put in its place. Call
    /// once -- before any session exists, so the dropdown starts hidden until <see cref="BindSession"/> supplies real
    /// data.</summary>
    public void Build(PrefsSection section)
    {
        section.AddChild(this);
        string prefix = $"{section.CategoryId}.{section.Id}";

        section.AddNote($"{prefix}.blurb", "ui.preferences.maturity_blurb");

        var labels = new string[Levels.Length];
        for (int i = 0; i < Levels.Length; i++) labels[i] = LabelFor(Levels[i]);
        _dropdownRow = section.AddOption($"{prefix}.rating", "ui.preferences.maturity_heading", labels, 0,
            OnItemSelected, optionKeys: LevelKeys);
        _dropdown = _dropdownRow.ControlAs<OptionButton>()!;
        _dropdownRow.Visible = false;

        _unsupportedRow = section.AddNote($"{prefix}.unsupported", "ui.preferences.maturity_unsupported");
        _unsupportedRow.Visible = false;

        _statusLabel = PrefsStyle.MakeHint("");
        section.AddBlock($"{prefix}.status", _statusLabel);
    }

    /// <summary>Rebinds to the current session -- call after every successful login (see class
    /// doc). Also subscribes to <see cref="GridSession.MaturityPreferenceChanged"/> so a
    /// server-side clamp (an unverified account asking for Adult, say) shows up here even though
    /// this page did not initiate the change.</summary>
    public void BindSession(GridSession session)
    {
        if (_session != null)
            _session.MaturityPreferenceChanged -= OnMaturityPreferenceChanged;

        _session = session;
        _session.MaturityPreferenceChanged += OnMaturityPreferenceChanged;
        _statusLabel.Text = "";
        Refresh();
    }

    private void OnMaturityPreferenceChanged(object? sender, MaturityLevel level)
        // GridSession raises this off whatever thread the triggering call ran on (a login
        // continuation, or the capability POST's own continuation) -- never assume main thread.
        => CallDeferred(nameof(Refresh));

    /// <summary>Re-reads <see cref="_session"/> and rebuilds the dropdown/status text. Public and
    /// called again every time Preferences opens (see Boot's OnOpenPreferences) because
    /// <see cref="GridSession.SupportsMaturityPreference"/> can be a false negative right after
    /// login: capabilities are not necessarily resolvable yet at the point <see cref="BindSession"/>
    /// runs (LibreMetaverse's own cap seed fetch is still in flight -- see GridSession's own
    /// EventQueueRunning-vs-SimConnected comment for the exact same race on the environment
    /// capabilities), and nothing here was watching for it to become true afterward. Measured
    /// live on Aditi: this page said "grid doesn't support it" for an entire session because it
    /// was opened once, early, and never asked again.</summary>
    public void Refresh()
    {
        if (_session == null) return;

        bool supported = _session.SupportsMaturityPreference;
        _dropdownRow.Visible = supported;
        _unsupportedRow.Visible = !supported;
        if (!supported) return;

        var max = _session.AccountMaturityMax;
        var current = _session.PreferredMaturity;

        _applyingRefresh = true;
        for (int i = 0; i < Levels.Length; i++)
        {
            // Disabled rather than removed: seeing "Adult" greyed out, with the account-max note
            // below, tells an unverified user WHY it isn't offered instead of it just not
            // existing -- SL's own Preferences dialog does the same (option present, disabled).
            _dropdown.SetItemDisabled(i, Levels[i] > max);
            if (Levels[i] == current) _dropdown.Select(i);
        }
        _applyingRefresh = false;

        _statusLabel.Text = L10n.TrFormat("ui.preferences.maturity_account_max", LabelFor(max));
    }

    private void OnItemSelected(int index)
    {
        if (_applyingRefresh || _session == null) return;
        _ = ApplyAsync(Levels[index]);
    }

    private async System.Threading.Tasks.Task ApplyAsync(MaturityLevel requested)
    {
        if (_session == null) return;

        var (success, actual, error) = await _session.SetPreferredMaturityAsync(requested);

        // SetPreferredMaturityAsync already raised MaturityPreferenceChanged on success, which
        // re-enters here via OnMaturityPreferenceChanged -> Refresh; this call is what makes
        // Refresh happen even on a FAILURE, where no event fires.
        Refresh();

        _statusLabel.Text = success
            ? L10n.TrFormat("ui.preferences.maturity_set_to", LabelFor(actual))
            : L10n.TrFormat("ui.preferences.maturity_set_failed", error ?? "?");
    }

    private static string LabelFor(MaturityLevel level) => level switch
    {
        MaturityLevel.Adult => L10n.Tr("ui.preferences.maturity_adult"),
        MaturityLevel.Moderate => L10n.Tr("ui.preferences.maturity_moderate"),
        _ => L10n.Tr("ui.preferences.maturity_general"),
    };
}
