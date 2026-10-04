using Godot;
using System;
using System.Collections.Generic;
using SLNG.Core.ChatLogs;
using SLNG.Core.Services;

namespace SLNG.App.UI;

/// <summary>
/// "Recent" tab content for <see cref="ChatWindow"/> (FEAT-UI-15): the last IM and group conversations,
/// newest first, each reopenable with one click -- including ones whose tab was closed or that were last
/// used in an earlier session. Opening goes through the same <see cref="ChatWindow"/> openers the Friends
/// and Groups tabs use, so the tab comes back with the tail of its on-disk log already loaded.
///
/// A row is: kind glyph, name, the last logged line as a muted preview, and when. The page's own buttons
/// are the full-history viewer and removing the row from the list (the log file is never touched).
/// </summary>
public partial class RecentPanel : Control
{
    private const int PreviewMaxChars = 80;

    private Label _emptyLabel = null!;
    private VBoxContainer _list = null!;
    private Button _clearButton = null!;

    private IReadOnlyList<RecentConversation> _items = Array.Empty<RecentConversation>();
    private ChatLogger? _logger;

    /// <summary>Wired by ChatWindow: the name to show for a row (the Display Name when there is one).
    /// The entry's own name stays the legacy name the log file is keyed by.</summary>
    public Func<RecentConversation, string>? ShownName;
    /// <summary>Wired by ChatWindow: the avatar's profile picture for an IM row, or null while it has none (the
    /// row then shows the plain kind glyph).</summary>
    public Func<RecentConversation, Texture2D?>? IconFor;
    /// <summary>Wired by ChatWindow: reopen one conversation.</summary>
    public Action<ChatLogKind, Guid, string>? OnOpenRequested;
    /// <summary>Wired by ChatWindow: show a conversation's full on-disk history.</summary>
    public Action<ChatLogKind, string>? OnHistoryRequested;
    /// <summary>Wired by ChatWindow: drop one row (<see cref="OnClearRequested"/> drops them all).</summary>
    public Action<ChatLogKind, Guid>? OnRemoveRequested;
    public Action? OnClearRequested;

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var vbox = new VBoxContainer();
        vbox.SetAnchorsPreset(LayoutPreset.FullRect);
        vbox.AddThemeConstantOverride("separation", 6);
        AddChild(vbox);

        var header = new HBoxContainer();
        vbox.AddChild(header);
        var hint = new Label
        {
            Text = L10n.Tr("ui.recent.hint"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
        };
        hint.AddThemeFontSizeOverride("font_size", ChatWindow.MetaFontSize);
        hint.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        header.AddChild(hint);

        _clearButton = new Button
        {
            Text = L10n.Tr("ui.recent.clear"),
            TooltipText = L10n.Tr("ui.recent.clear_tooltip"),
            FocusMode = FocusModeEnum.None,
            Flat = true,
        };
        _clearButton.AddThemeFontSizeOverride("font_size", ChatWindow.MetaFontSize);
        _clearButton.Pressed += () => OnClearRequested?.Invoke();
        header.AddChild(_clearButton);

        _emptyLabel = new Label
        {
            Text = L10n.Tr("ui.recent.empty"),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(220, 0),
        };
        _emptyLabel.AddThemeFontSizeOverride("font_size", ChatWindow.BodyFontSize);
        _emptyLabel.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        vbox.AddChild(_emptyLabel);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        vbox.AddChild(scroll);

        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_list);

