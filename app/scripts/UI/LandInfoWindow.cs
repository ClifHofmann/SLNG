using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Godot;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-LAND-01: "About Land" -- what the sim says about the parcel the agent stands on, in the
/// tabbed shape of the reference viewer's floater. Today it has General, Options, Media and Sound and is
/// read-only; Covenant, Objects and Access follow in FEAT-LAND-03..05, each as one more
/// <see cref="ILandInfoTab"/> and one more <see cref="AddTab"/> call in <see cref="_Ready"/>.
///
/// <para><b>It follows the agent.</b> The data layer raises <c>ParcelInfoReceived</c> for the answer to
/// the request made on open, again when the traffic figure arrives, and whenever the agent walks into
/// another parcel; this window just shows the latest one. A refused or unanswered request, and a
/// session that is not connected, show one line saying the information is not available instead of an
/// empty form.</para>
///
/// <para><b>Threading.</b> The session's events fire on LibreMetaverse network threads. The handlers
/// only queue a closure; <see cref="_Process"/> runs it on the main thread. (Not
/// <c>Callable.From(..).CallDeferred()</c>: that is not safe off the main thread.) The handlers are
/// removed when the window leaves the tree, so a closed window is not kept alive by the session.</para>
/// </summary>
public partial class LandInfoWindow : SLNGWindow
{
    /// <summary>Fired once this window has closed and freed itself, so the owner can drop it.</summary>
    public event Action? Closed;

    private GridSession? _session;
    private readonly ConcurrentQueue<Action> _inbox = new();

    // Display Names that arrived, written from network threads.
    private readonly ConcurrentDictionary<Guid, string> _displayNames = new();
    // Ids already asked for, so a name that is slow does not turn every redraw into a new request.
    private readonly HashSet<Guid> _asked = new();
    private readonly HashSet<Guid> _displayAsked = new();

    private readonly List<ILandInfoTab> _tabs = new();
    private TabContainer _tabContainer = null!;
    private Label _status = null!;
    private LandGeneralTab _general = null!;
    private LandOptionsTab _options = null!;
    private LandMediaTab _media = null!;
    private LandSoundTab _sound = null!;

    // True once a parcel has arrived since the last request, so a late timeout cannot blank a parcel
    // that a push already put on screen.
    private bool _freshSinceRequest;
    private bool _closed;
    private bool _subscribed;

    public override void _Ready()
    {
        base._Ready();

        PersistId = "land_info";
        Title = L10n.Tr("ui.land.title");
        CustomMinimumSize = new Vector2(520, 480);
        Size = new Vector2(540, 560);
        Position = new Vector2(320, 110);
        OnCloseRequested = Close;

        // The inset is SLNGWindow's; no wrapper margin here.
        var column = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        ContentContainer.AddChild(column);

        _status = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _status.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        column.AddChild(_status);

        _tabContainer = new TabContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            Visible = false,
        };
        column.AddChild(_tabContainer);

        _general = new LandGeneralTab();
        _general.Initialize(LookupName);
        AddTab(_general);
        _options = new LandOptionsTab();
        AddTab(_options);
        _media = new LandMediaTab();
        AddTab(_media);
        _sound = new LandSoundTab();
        AddTab(_sound);

