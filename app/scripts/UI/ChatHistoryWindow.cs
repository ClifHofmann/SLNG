using System;
using Godot;
using SLNG.Core;
using SLNG.Core.Services;

namespace SLNG.App.UI;

/// <summary>
/// Read-only, paginated viewer over a chat log file on disk (§2a of the M5-3 spec). Opened via
/// the "History" button in <see cref="ChatWindow"/>'s Chat body — deliberately reads from
/// <see cref="ChatLogger.GetPage"/> rather than the live in-memory buffer, so it always reflects
/// the full logged record even for a conversation whose tab was closed and reopened.
/// </summary>
public partial class ChatHistoryWindow : SLNGWindow
{
    private const int PageSize = 100;

    private RichTextLabel _log = null!;
    private Label _pageLabel = null!;
    private Button _prevButton = null!;
    private Button _nextButton = null!;

    private ChatLogger _logger = null!;
    private ChatLogKind _kind;
    private string _conversationName = "";
    private int _pageIndex;
    private int _totalPages = 1;

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(360, 280);
        Size = new Vector2(420, 360);
        OnCloseRequested = QueueFree;

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        ContentContainer.AddChild(vbox);

        _log = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = false,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _log.AddThemeFontSizeOverride("normal_font_size", ChatWindow.BodyFontSize);
        _log.MetaClicked += OnLogMetaClicked;
        vbox.AddChild(_log);

        var nav = new HBoxContainer();
        vbox.AddChild(nav);

        _prevButton = new Button { Text = "< Prev", FocusMode = FocusModeEnum.None };
        _prevButton.AddThemeFontSizeOverride("font_size", ChatWindow.LabelFontSize);
        _prevButton.Pressed += () => GoToPage(_pageIndex - 1);
        nav.AddChild(_prevButton);

        _pageLabel = new Label
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _pageLabel.AddThemeFontSizeOverride("font_size", ChatWindow.MetaFontSize);
        _pageLabel.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        nav.AddChild(_pageLabel);

        _nextButton = new Button { Text = "Next >", FocusMode = FocusModeEnum.None };
        _nextButton.AddThemeFontSizeOverride("font_size", ChatWindow.LabelFontSize);
        _nextButton.Pressed += () => GoToPage(_pageIndex + 1);
        nav.AddChild(_nextButton);
    }

    public Action<Guid, string>? OnOpenProfileRequested { get; set; }
    public Action<string, System.Numerics.Vector3>? OnTeleportRequested { get; set; }

    /// <summary>Loads and shows the most recent page for one conversation's on-disk log.</summary>
    public void Open(ChatLogger logger, ChatLogKind kind, string conversationName, string title)
    {
        _logger = logger;
        _kind = kind;
        _conversationName = conversationName;
        Title = $"HISTORY: {title}";

        // GetPage clamps out-of-range indices, so asking for a huge page index is a cheap way to
        // discover totalPages and land on the last (most recent) page in one call.
        _logger.GetPage(_kind, _conversationName, int.MaxValue, PageSize, out _totalPages);
        GoToPage(_totalPages - 1);
    }

    private void GoToPage(int pageIndex)
    {
        var lines = _logger.GetPage(_kind, _conversationName, pageIndex, PageSize, out _totalPages);
        _pageIndex = Mathf.Clamp(pageIndex, 0, _totalPages - 1);

        _log.Clear();
        if (lines.Count == 0)
        {
            _log.AppendText("[i]No logged history yet.[/i]");
        }
        else
        {
            foreach (var line in lines)
                _log.AppendText($"{ChatTextParser.FormatMessageToBbCode(line)}\n");
        }

        _pageLabel.Text = $"Page {_pageIndex + 1} / {_totalPages}";
        _prevButton.Disabled = _pageIndex <= 0;
        _nextButton.Disabled = _pageIndex >= _totalPages - 1;
    }

    private void OnLogMetaClicked(Variant meta)
    {
        var s = meta.AsString();
        const string prefix = "avatar:";
        if (s.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParse(s.AsSpan(prefix.Length), out var id))
        {
            OnOpenProfileRequested?.Invoke(id, "");
            return;
        }

        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            OS.ShellOpen(s);
            return;
        }

        const string slPrefix = "secondlife:///app/";
        if (s.StartsWith(slPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string rest = s[slPrefix.Length..];
            string[] parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[0], "agent", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(parts[1], out var agentId))
            {
                OnOpenProfileRequested?.Invoke(agentId, "");
                return;
            }
            if (parts.Length >= 5 && string.Equals(parts[0], "teleport", StringComparison.OrdinalIgnoreCase))
            {
                string region = Uri.UnescapeDataString(parts[1]);
                if (float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y)
                    && float.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                {
                    OnTeleportRequested?.Invoke(region, new System.Numerics.Vector3(x, y, z));
                }
                return;
            }
        }
    }
}
