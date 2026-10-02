using System;
using System.Collections.Concurrent;
using Godot;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-LAND-05: the read-only estate covenant -- estate name, estate owner, the text, and when it was
/// last changed. A SHARED view: the Land-Info "Covenant" tab (<see cref="LandCovenantTab"/>) and the
/// Region/Estate window's covenant tab (FEAT-LAND-06) both embed this same control. A covenant belongs
/// to the estate, so the same record serves both.
///
/// <para><b>Using it.</b> Add it to a container, call <see cref="Initialize"/> with the name lookup and
/// <see cref="Bind"/> with the session, then <see cref="Follow"/> with the region the host is about. The
/// view asks the session for the covenant once per region (and again at most once a minute, as the
/// viewer's panel does), listens for the answer, and draws it. A host without a session can drive it
/// with <see cref="ShowCovenant"/> instead.</para>
///
/// <para><b>States.</b> Loading (no answer yet), the covenant (header and text), "no covenant provided",
/// the text failed to load (the header still shows), and the request failed (nothing to show). A failure
/// is drawn as a failure, never as an empty or absent covenant.</para>
///
/// <para><b>Threading.</b> The session's events fire on network threads. The handlers only queue a
/// closure; <see cref="_Process"/> runs it on the main thread. (Not <c>Callable.From(..).CallDeferred()</c>:
/// that is not safe off the main thread.) The handlers are removed when the view leaves the tree.</para>
/// </summary>
public partial class CovenantView : VBoxContainer
{
    /// <summary>How long before a region already followed is asked again (the viewer's
    /// <c>COVENANT_REFRESH_TIME_SEC</c>, <c>llfloaterland.cpp</c>:88).</summary>
    internal const ulong RefreshMs = 60_000;

    private readonly ConcurrentQueue<Action> _inbox = new();
    private LandInfoFormat.NameLookup _lookup = static (_, _) => null;
    private GridSession? _session;
    private bool _subscribed;

    private ulong _followed;
    private ulong _askedAtMs;
    private CovenantInfo? _current;

    private Label _estate = null!;
    private Label _owner = null!;
    private Label _modified = null!;
    private Label _notice = null!;
    private TextEdit _text = null!;
    private LandInfoFormat.CovenantBodyKind _bodyKind;

