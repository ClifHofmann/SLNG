using Godot;
using System;
using System.Collections.Generic;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-33: where an arriving payment, invitation or system message lands.
///
/// <para>Those used to go to the chat log, which is the wrong place for them twice over: a
/// payment scrolls away behind local chatter within seconds, and afterwards there is nowhere to
/// look up who paid you this morning. Asked for in-world while testing the received-payment line
/// — "wir brauchen ein Feature für das Benachrichtigungsfenster, dort landen solche Infos".</para>
///
/// <para>The counts in the tab titles are the point of the thing: "Transaktionen (1)" and
/// "1 thing happened" are different sentences, and only the first says whether to look. The
/// counting and dismissal rules live in <see cref="NotificationStore"/> so they can be tested
/// away from a scene — a count that drifts from its list is how this kind of window goes wrong,
/// and it does so quietly.</para>
/// </summary>
public partial class NotificationWindow : SLNGWindow
{
    /// <summary>The tabs, in the order they are shown, with their title keys.</summary>
    private static readonly (NotificationKind Kind, string Key)[] Tabs =
    {
        (NotificationKind.System, "ui.notifications.tab_system"),
        (NotificationKind.Transaction, "ui.notifications.tab_transactions"),
        (NotificationKind.Invitation, "ui.notifications.tab_invitations"),
        (NotificationKind.Group, "ui.notifications.tab_group"),
    };

    private NotificationStore _store = null!;
    private NotificationKind _active = NotificationKind.Transaction;

    private readonly Dictionary<NotificationKind, Button> _tabButtons = new();
    private VBoxContainer _entryList = null!;
    private Label _emptyLabel = null!;
    private Button _dismissAllButton = null!;
    private readonly HashSet<Guid> _expanded = new();

    /// <summary>Asked to open a resident's profile, when their name in an entry is clicked.
    /// Boot owns the profile windows, same as everywhere else.</summary>
    public Action<Guid, string>? OnOpenProfileRequested;

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(420, 380);
        Size = CustomMinimumSize;
        Visible = false;
        OnCloseRequested = () => Visible = false;

        Title = L10n.Tr("ui.notifications.title");

        var margin = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        ContentContainer.AddChild(margin);

        var root = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        root.AddThemeConstantOverride("separation", 8);
        margin.AddChild(root);

        var tabStrip = new HBoxContainer();
        tabStrip.AddThemeConstantOverride("separation", 4);
        root.AddChild(tabStrip);

        foreach (var (kind, _) in Tabs)
        {
            var button = new Button { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            button.AddThemeFontSizeOverride("font_size", 12);
            var captured = kind;
            button.Pressed += () => SelectTab(captured);
            tabStrip.AddChild(button);
            _tabButtons[kind] = button;
        }

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        root.AddChild(scroll);

        _entryList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _entryList.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_entryList);

