using Godot;
using System;

namespace SLNG.App.UI;

/// <summary>
/// A one-line question with a text field: "name this folder", "rename this to…". Built for
/// FEAT-INV-08's folder operations and deliberately generic, because the alternative was a fourth
/// hand-rolled name field in this panel.
/// </summary>
/// <remarks>
/// Confirming with an empty field is refused rather than accepted-as-blank: an inventory folder
/// with no name is not something the user can undo by looking at it.
///
/// <para>The caller owns the consequences — this window only collects a string and closes. It
/// reports a cancel as well as a confirm, because "the user changed their mind" and "the user
/// confirmed an empty name" have to be different outcomes for the caller.</para>
/// </remarks>
public partial class TextPromptWindow : SLNGWindow
{
    /// <summary>The user confirmed. The string is trimmed and never empty.</summary>
    public event Action<string>? Confirmed;

    /// <summary>Fired once the window has closed and freed itself, confirmed or not.</summary>
    public event Action? Closed;

    private LineEdit _edit = null!;
    private Button _okButton = null!;
    private bool _closing;

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(340, 0); // height comes from FitAndCentre
        Size = CustomMinimumSize;
        OnCloseRequested = () => Close(confirm: false);
    }

    /// <param name="title">The window's own title bar.</param>
    /// <param name="prompt">The question, above the field.</param>
    /// <param name="initialText">Pre-filled and selected, so "rename" starts from the old name and
    /// one keystroke replaces it.</param>
    /// <param name="confirmLabel">What the confirming button says. Never "OK" by default: a button
    /// that names its action is the one the user can read without re-reading the prompt.</param>
    public void Initialize(string title, string prompt, string initialText, string confirmLabel)
    {
        Title = title;

        var margin = new MarginContainer();
        ContentContainer.AddChild(margin);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 10);
        margin.AddChild(box);

        var label = new Label
        {
            Text = prompt,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        box.AddChild(label);

        _edit = new LineEdit
        {
            Text = initialText ?? string.Empty,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _edit.TextSubmitted += _ => Close(confirm: true);
        _edit.TextChanged += _ => UpdateOkEnabled();
        box.AddChild(_edit);

        var buttons = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        buttons.AddThemeConstantOverride("separation", 8);
        buttons.Alignment = BoxContainer.AlignmentMode.End;
        box.AddChild(buttons);

        var cancel = new Button { Text = L10n.Tr("ui.common.cancel"), FocusMode = FocusModeEnum.None };
        cancel.Pressed += () => Close(confirm: false);
        buttons.AddChild(cancel);

        _okButton = new Button { Text = confirmLabel, FocusMode = FocusModeEnum.None };
        _okButton.Pressed += () => Close(confirm: true);
        buttons.AddChild(_okButton);

        UpdateOkEnabled();
        CallDeferred(nameof(FocusField));
    }

    private void FocusField()
    {
        if (!IsInstanceValid(this) || _edit == null) return;
        FitAndCentre();
        _edit.GrabFocus();
        _edit.SelectAll();
    }

    private void UpdateOkEnabled()
        => _okButton.Disabled = string.IsNullOrWhiteSpace(_edit.Text);


    /// <summary>Sizes the frame to exactly what its contents need, and centres it.</summary>
    /// <remarks>
    /// Not left to <c>Size.Y = 0</c>. A Godot <c>Control</c> does not shrink-wrap: the height it
    /// ends up with depends on how the layout settles, and this window came out the full height of
    /// the viewport in-world. <c>GetCombinedMinimumSize()</c> is the layout's own answer to "how
    /// much room do these children need", which is the question being asked — the same call
    /// <see cref="SLNGWindow"/> uses to collapse a frame to its title bar.
    ///
    /// <para>Deferred, because the answer is only correct once the children are in the tree and
    /// their wrapped text has been measured. The width is clamped to the viewport too: a prompt
    /// wider than the window it appears in has its buttons off the edge, which a narrow client
    /// makes easy to hit.</para>
    /// </remarks>
    private void FitAndCentre()
    {
        if (!IsInstanceValid(this)) return;

        var viewport = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        float width = Mathf.Min(CustomMinimumSize.X, Mathf.Max(220f, viewport.X - 24f));

        // Width first: the height of wrapped text depends on it.
        Size = new Vector2(width, Size.Y);
        Size = new Vector2(width, GetCombinedMinimumSize().Y);

        Position = new Vector2(
            Mathf.Max(0, (viewport.X - Size.X) / 2),
            Mathf.Max(0, (viewport.Y - Size.Y) / 3));
    }

    private void Close(bool confirm)
    {
        if (_closing) return;

        // An empty name is refused rather than accepted: leave the window open so the user can
        // see why nothing happened.
        string text = _edit?.Text?.Trim() ?? string.Empty;
        if (confirm && text.Length == 0) return;

        _closing = true;
        if (confirm) Confirmed?.Invoke(text);
        Closed?.Invoke();
        QueueFree();
    }
}