    /// <summary>Sets how the estate owner's id becomes a name. May be called again to replace it.</summary>
    internal void Initialize(LandInfoFormat.NameLookup lookup) => _lookup = lookup;

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 10);

        var grid = LandTabPage.NewGrid(this);
        _estate = LandTabPage.ValueLabel();
        LandTabPage.AddRow(grid, L10n.Tr("ui.land.cov_estate"), _estate);
        _owner = LandTabPage.ValueLabel();
        LandTabPage.AddRow(grid, L10n.Tr("ui.land.row_owner"), _owner);

        // The body is a selectable text box for a covenant, a plain line for everything else, so a
        // notice can never be mistaken for covenant text.
        var body = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.CustomMinimumSize = new Vector2(0, 140);
        AddChild(body);

        _text = new TextEdit
        {
            Editable = false,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            Visible = false,
        };
        _text.SetAnchorsPreset(LayoutPreset.FullRect);
        body.AddChild(_text);

        _notice = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _notice.SetAnchorsPreset(LayoutPreset.FullRect);
        body.AddChild(_notice);

        _modified = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _modified.AddThemeFontSizeOverride("font_size", 12);
        _modified.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        AddChild(_modified);

        ShowLoading();
    }

    // --- wiring ------------------------------------------------------------------------------------

    /// <summary>Listens to the session's covenant events. Call once, after <see cref="Initialize"/>. A null
    /// session leaves the view to <see cref="ShowCovenant"/>.</summary>
    internal void Bind(GridSession? session)
    {
        Unsubscribe();
        _session = session;
        if (session == null) return;
        session.CovenantReceived += OnCovenantReceived;
        session.CovenantFailed += OnCovenantFailed;
        _subscribed = true;
    }

    /// <summary>The region the host is showing. A region not followed yet shows what the session already
    /// holds for it (or "loading") and asks for the covenant; the same region is asked again only after
    /// <see cref="RefreshMs"/>, so a stream of parcel changes inside one region costs nothing.</summary>
    internal void Follow(ulong regionHandle)
    {
        bool newRegion = regionHandle != _followed;
        ulong now = Time.GetTicksMsec();
        if (!newRegion && now - _askedAtMs < RefreshMs) return;

        if (newRegion)
        {
            _followed = regionHandle;
            _current = null;
            if (_session?.LastCovenant is { } known && known.RegionHandle == regionHandle) Render(known);
            else ShowLoading();
        }

        if (_session == null) return;
        _askedAtMs = now;
        if (!_session.RequestCovenant() && _current == null) ShowFailed();
    }

    public override void _Process(double delta)
    {
        while (_inbox.TryDequeue(out var work)) work();
    }

    public override void _ExitTree()
    {
        Unsubscribe();
        base._ExitTree();
    }

    private void Unsubscribe()
    {
        if (!_subscribed || _session == null) return;
        _subscribed = false;
        _session.CovenantReceived -= OnCovenantReceived;
        _session.CovenantFailed -= OnCovenantFailed;
    }

    // --- network-thread handlers: queue, never touch a Control ---------------------------------------

    internal void OnCovenantReceived(object? sender, CovenantInfo info) =>
        _inbox.Enqueue(() =>
        {
            // An answer for a region the host has since left is not what it is showing.
            if (_followed != 0 && info.RegionHandle != _followed) return;
            ShowCovenant(info);
        });

    internal void OnCovenantFailed(object? sender, CovenantFailure failure) =>
        _inbox.Enqueue(() =>
        {
            if (_followed != 0 && failure.RegionHandle != _followed) return;
            // A late timeout must not blank a covenant that a push already put on screen.
            if (_current == null) ShowFailed();
        });

    // --- state ---------------------------------------------------------------------------------------

    /// <summary>Shows a covenant. Main thread.</summary>
    public void ShowCovenant(CovenantInfo covenant)
    {
        _current = covenant;
        Render(covenant);
    }

    /// <summary>Redraws what depends on a name (the estate owner) once it has been looked up.</summary>
    internal void RefreshNames()
    {
        if (_current != null) _owner.Text = LandInfoFormat.CovenantOwnerText(_current, _lookup);
    }

    /// <summary>No answer yet.</summary>
    public void ShowLoading()
    {
        _current = null;
        _estate.Text = LandInfoFormat.Unknown;
        _owner.Text = LandInfoFormat.Unknown;
        _modified.Text = string.Empty;
        SetBody(LandInfoFormat.CovenantBodyLoading());
    }

    /// <summary>The request got no reply. Nothing is known, and the view says so.</summary>
    public void ShowFailed()
    {
        _current = null;
        _estate.Text = LandInfoFormat.Unknown;
        _owner.Text = LandInfoFormat.Unknown;
        _modified.Text = string.Empty;
        SetBody(LandInfoFormat.CovenantBodyFailed());
    }

    private void Render(CovenantInfo c)
    {
        _estate.Text = LandInfoFormat.CovenantEstateText(c);
        _owner.Text = LandInfoFormat.CovenantOwnerText(c, _lookup);
        _modified.Text = LandInfoFormat.CovenantModifiedText(c.TimestampUtc);
        SetBody(LandInfoFormat.CovenantBody(c));
    }

    private void SetBody((LandInfoFormat.CovenantBodyKind Kind, string Text) body)
    {
        _bodyKind = body.Kind;
        bool isText = body.Kind == LandInfoFormat.CovenantBodyKind.Text;
        _text.Visible = isText;
        _notice.Visible = !isText;

        if (isText)
        {
            // Not reassigned when unchanged: the same text again would throw the scroll position away.
            if (_text.Text != body.Text) _text.Text = body.Text;
            return;
        }

        _notice.Text = body.Text;
        _notice.AddThemeColorOverride(
            "font_color",
            body.Kind == LandInfoFormat.CovenantBodyKind.Problem ? ProblemText : UiTheme.SecondaryText);
    }

    private static readonly Color ProblemText = new(1f, 0.72f, 0.35f);

    // --- for the selftest ----------------------------------------------------------------------------

    internal string EstateText => _estate.Text;

    internal string OwnerText => _owner.Text;

    internal string ModifiedText => _modified.Text;

    /// <summary>What the body shows: the covenant text when it is text, otherwise the notice line.</summary>
    internal string BodyText => _bodyKind == LandInfoFormat.CovenantBodyKind.Text ? _text.Text : _notice.Text;

    internal LandInfoFormat.CovenantBodyKind BodyKind => _bodyKind;

    internal bool TextBoxVisible => _text.Visible;

    internal bool TextBoxEditable => _text.Editable;

    internal ulong Followed => _followed;
}