        ShowStatus(L10n.Tr("ui.land.loading_parcel"));
    }

    /// <summary>Binds the window to a session and asks it for the parcel under the agent. Call once,
    /// right after adding the window to the tree. A null or not connected session is shown as
    /// "not available".</summary>
    public void Initialize(GridSession? session)
    {
        _session = session;
        if (session == null)
        {
            ShowStatus(L10n.Tr("ui.land.unavailable"));
            return;
        }

        session.ParcelInfoReceived += OnParcelInfoReceived;
        session.ParcelInfoFailed += OnParcelInfoFailed;
        session.NameResolved += OnNameResolved;
        session.DisplayNameResolved += OnDisplayNameResolved;
        _subscribed = true;

        // What the session already knows is on screen at once; the request below refreshes it.
        if (session.LastParcelInfo is { } last) ShowParcel(last);
        RequestRefresh();
    }

    /// <summary>Asks again for the parcel under the agent (the owner calls this when the window is
    /// opened a second time). The current display stays until the answer arrives or the request fails.</summary>
    public void RequestRefresh()
    {
        _freshSinceRequest = false;
        if (_session == null || !_session.RequestParcelInfoHere())
            ShowStatus(L10n.Tr("ui.land.unavailable"));
    }

    /// <summary>Closes and frees the window. Idempotent.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        Unsubscribe();
        Closed?.Invoke();
        QueueFree();
    }

    public override void _ExitTree()
    {
        Unsubscribe();
        base._ExitTree();
    }

    public override void _Process(double delta)
    {
        while (_inbox.TryDequeue(out var work)) work();
    }

    // --- tabs ------------------------------------------------------------------------------------

    private void AddTab<T>(T tab) where T : Control, ILandInfoTab
    {
        _tabContainer.AddChild(tab);
        _tabContainer.SetTabTitle(_tabContainer.GetTabCount() - 1, tab.TabTitle);
        _tabs.Add(tab);
    }

    // --- state -----------------------------------------------------------------------------------

    /// <summary>Shows a parcel on every tab and drops the status line. Main thread.</summary>
    internal void ShowParcel(ParcelInfo parcel)
    {
        _freshSinceRequest = true;
        _status.Visible = false;
        _tabContainer.Visible = true;
        foreach (var tab in _tabs) tab.ShowParcel(parcel);
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.Visible = true;
        _tabContainer.Visible = false;
    }

    /// <summary>The status line, or null while the tabs are showing. Selftest only.</summary>
    internal string? StatusText => _status.Visible ? _status.Text : null;

    internal LandGeneralTab General => _general;

    internal LandOptionsTab Options => _options;

    internal LandMediaTab Media => _media;

    internal LandSoundTab Sound => _sound;

    /// <summary>The tab titles in order. Selftest only.</summary>
    internal IEnumerable<string> TabTitles => _tabs.ConvertAll(t => t.TabTitle);

    // --- network-thread handlers: queue, never touch a Control -------------------------------------

    internal void OnParcelInfoReceived(object? sender, ParcelInfo info) =>
        _inbox.Enqueue(() => { if (!_closed) ShowParcel(info); });

    internal void OnParcelInfoFailed(object? sender, ParcelInfoFailure failure) =>
        _inbox.Enqueue(() =>
        {
            if (!_closed && !_freshSinceRequest) ShowStatus(L10n.Tr("ui.land.unavailable"));
        });

    private void OnNameResolved(object? sender, NameResolvedEvent e) => QueueNameRefresh();

    private void OnDisplayNameResolved(object? sender, NameResolvedEvent e)
    {
        if (!string.IsNullOrEmpty(e.Name)) _displayNames[e.Id] = e.Name;
        QueueNameRefresh();
    }

    private void QueueNameRefresh() =>
        _inbox.Enqueue(() =>
        {
            if (_closed) return;
            foreach (var tab in _tabs) tab.RefreshNames();
        });

    private void Unsubscribe()
    {
        if (!_subscribed || _session == null) return;
        _subscribed = false;
        _session.ParcelInfoReceived -= OnParcelInfoReceived;
        _session.ParcelInfoFailed -= OnParcelInfoFailed;
        _session.NameResolved -= OnNameResolved;
        _session.DisplayNameResolved -= OnDisplayNameResolved;
    }

    // --- names -----------------------------------------------------------------------------------

    /// <summary>The name for an owner / group / buyer id, or null while it is still loading. Asks the
    /// session once per id; the answer arrives as <c>NameResolved</c> and redraws the tabs.</summary>
    private string? LookupName(Guid id, bool isGroup)
    {
        if (_session == null || id == Guid.Empty) return null;

        string? legacy = _session.TryGetCachedName(id, out var cached) && cached.Length > 0 ? cached : null;
        if (legacy == null && isGroup)
        {
            foreach (var g in _session.GetGroups())
                if (g.Id == id && g.Name.Length > 0) { legacy = g.Name; break; }
        }

        if (legacy == null && _asked.Add(id))
        {
            if (isGroup) _session.RequestGroupName(id);
            else _session.RequestAvatarName(id);
        }

        if (isGroup) return legacy;

        // An avatar may have a Display Name too. The session raises it through DisplayNameResolved
        // -- but only for ids it has not already asked about, so the legacy name alone is a normal outcome.
        if (_displayAsked.Add(id)) _session.RequestDisplayName(id);
        if (_displayNames.TryGetValue(id, out var display) && display != legacy)
            return legacy == null ? display : $"{display} ({legacy})";
        return legacy;
    }
}
