using Godot;
using System;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-SL-02 — asks for the one-time code of an account protected with multi-factor
/// authentication. Shown when a grid refuses a login with <c>reason: "mfa_challenge"</c>; the login
/// flow in <c>Boot</c> awaits the answer and retries with it.
///
/// Modelled on the reference viewer's <c>PromptMFATokenWithSave</c> notification
/// (<c>notifications.xml</c>): the grid's request, one text field, an optional "remember this
/// computer" box, Continue and Cancel. Differences on purpose: the code must be six digits before
/// Login enables (a typo never burns a single-use code), spaces are ignored (authenticator apps show
/// "123 456"), and a code the grid already refused is said so in red instead of the same prompt
/// reappearing unexplained.
///
/// The code is a secret: nothing in this class logs or prints it, and it travels only in the
/// <see cref="Submitted"/> event to the one caller that sends it to the grid.
/// </summary>
public partial class MfaPromptWindow : SLNGWindow
{
    /// <summary>The person entered a code and confirmed. Arguments: the code with whitespace removed,
    /// and whether "remember this computer" was ticked (always false when the box was not offered).
    /// Raised at most once, instead of <see cref="Cancelled"/>.</summary>
    public event Action<string, bool>? Submitted;

    /// <summary>The person cancelled, or closed the window. Raised at most once, instead of
    /// <see cref="Submitted"/>.</summary>
    public event Action? Cancelled;

    private static readonly Color ErrorColor = new(0.94f, 0.45f, 0.45f);

    private VBoxContainer _box = null!;
    private LineEdit _codeEdit = null!;
    private Label _errorLabel = null!;
    private CheckBox? _rememberCheck;
    private Button _loginButton = null!;
    private bool _answered;

    /// <summary>Test hook: whether the Login button is currently usable.</summary>
    internal bool LoginEnabled => _loginButton != null && !_loginButton.Disabled;

    /// <summary>Test hook: whether the "that code was refused" line is showing.</summary>
    internal bool ErrorVisible => _errorLabel != null && _errorLabel.Visible;

    /// <summary>Test hook: whether "remember this computer" is offered.</summary>
    internal bool RememberOffered => _rememberCheck != null;

    /// <summary>Test hook: the code field's own text, as typed.</summary>
    internal string CodeText => _codeEdit?.Text ?? "";

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(440, 0); // height comes from FitAndCentre
        Size = CustomMinimumSize;

        OnCloseRequested = Cancel;

