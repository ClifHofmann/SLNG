using Godot;
using System;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-SL-01 — the Terms-of-Service / critical-message gate the Third-Party Viewer Policy §1.f
/// requires: when a grid refuses a login with <c>reason: "tos"</c> (or <c>"critical"</c>), the
/// viewer must SHOW the text and obtain the user's acceptance before retrying with
/// <c>agree_to_tos</c> / <c>read_critical</c> set.
///
/// The whole point is that acceptance is the USER's. LibreMetaverse's <c>LoginParams</c> defaults
/// both flags to true, so a viewer that never handles this response accepts the grid's terms on
/// the user's behalf, sight unseen — that is the violation, and this window is the fix for it (see
/// <see cref="LoginCredentials.AgreeToTos"/>).
///
/// Modelled on the reference viewer's <c>LLFloaterTOS</c>: the grid's own message text, a link out
/// to the full agreement in a real browser (the viewer loads <c>secondlife.com/app/tos</c> in an
/// embedded browser we do not have), an "I agree" checkbox that gates Continue, and Cancel — which
/// abandons the login attempt rather than sending anything. Closing via the title bar is Cancel.
/// </summary>
public partial class TermsOfServiceWindow : SLNGWindow
{
    /// <summary>Second Life's own Terms of Service page, the "real_url" the reference viewer's
    /// floater_tos.xml navigates to. Offered as an external link, since the message a grid sends
    /// is frequently a short notice rather than the full agreement.</summary>
    public const string SecondLifeTermsUrl = "https://secondlife.com/app/tos/";

    /// <summary>Answered with true (accepted) or false (cancelled / dismissed) exactly once.
    /// The login flow awaits this rather than polling.</summary>
    public event Action<bool>? Answered;

    private VBoxContainer _contentVBox = null!;
    private CheckBox _agreeCheck = null!;
    private Button _continueButton = null!;
    private bool _answered;

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(620, 480);
        Size = CustomMinimumSize;

        OnCloseRequested = () => Answer(false);

        // Inset comes from SLNGWindow.ContentContainer now (FEAT-UI-26); this container is kept
        // only because the layout below hangs off it.
        var margin = new MarginContainer();
        ContentContainer.AddChild(margin);

        _contentVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _contentVBox.AddThemeConstantOverride("separation", 10);
        margin.AddChild(_contentVBox);
    }

    /// <summary>Fills the window from a failed login result. <paramref name="critical"/> selects
    /// the wording only — both cases are one dialog and one answer in the reference viewer too
    /// (<c>handleTOSResponse</c>, keyed by which flag to set).</summary>
    public void Initialize(string gridLoginUri, string message, bool critical)
    {
        Title = L10n.Tr(critical ? "ui.tos.title_critical" : "ui.tos.title");

        var heading = new Label
        {
            Text = L10n.Tr(critical ? "ui.tos.heading_critical" : "ui.tos.heading"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _contentVBox.AddChild(heading);

        var gridLabel = new Label { Text = gridLoginUri };
        gridLabel.AddThemeFontSizeOverride("font_size", 11);
        gridLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        _contentVBox.AddChild(gridLabel);

        // The grid's text verbatim, selectable and scrollable. Not summarised, not reformatted:
        // this is the thing the user is being asked to agree to.
        var body = new RichTextLabel
        {
            BbcodeEnabled = false,
            SelectionEnabled = true,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 220),
            Text = string.IsNullOrWhiteSpace(message) ? L10n.Tr("ui.tos.no_message") : message,
        };
        _contentVBox.AddChild(body);

        // A grid may send a short notice instead of the agreement itself, so always offer the way
        // out to the full text. The URL is either one found in the message or, on a Linden grid,
        // Second Life's own ToS page.
        string? url = FindUrl(message) ?? (IsSecondLifeGrid(gridLoginUri) ? SecondLifeTermsUrl : null);
        if (url != null)
        {
            var openButton = new Button { Text = L10n.TrFormat("ui.tos.open_in_browser", url) };
            openButton.Pressed += () => OS.ShellOpen(url);
            _contentVBox.AddChild(openButton);
        }

        _agreeCheck = new CheckBox
        {
            Text = L10n.Tr(critical ? "ui.tos.agree_critical" : "ui.tos.agree"),
        };
        _agreeCheck.Toggled += pressed => _continueButton.Disabled = !pressed;
        _contentVBox.AddChild(_agreeCheck);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 8);

        var cancelButton = new Button { Text = L10n.Tr("ui.tos.cancel"), CustomMinimumSize = new Vector2(110, 0) };
        cancelButton.Pressed += () => Answer(false);
        buttons.AddChild(cancelButton);

        // Disabled until the checkbox is ticked -- same gating as the reference viewer's
        // "Continue" button (floater_tos.xml ships it enabled="false" and updateAgree enables it).
        _continueButton = new Button
        {
            Text = L10n.Tr("ui.tos.continue"),
            CustomMinimumSize = new Vector2(110, 0),
            Disabled = true,
        };
        _continueButton.Pressed += () => Answer(true);
        buttons.AddChild(_continueButton);

        _contentVBox.AddChild(buttons);

        var viewportSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        Position = new Vector2(
            Mathf.Max(0, (viewportSize.X - Size.X) / 2),
            Mathf.Max(0, (viewportSize.Y - Size.Y) / 2));
    }

    private void Answer(bool accepted)
    {
        if (_answered) return;
        _answered = true;
        Answered?.Invoke(accepted);
        QueueFree();
    }

    /// <summary>First http(s) URL in the grid's message, if it sent one. Trailing punctuation is
    /// trimmed so a URL at the end of a sentence still opens.</summary>
    internal static string? FindUrl(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        int start = message.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        int secure = message.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        if (secure >= 0 && (start < 0 || secure < start)) start = secure;
        if (start < 0) return null;

        int end = start;
        while (end < message.Length && !char.IsWhiteSpace(message[end])) end++;
        return message[start..end].TrimEnd('.', ',', ')', ';', '"', '\'');
    }

    /// <summary>Whether this login URI belongs to a Linden Lab grid (Agni or Aditi), which is the
    /// only case where secondlife.com's ToS page is the right fallback link.</summary>
    internal static bool IsSecondLifeGrid(string? loginUri) =>
        loginUri != null && loginUri.Contains("lindenlab.com", StringComparison.OrdinalIgnoreCase);
}
