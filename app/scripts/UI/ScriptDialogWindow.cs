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
        ownerLabel.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
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

        PersistId = $"script_dialog_{_objectId}";
        PositionWindow();
    }

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(340, 0); // Height 0 allows it to shrink-wrap its contents without overlapping
        Size = CustomMinimumSize;

        OnCloseRequested = () => Close(sendReply: false, buttonIndex: -1, buttonLabel: "");

        // Inset comes from SLNGWindow.ContentContainer now (FEAT-UI-26); this container is kept
        // only because the layout below hangs off it.
        var margin = new MarginContainer();
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
            Mathf.Max(TopInset + 10f, (viewportSize.Y - Size.Y) / 3) + cascadeOffset.Y);
        ClampToViewport();
    }

    /// <summary>Lays out up to 12 buttons bottom-to-top/left-to-right (see class doc), then a
    /// separate always-at-bottom Ignore button, matching the real viewer's reserved ignore row.</summary>
    private void BuildButtonGrid(System.Collections.Generic.IReadOnlyList<string> labels)
    {
        // One grid, one button width: an HBox per row sized every button to its own text, so "Ignore"-short and
        // "Wildest Dreams"-long labels sat side by side at different widths. Every cell is now as wide as the
        // widest label (within a cap; a longer one is cut with an ellipsis and its full text is the tooltip),
        // and a partial row is padded with empty cells so it keeps the grid's columns.
        const int Gap = 6;
        var grid = new GridContainer { Columns = ButtonsPerRow, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", Gap);
        grid.AddThemeConstantOverride("v_separation", Gap);
        _contentVBox.AddChild(grid);

        var buttons = new System.Collections.Generic.List<Button>();
        var rowCount = Mathf.CeilToInt(labels.Count / (float)ButtonsPerRow);
        for (int row = rowCount - 1; row >= 0; row--)
        {
            for (int col = 0; col < ButtonsPerRow; col++)
            {
                int index = row * ButtonsPerRow + col;
                if (index >= labels.Count)
                {
                    grid.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
                    continue;
                }

                var label = labels[index];
                var btn = new Button
                {
                    Text = label,
                    TooltipText = label,
                    ClipText = true,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                };
                btn.Pressed += () => Close(sendReply: true, buttonIndex: index, buttonLabel: label);
                grid.AddChild(btn);
                buttons.Add(btn);
            }
        }

        if (buttons.Count > 0)
        {
            var font = buttons[0].GetThemeFont("font");
            int fontSize = buttons[0].GetThemeFontSize("font_size");
            float widest = 0;
            foreach (var l in labels)
                widest = Mathf.Max(widest, font.GetStringSize(l, HorizontalAlignment.Left, -1, fontSize).X);
            float cell = Mathf.Clamp(widest + 28f, 96f, 180f);

            foreach (var child in grid.GetChildren())
                if (child is Control c) c.CustomMinimumSize = new Vector2(cell, 0);

            // The window grows to fit the grid instead of squeezing the cells back to different widths.
            float needed = ButtonsPerRow * cell + (ButtonsPerRow - 1) * Gap + 2 * DefaultContentMarginH + 8;
            CustomMinimumSize = new Vector2(Mathf.Max(CustomMinimumSize.X, needed), 0);
            Size = new Vector2(CustomMinimumSize.X, Size.Y);
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