        _box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _box.AddThemeConstantOverride("separation", 10);
        ContentContainer.AddChild(_box);
    }

    /// <param name="gridMessage">The text the grid sent with its challenge, shown under our own
    /// explanation when there is one. Not parsed or trusted for anything but display.</param>
    /// <param name="codeWasRefused">True when this prompt follows a code the grid did not accept (or
    /// one that went stale), so the person is told instead of guessing.</param>
    /// <param name="offerRemember">Whether to show "remember this computer". Only when the login is
    /// being saved: the reference viewer shows it only with "remember user" on
    /// (<c>lllogininstance.cpp:515</c>), because the grid's token is kept in the saved login.</param>
    /// <param name="extraNote">An optional line shown above the field, e.g. why a fresh code is needed
    /// again.</param>
    public void Initialize(string? gridMessage, bool codeWasRefused, bool offerRemember, string? extraNote = null)
    {
        Title = L10n.Tr("ui.mfa.title");

        _box.AddChild(MakeWrapLabel(L10n.Tr("ui.mfa.heading")));

        if (!string.IsNullOrWhiteSpace(extraNote))
            _box.AddChild(MakeWrapLabel(extraNote!.Trim()));

        // The grid's own words, secondary: ours is the one that is always translated and always there.
        if (!string.IsNullOrWhiteSpace(gridMessage))
        {
            var grid = MakeWrapLabel(gridMessage!.Trim());
            grid.AddThemeFontSizeOverride("font_size", 12);
            grid.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
            _box.AddChild(grid);
        }

        _errorLabel = MakeWrapLabel(L10n.Tr("ui.mfa.wrong_code"));
        _errorLabel.AddThemeColorOverride("font_color", ErrorColor);
        _errorLabel.Visible = codeWasRefused;
        _box.AddChild(_errorLabel);

        var codeLabel = new Label { Text = L10n.Tr("ui.mfa.code_label") };
        _box.AddChild(codeLabel);

        _codeEdit = new LineEdit
        {
            PlaceholderText = L10n.Tr("ui.mfa.code_placeholder"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            // Room for "123 456" pasted with its space, and a little slack; the digits are checked
            // by MfaLogin, not by a cap here that would silently cut a paste.
            MaxLength = 16,
            VirtualKeyboardType = LineEdit.VirtualKeyboardTypeEnum.Number,
            SelectAllOnFocus = true,
        };
        _codeEdit.TextChanged += _ => UpdateLoginEnabled();
        _codeEdit.TextSubmitted += _ => Submit();
        _box.AddChild(_codeEdit);

        if (offerRemember)
        {
            _rememberCheck = new CheckBox
            {
                Text = L10n.Tr("ui.mfa.remember"),
                TooltipText = L10n.Tr("ui.mfa.remember_tooltip"),
                // Unticked, as in the reference viewer: keeping a login-equivalent token is a choice.
                ButtonPressed = false,
            };
            _box.AddChild(_rememberCheck);
        }

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 8);

        var cancel = new Button { Text = L10n.Tr("ui.mfa.cancel"), CustomMinimumSize = new Vector2(110, 0), FocusMode = FocusModeEnum.None };
        cancel.Pressed += Cancel;
        buttons.AddChild(cancel);

        _loginButton = new Button
        {
            Text = L10n.Tr("ui.mfa.continue"),
            CustomMinimumSize = new Vector2(110, 0),
            FocusMode = FocusModeEnum.None,
            Disabled = true,
        };
        _loginButton.Pressed += Submit;
        buttons.AddChild(_loginButton);
        _box.AddChild(buttons);

        UpdateLoginEnabled();
        CallDeferred(nameof(FocusField));
    }

    /// <summary>Focus in the code field and the window centred: deferred, because the height is only
    /// right once the children are in the tree and their wrapped text has been measured.</summary>
    private void FocusField()
    {
        if (!IsInstanceValid(this) || _codeEdit == null) return;
        FitAndCentre();
        _codeEdit.GrabFocus();
    }

    private void FitAndCentre()
    {
        var viewport = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        float width = Mathf.Min(CustomMinimumSize.X, Mathf.Max(220f, viewport.X - 24f));

        // Width first: the height of wrapped text depends on it.
        Size = new Vector2(width, Size.Y);
        Size = new Vector2(width, GetCombinedMinimumSize().Y);

        Position = new Vector2(
            Mathf.Max(0, (viewport.X - Size.X) / 2),
            Mathf.Max(TopInset + 10f, (viewport.Y - Size.Y) / 3));
        ClampToViewport();
    }

    private static Label MakeWrapLabel(string text) => new()
    {
        Text = text,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        CustomMinimumSize = new Vector2(220, 0), // width floor: an autowrap Label without one is measured at ~0 px
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
    };

    private void UpdateLoginEnabled()
        => _loginButton.Disabled = !MfaLogin.IsPlausibleToken(_codeEdit.Text);

    /// <summary>Sets the field as if the person typed or pasted <paramref name="text"/> (test hook and
    /// paste path alike: the same handler runs).</summary>
    internal void SetCodeText(string text)
    {
        _codeEdit.Text = text;
        UpdateLoginEnabled();
    }

    internal void SetRemember(bool value)
    {
        if (_rememberCheck != null) _rememberCheck.ButtonPressed = value;
    }

    /// <summary>Confirms. Refused, with the window left open, while the field does not hold six
    /// digits.</summary>
    internal void Submit()
    {
        if (_answered || _codeEdit == null) return;
        if (!MfaLogin.IsPlausibleToken(_codeEdit.Text)) return;

        _answered = true;
        string token = MfaLogin.NormalizeToken(_codeEdit.Text);
        bool remember = _rememberCheck?.ButtonPressed ?? false;
        // Forget the field's copy before anything else runs: the code is single-use but still not
        // something to leave in a node that may linger a frame.
        _codeEdit.Text = "";
        Submitted?.Invoke(token, remember);
        QueueFree();
    }

    internal void Cancel()
    {
        if (_answered) return;
        _answered = true;
        if (_codeEdit != null) _codeEdit.Text = "";
        Cancelled?.Invoke();
        QueueFree();
    }
}