        Rebuild();
    }

    /// <summary>Shows <paramref name="items"/>. <paramref name="logger"/> supplies each row's preview line.</summary>
    public void SetItems(IReadOnlyList<RecentConversation> items, ChatLogger logger)
    {
        _items = items;
        _logger = logger;
        if (IsInsideTree()) Rebuild();
    }

    // ---- selftest seams ------------------------------------------------------------------------
    internal int RowCount => _list?.GetChildCount() ?? 0;
    internal bool EmptyHintVisible => _emptyLabel.Visible;
    internal void OpenRowForSelfTest(int index) => OnOpenRequested?.Invoke(_items[index].Kind, _items[index].Id, _items[index].Name);

    private void Rebuild()
    {
        foreach (Node child in _list.GetChildren())
        {
            _list.RemoveChild(child);
            child.QueueFree();
        }

        _emptyLabel.Visible = _items.Count == 0;
        _clearButton.Visible = _items.Count > 0;

        foreach (var item in _items)
            _list.AddChild(BuildRow(item));
    }

    private Control BuildRow(RecentConversation item)
    {
        var row = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(1, 1, 1, 0.04f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 6,
            ContentMarginRight = 4,
            ContentMarginTop = 3,
            ContentMarginBottom = 3,
        });

        var inner = new HBoxContainer();
        inner.AddThemeConstantOverride("separation", 6);
        row.AddChild(inner);

        var picture = IconFor?.Invoke(item);
        if (picture != null)
        {
            inner.AddChild(new TextureRect
            {
                Texture = picture,
                CustomMinimumSize = new Vector2(AvatarIcons.IconSize, AvatarIcons.IconSize),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                TooltipText = L10n.Tr("ui.recent.kind_im"),
            });
        }
        else
        {
            var kindLabel = new Label
            {
                Text = item.Kind == ChatLogKind.Group ? "👥" : "💬",
                TooltipText = L10n.Tr(item.Kind == ChatLogKind.Group ? "ui.recent.kind_group" : "ui.recent.kind_im"),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
            };
            kindLabel.AddThemeFontSizeOverride("font_size", ChatWindow.BodyFontSize);
            inner.AddChild(kindLabel);
        }

        var textCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        textCol.AddThemeConstantOverride("separation", 0);
        inner.AddChild(textCol);

        var nameBtn = new Button
        {
            Text = ShownName?.Invoke(item) ?? item.Name,
            Flat = true,
            ClipText = true,
            FocusMode = FocusModeEnum.None,
            Alignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = L10n.Tr("ui.recent.open_tooltip"),
        };
        nameBtn.AddThemeFontSizeOverride("font_size", ChatWindow.BodyFontSize);
        nameBtn.AddThemeColorOverride("font_color", new Color(0.92f, 0.92f, 0.92f));
        var (kind, id, name) = (item.Kind, item.Id, item.Name);
        nameBtn.Pressed += () => OnOpenRequested?.Invoke(kind, id, name);
        textCol.AddChild(nameBtn);

        string preview = Preview(item);
        if (preview.Length > 0)
        {
            var previewLabel = new Label { Text = preview, ClipText = true };
            previewLabel.AddThemeFontSizeOverride("font_size", ChatWindow.MetaFontSize);
            previewLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f));
            textCol.AddChild(previewLabel);
        }

        var when = new Label
        {
            Text = FormatWhen(item.LastActivityUtc),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        when.AddThemeFontSizeOverride("font_size", ChatWindow.MetaFontSize);
        when.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        inner.AddChild(when);

        inner.AddChild(BuildSmallButton("📜", L10n.Tr("ui.recent.history_tooltip"),
            () => OnHistoryRequested?.Invoke(kind, name)));
        inner.AddChild(BuildSmallButton("✕", L10n.Tr("ui.recent.remove_tooltip"),
            () => OnRemoveRequested?.Invoke(kind, id)));

        return row;
    }

    private static Button BuildSmallButton(string glyph, string tooltip, Action onPressed)
    {
        var btn = new Button
        {
            Text = glyph,
            TooltipText = tooltip,
            Flat = true,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(24, 22),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        btn.AddThemeFontSizeOverride("font_size", ChatWindow.MetaFontSize);
        btn.Pressed += onPressed;
        return btn;
    }

    /// <summary>The last logged message of the conversation, one line. Read from the end of the file
    /// (<see cref="ChatLogger.GetTail"/>), so it is cheap however big the log is.</summary>
    private string Preview(RecentConversation item)
    {
        if (_logger == null) return "";
        var tail = _logger.GetTail(item.Kind, item.Name, 1);
        if (tail.Count == 0) return "";

        string line = tail[^1].Replace('\n', ' ').Trim();
        // Drop the "[2026/04/11 08:07]  " stamp: the row already has its own time.
        if (line.StartsWith('[') && line.IndexOf(']') is var end and > 0) line = line[(end + 1)..].TrimStart();
        return line.Length <= PreviewMaxChars ? line : line[..PreviewMaxChars] + "…";
    }

    /// <summary>"14:05" today, "Yesterday", else the date -- in the viewer's local time.</summary>
    internal static string FormatWhen(DateTime whenUtc) => FormatWhen(whenUtc, DateTime.Now);

    internal static string FormatWhen(DateTime whenUtc, DateTime nowLocal)
    {
        var local = whenUtc.ToLocalTime();
        if (local.Date == nowLocal.Date) return local.ToString("HH:mm");
        if (local.Date == nowLocal.Date.AddDays(-1)) return L10n.Tr("ui.recent.yesterday");
        return local.ToString("yyyy-MM-dd");
    }
}
