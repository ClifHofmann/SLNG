using Godot;
using System;

namespace SLNG.App.UI;

/// <summary>
/// A yes/no question before something the user would rather not have done by accident.
/// </summary>
/// <remarks>
/// The safe answer is on the LEFT and holds focus, the same way the script-permission prompt does
/// it (FEAT-NET-01): Return and a stray click both land on "no". Nothing in this window decides
/// anything by itself — dismissing it with the title-bar × is a cancel, because a prompt that
/// treats being closed as consent is the one shape of confirmation that is worse than none.
/// </remarks>
public partial class ConfirmWindow : SLNGWindow
{
    /// <summary>The user said yes. Not fired for a cancel or a dismissal.</summary>
    public event Action? Confirmed;

    /// <summary>Fired once the window has closed and freed itself, either way.</summary>
    public event Action? Closed;

    private bool _closing;

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(360, 0); // height comes from FitAndCentre
        Size = CustomMinimumSize;
        OnCloseRequested = () => Close(confirm: false);
    }

    /// <param name="confirmLabel">What the confirming button says. It names the action — "Move to
    /// Trash", not "OK" — so the button can be read without re-reading the question.</param>
    /// <param name="danger">Colours the confirming label as a warning. For an action that destroys
    /// or spends something.</param>
    public void Initialize(string title, string question, string confirmLabel, bool danger = false)
    {
        Title = title;

        var margin = new MarginContainer();
        ContentContainer.AddChild(margin);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 12);
        margin.AddChild(box);

        var label = new Label
        {
            Text = question,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        box.AddChild(label);

        var buttons = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        buttons.AddThemeConstantOverride("separation", 8);
        box.AddChild(buttons);

        var cancel = new Button
        {
            Text = L10n.Tr("ui.common.cancel"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        cancel.Pressed += () => Close(confirm: false);
        buttons.AddChild(cancel);

        var ok = new Button
        {
            Text = confirmLabel,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
        };
        if (danger) ok.AddThemeColorOverride("font_color", new Color(1f, 0.55f, 0.45f));
        ok.Pressed += () => Close(confirm: true);
        buttons.AddChild(ok);

        // Deferred: focus before the node is in the tree does nothing.
        CallDeferred(nameof(FocusCancel));
    }

    private void FocusCancel()
    {
        if (!IsInstanceValid(this)) return;
        FitAndCentre();
        foreach (var child in GetChildren())
            if (FindCancelButton(child) is { } b) { b.GrabFocus(); return; }
    }

    private static Button? FindCancelButton(Node node)
    {
        if (node is Button b && b.Text == L10n.Tr("ui.common.cancel")) return b;
        foreach (var child in node.GetChildren())
            if (FindCancelButton(child) is { } found) return found;
        return null;
    }


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
        _closing = true;
        if (confirm) Confirmed?.Invoke();
        Closed?.Invoke();
        QueueFree();
    }
}
