using Godot;
using System;
using SLNG.Core;
using SLNG.Net;
using static Godot.Control;

namespace SLNG.App.UI;

/// <summary>
/// Modal popup for an LSL <c>llDialog</c> script menu (M5-4). Renders the prompting object's
/// name/owner, the message text, and up to 12 dynamic buttons in the real SL viewer's layout --
/// first button bottom-left, filling left-to-right then wrapping upward one row at a time
/// (verified against lltoastnotifypanel.cpp's updateButtonsLayout: "we arrange buttons from
/// bottom to top for backward support of old script"). Clicking a button (or the always-present
/// Ignore) answers via GridSession.ReplyToScriptDialog and closes; Ignore sends no reply at all,
/// matching real-viewer behavior of silently dismissing without a script response.
/// </summary>
public partial class ScriptDialogWindow : SLNGWindow
{
    private const int ButtonsPerRow = 3;

    /// <summary>Fired once this window has closed (answered or ignored) and freed itself, so an
    /// owner tracking multiple concurrent dialogs (DialogQueueManager) knows to drop it -- same
    /// pattern as ObjectEditWindow.Closed.</summary>
    public event Action? Closed;

    /// <summary>Set by the owner before Initialize so several simultaneously-open dialogs cascade
    /// instead of stacking exactly on top of each other -- same pattern as
    /// ObjectEditWindow.CascadeIndex.</summary>
    public int CascadeIndex { get; set; }

    private GridSession _session = null!;
    private Guid _objectId;
    private int _channel;
    private bool _closing;
    private VBoxContainer _contentVBox = null!;

    public void Initialize(GridSession session, ScriptDialogEvent e)
    {
        _session = session;
        _objectId = e.ObjectId;
        _channel = e.Channel;

        Title = e.ObjectName;

        var ownerLabel = new Label { Text = $"by {e.OwnerName}" };
        ownerLabel.AddThemeFontSizeOverride("font_size", 11);
        ownerLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        _contentVBox.AddChild(ownerLabel);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 120), // Increased from 60 to allow more text visibility
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        var messageLabel = new Label
        {
            Text = e.Message,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        scroll.AddChild(messageLabel);
        _contentVBox.AddChild(scroll);

        BuildButtonGrid(e.ButtonLabels);

        CallDeferred(nameof(PositionWindow));
    }

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(340, 0); // Height 0 allows it to shrink-wrap its contents without overlapping
        Size = CustomMinimumSize;

        OnCloseRequested = () => Close(sendReply: false, buttonIndex: -1, buttonLabel: "");

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        ContentContainer.AddChild(margin);

        _contentVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        _contentVBox.AddThemeConstantOverride("separation", 6);
        margin.AddChild(_contentVBox);
    }

    private void PositionWindow()
    {
        var viewportSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        var cascadeOffset = new Vector2(CascadeIndex * 28, CascadeIndex * 28);
        Position = new Vector2(
            Mathf.Max(0, (viewportSize.X - Size.X) / 2) + cascadeOffset.X,
            Mathf.Max(0, (viewportSize.Y - Size.Y) / 3) + cascadeOffset.Y);
    }

    /// <summary>Lays out up to 12 buttons bottom-to-top/left-to-right (see class doc), then a
    /// separate always-at-bottom Ignore button, matching the real viewer's reserved ignore row.</summary>
    private void BuildButtonGrid(System.Collections.Generic.IReadOnlyList<string> labels)
    {
        var rowCount = Mathf.CeilToInt(labels.Count / (float)ButtonsPerRow);
        for (int row = rowCount - 1; row >= 0; row--)
        {
            var rowBox = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            rowBox.AddThemeConstantOverride("separation", 6);
            for (int col = 0; col < ButtonsPerRow; col++)
            {
                int index = row * ButtonsPerRow + col;
                if (index >= labels.Count) break;

                var label = labels[index];
                var btn = new Button { Text = label, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                btn.Pressed += () => Close(sendReply: true, buttonIndex: index, buttonLabel: label);
                rowBox.AddChild(btn);
            }
            _contentVBox.AddChild(rowBox);
        }

        var ignoreBox = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        var ignoreBtn = new Button { Text = "Ignore", CustomMinimumSize = new Vector2(80, 0) };
        ignoreBtn.Pressed += () => Close(sendReply: false, buttonIndex: -1, buttonLabel: "");
        ignoreBox.AddChild(ignoreBtn);
        _contentVBox.AddChild(ignoreBox);
    }

    private void Close(bool sendReply, int buttonIndex, string buttonLabel)
    {
        if (_closing) return;
        _closing = true;

        if (sendReply)
            _session.ReplyToScriptDialog(_objectId, _channel, buttonIndex, buttonLabel);

        Closed?.Invoke();
        QueueFree();
    }
}
