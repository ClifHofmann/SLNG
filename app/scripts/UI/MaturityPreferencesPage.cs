using Godot;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// "Age settings" tab for PreferencesWindow: Second Life's content-rating preference
/// (General/Moderate/Adult), the equivalent of the reference viewer's Preferences → General
/// maturity checkboxes. Nothing here is about age -- it is the grid's own rating system, gating
/// which regions and content the account is shown.
///
/// Session-bound like <see cref="InventoryPanel"/>: constructed once at boot and added as a tab
/// (see Boot.SetupHud), then rebound to the current <see cref="GridSession"/> after every
/// successful login via <see cref="BindSession"/> (mirrors <c>_inventoryPanel?.Initialize(_session)</c>
/// in Boot.OnLoginPressed) -- the account's own verified ceiling is only known once a login
/// response has actually arrived.
///
/// OpenSim exposes none of this: there is no <c>UpdateAgentInformation</c> capability, so
/// <see cref="GridSession.SupportsMaturityPreference"/> is false there and this page shows an
/// explanatory message instead of a dropdown that would silently do nothing.
///
/// <see cref="Refresh"/> is called again every time Preferences opens (Boot's
/// <c>OnOpenPreferences</c>), not just from <see cref="BindSession"/> -- see its own doc comment
/// for why: measured live on Aditi, this page said "grid doesn't support it" for an entire
/// session because <see cref="GridSession.SupportsMaturityPreference"/> read false at the one
/// moment it was checked (right after login, before the region's capability seed had actually
/// resolved) and nothing asked again once it settled.
/// </summary>
public partial class MaturityPreferencesPage : VBoxContainer
{
    private static readonly MaturityLevel[] Levels =
        { MaturityLevel.General, MaturityLevel.Moderate, MaturityLevel.Adult };

    private GridSession? _session;
    private OptionButton _dropdown = null!;
    private Label _unsupportedLabel = null!;
    private Label _statusLabel = null!;

    /// <summary>True while <see cref="Refresh"/> is repopulating the dropdown, so its own
    /// <c>ItemSelected</c> handler does not read the change as a user action and re-POST it.</summary>
    private bool _applyingRefresh;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 10);
    }

    /// <summary>Builds the page. Call once, right after PreferencesWindow.AddTab (mirrors every
    /// other page's Initialize convention) -- before any session exists, so the dropdown starts
    /// empty/hidden until <see cref="BindSession"/> supplies real data.</summary>
    public void Initialize()
    {
        var heading = new Label { Text = L10n.Tr("ui.preferences.maturity_heading") };
        heading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        AddChild(heading);

        var blurb = new Label
        {
            Text = L10n.Tr("ui.preferences.maturity_blurb"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        AddChild(blurb);

        _dropdown = new OptionButton { Visible = false };
        foreach (var level in Levels)
            _dropdown.AddItem(LabelFor(level));
        _dropdown.ItemSelected += OnItemSelected;
        AddChild(_dropdown);

        _unsupportedLabel = new Label
        {
            Text = L10n.Tr("ui.preferences.maturity_unsupported"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Visible = false,
        };
        _unsupportedLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        AddChild(_unsupportedLabel);

        _statusLabel = new Label { Text = "" };
        _statusLabel.AddThemeFontSizeOverride("font_size", 11);
        _statusLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        AddChild(_statusLabel);
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
    /// called again every time Preferences opens (see Boot's OnOpenPreferences, alongside
    /// QualityPreferencesPage/DesignPreferencesPage's own Refresh calls there) because
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
        _dropdown.Visible = supported;
        _unsupportedLabel.Visible = !supported;
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

    private void OnItemSelected(long index)
    {
        if (_applyingRefresh || _session == null) return;
        _ = ApplyAsync(Levels[(int)index]);
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