        _emptyLabel = new Label
        {
            Text = L10n.Tr("ui.notifications.empty"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _emptyLabel.AddThemeFontSizeOverride("font_size", 11);
        _emptyLabel.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        root.AddChild(_emptyLabel);

        var footer = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        footer.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        _dismissAllButton = new Button { Text = L10n.Tr("ui.notifications.dismiss_all"), FocusMode = FocusModeEnum.None };
        _dismissAllButton.Pressed += () => _store.DismissAll(_active);
        footer.AddChild(_dismissAllButton);
        root.AddChild(footer);
    }

    public void Initialize(NotificationStore store)
    {
        _store = store;
        _store.Changed += OnStoreChanged;
        Rebuild();
    }

    /// <summary>Off the main thread is possible (the store is fed from network events), so the
    /// redraw hops rather than touching nodes directly.</summary>
    private void OnStoreChanged(object? sender, EventArgs e) => CallDeferred(MethodName.Rebuild);

    public void Toggle()
    {
        Visible = !Visible;
        if (!Visible) return;

        MoveToFront();
        MarkActiveTabRead();
    }

    /// <summary>Opens the window on the tab a particular kind lives in — used when the user
    /// clicks a toast, so they land where the thing they clicked actually is.</summary>
    public void ShowTab(NotificationKind kind)
    {
        Visible = true;
        MoveToFront();
        SelectTab(kind);
    }

    private void SelectTab(NotificationKind kind)
    {
        _active = kind;
        // Looking at a tab is what "read" means. Only THIS tab: opening the window on
        // Transactions must not clear the badge for three group notices nobody has seen, or the
        // badge stops meaning anything.
        if (Visible) MarkActiveTabRead();
        Rebuild();
    }

    private void MarkActiveTabRead()
    {
        // MarkRead raises Changed, which redraws -- so no Rebuild call here, and none is missed.
        if (_store.MarkRead(_active) == 0) Rebuild();
    }

    private void Rebuild()
    {
        if (_store == null || !IsInstanceValid(_entryList)) return;

        foreach (var (kind, key) in Tabs)
        {
            var button = _tabButtons[kind];
            button.Text = $"{L10n.Tr(key)} ({_store.CountOf(kind)})";
            // The active tab is the one that is NOT dimmed, which reads at a glance without
            // needing a second colour to mean something.
            button.Modulate = kind == _active ? Colors.White : new Color(1, 1, 1, 0.55f);
        }

        // RemoveChild before QueueFree: a freed node is still a child until the end of the frame,
        // and rebuilding twice in one frame would otherwise stack two copies of every row.
        foreach (var child in _entryList.GetChildren())
        {
            _entryList.RemoveChild(child);
            child.QueueFree();
        }

        int shown = 0;
        foreach (var entry in _store.Entries)
        {
            if (entry.Kind != _active) continue;
            _entryList.AddChild(BuildRow(entry));
            shown++;
        }

        _emptyLabel.Visible = shown == 0;
        _dismissAllButton.Disabled = shown == 0;
    }

    /// <summary>
    /// The user wants back to the decision behind an entry. The key is the owner's own, so this
    /// window never learns what kind of prompt it re-opens.
    /// </summary>
    public event Action<Guid>? ActionRequested;

    private Control BuildRow(NotificationEntry entry)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(1, 1, 1, 0.05f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
        });

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);

        var textColumn = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        textColumn.AddThemeConstantOverride("separation", 2);
        row.AddChild(textColumn);

        textColumn.AddChild(BuildText(entry));

        // Local time, and labelled as such. Firestorm writes "SLT" here, which is the grid's own
        // clock -- we do not convert to it, so claiming it would be a lie on the face of the row.
        var when = new Label { Text = entry.ReceivedUtc.ToLocalTime().ToString("g") };
        when.AddThemeFontSizeOverride("font_size", 10);
        when.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        textColumn.AddChild(when);

        if (!string.IsNullOrWhiteSpace(entry.Detail) && _expanded.Contains(entry.Id))
        {
            var detail = new Label
            {
                Text = entry.Detail,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            detail.AddThemeFontSizeOverride("font_size", 11);
            detail.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
            textColumn.AddChild(detail);
        }

        // The way back to an unanswered decision (BUG-UI-12). Shown only where there IS one:
        // an entry whose question has been settled -- or never had one -- gets no button at all,
        // because a button that re-asks something already answered reads as broken.
        if (entry.ActionKey != Guid.Empty)
        {
            var open = new Button
            {
                Text = L10n.Tr("ui.notifications.open"),
                FocusMode = FocusModeEnum.None,
            };
            var key = entry.ActionKey;
            open.Pressed += () => ActionRequested?.Invoke(key);
            row.AddChild(open);
        }

        if (!string.IsNullOrWhiteSpace(entry.Detail))
        {
            var expand = new Button
            {
                Text = _expanded.Contains(entry.Id) ? "▲" : "▼",
                FocusMode = FocusModeEnum.None,
                TooltipText = L10n.Tr("ui.notifications.expand"),
            };
            expand.Pressed += () =>
            {
                if (!_expanded.Remove(entry.Id)) _expanded.Add(entry.Id);
                Rebuild();
            };
            row.AddChild(expand);
        }

        var dismiss = new Button
        {
            Text = "✕",
            FocusMode = FocusModeEnum.None,
            TooltipText = L10n.Tr("ui.notifications.dismiss"),
        };
        dismiss.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        dismiss.AddThemeColorOverride("font_hover_color", new Color(1f, 0.5f, 0.45f));
        dismiss.Pressed += () =>
        {
            _expanded.Remove(entry.Id);
            _store.Dismiss(entry.Id);
        };
        row.AddChild(dismiss);

        return panel;
    }

    /// <summary>
    /// The entry's line, with the sender's name as a link to their profile where there is one.
    /// </summary>
    /// <remarks>
    /// Same idiom the chat log already uses — <c>[url=avatar:&lt;guid&gt;]</c> in a
    /// RichTextLabel — so a name behaves the same wherever it appears. A plain Label is used when
    /// there is nothing to link to, rather than a RichTextLabel with the markup left out: the
    /// escaping and the BBCode parser are pure cost for a line that is only ever text.
    /// </remarks>
    private Control BuildText(NotificationEntry entry)
    {
        bool linkable = entry.SenderId != Guid.Empty
            && !entry.SenderIsGroup
            && !string.IsNullOrEmpty(entry.SenderName)
            && entry.Text.Contains(entry.SenderName, StringComparison.Ordinal);

        if (!linkable)
        {
            var plain = new Label
            {
                Text = entry.Text,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            plain.AddThemeFontSizeOverride("font_size", 12);
            return plain;
        }

        int at = entry.Text.IndexOf(entry.SenderName, StringComparison.Ordinal);
        string before = ChatWindow.BbEscape(entry.Text[..at]);
        string after = ChatWindow.BbEscape(entry.Text[(at + entry.SenderName.Length)..]);
        string linked = $"[url=avatar:{entry.SenderId}][color=#7ec0ee]{ChatWindow.BbEscape(entry.SenderName)}[/color][/url]";

        var rich = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Text = before + linked + after,
        };
        rich.AddThemeFontSizeOverride("normal_font_size", 12);
        rich.MetaClicked += meta =>
        {
            var m = meta.AsString();
            const string prefix = "avatar:";
            if (m.StartsWith(prefix, StringComparison.Ordinal)
                && Guid.TryParse(m.AsSpan(prefix.Length), out var id))
            {
                OnOpenProfileRequested?.Invoke(id, entry.SenderName);
            }
        };
        return rich;
    }

    public override void _ExitTree()
    {
        if (_store != null) _store.Changed -= OnStoreChanged;
        base._ExitTree();
    }
}
