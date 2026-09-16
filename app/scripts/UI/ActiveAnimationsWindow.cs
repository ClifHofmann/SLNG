using System;
using System.Collections.Generic;
using Godot;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-ANIM-06 / FEAT-ANIM-04: Floating inspector window listing all currently playing
/// animations on the self avatar (both network/inworld and local overlays), with individual
/// and collective stop controls.
/// </summary>
public partial class ActiveAnimationsWindow : SLNGWindow
{
    private AvatarRenderer? _avatarRenderer;
    private GridSession? _session;

    private VBoxContainer _listContainer = null!;
    private Label _emptyLabel = null!;
    private Button _stopLocalBtn = null!;
    private Button _stopAllBtn = null!;
    private Timer _refreshTimer = null!;

    public override void _Ready()
    {
        PersistId = "active_animations";
        base._Ready();

        Title = L10n.Tr("ui.active_animations.title");
        CustomMinimumSize = new Vector2(440, 280);
        Size = new Vector2(440, 320);
        Position = new Vector2(240, 180);
        Visible = false;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 8);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        ContentContainer.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(vbox);

        // Header action row
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(header);

        _stopLocalBtn = new Button
        {
            Text = L10n.Tr("ui.active_animations.stop_all_local"),
            CustomMinimumSize = new Vector2(130, 26),
        };
        _stopLocalBtn.AddThemeFontSizeOverride("font_size", 12);
        _stopLocalBtn.Pressed += OnStopAllLocalPressed;
        header.AddChild(_stopLocalBtn);

        _stopAllBtn = new Button
        {
            Text = L10n.Tr("ui.active_animations.stop_all"),
            CustomMinimumSize = new Vector2(110, 26),
        };
        _stopAllBtn.AddThemeFontSizeOverride("font_size", 12);
        _stopAllBtn.Pressed += OnStopAllPressed;
        header.AddChild(_stopAllBtn);

        var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        header.AddChild(spacer);

        var refreshBtn = new Button
        {
            Text = L10n.Tr("ui.active_animations.refresh"),
            CustomMinimumSize = new Vector2(80, 26),
        };
        refreshBtn.AddThemeFontSizeOverride("font_size", 12);
        refreshBtn.Pressed += RefreshList;
        header.AddChild(refreshBtn);

        var separator = new HSeparator();
        vbox.AddChild(separator);

        // Scrollable anim list
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        vbox.AddChild(scroll);

        _listContainer = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _listContainer.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_listContainer);

        _emptyLabel = new Label
        {
            Text = L10n.Tr("ui.active_animations.no_animations"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _emptyLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        _emptyLabel.AddThemeFontSizeOverride("font_size", 12);
        _listContainer.AddChild(_emptyLabel);

        // Auto-refresh timer while visible
        _refreshTimer = new Timer
        {
            WaitTime = 0.5,
            Autostart = false,
            OneShot = false,
        };
        _refreshTimer.Timeout += () =>
        {
            if (Visible) RefreshList();
        };
        AddChild(_refreshTimer);

        VisibilityChanged += () =>
        {
            if (Visible)
            {
                _refreshTimer.Start();
                RefreshList();
            }
            else
            {
                _refreshTimer.Stop();
            }
        };
    }

    public void Initialize(AvatarRenderer? avatarRenderer, GridSession? session)
    {
        _avatarRenderer = avatarRenderer;
        _session = session;
        RefreshList();
    }

    public void RefreshList()
    {
        if (_avatarRenderer == null) return;

        // Clear existing rows (keep _emptyLabel)
        foreach (var child in _listContainer.GetChildren())
        {
            if (child == _emptyLabel) continue;
            child.QueueFree();
        }

        var anims = _avatarRenderer.GetSelfAllActiveAnimations();
        bool hasAnims = anims.Count > 0;
        _emptyLabel.Visible = !hasAnims;

        foreach (var anim in anims)
        {
            var row = CreateAnimationRow(anim);
            _listContainer.AddChild(row);
        }
    }

    private Control CreateAnimationRow((Guid Id, string Name, int Priority, bool IsLocal, string Source) anim)
    {
        var panel = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.1f, 0.14f, 0.2f, 0.6f),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.2f, 0.3f, 0.4f, 0.4f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4,
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 8);
        panel.AddChild(hbox);

        // Badge: [Lokal] vs [Inworld]
        var badge = new Label
        {
            Text = anim.IsLocal ? L10n.Tr("ui.active_animations.badge_local") : L10n.Tr("ui.active_animations.badge_inworld"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        badge.AddThemeFontSizeOverride("font_size", 11);
        badge.AddThemeColorOverride("font_color", anim.IsLocal
            ? new Color(0.2f, 0.85f, 0.75f)   // Teal for local
            : new Color(0.95f, 0.65f, 0.25f)); // Amber for inworld
        hbox.AddChild(badge);

        // Animation name
        var nameLabel = new Label
        {
            Text = anim.Name,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            ClipText = true,
        };
        nameLabel.AddThemeFontSizeOverride("font_size", 12);
        hbox.AddChild(nameLabel);

        // Source or Priority info
        var sourceLabel = new Label
        {
            Text = anim.IsLocal ? $"P{anim.Priority}" : anim.Source,
            VerticalAlignment = VerticalAlignment.Center,
        };
        sourceLabel.AddThemeFontSizeOverride("font_size", 10);
        sourceLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        hbox.AddChild(sourceLabel);

        // Stop button
        var stopBtn = new Button
        {
            Text = L10n.Tr("ui.active_animations.stop"),
            CustomMinimumSize = new Vector2(60, 22),
        };
        stopBtn.AddThemeFontSizeOverride("font_size", 11);
        stopBtn.Pressed += () =>
        {
            if (anim.IsLocal)
            {
                _avatarRenderer?.StopSelfAnimationLocal(anim.Id);
            }
            else
            {
                _session?.StopAnimation(anim.Id);
            }
            RefreshList();
        };
        hbox.AddChild(stopBtn);

        return panel;
    }

    private void OnStopAllLocalPressed()
    {
        _avatarRenderer?.StopAllSelfAnimationsLocal();
        RefreshList();
    }

    private void OnStopAllPressed()
    {
        _avatarRenderer?.StopAllSelfAnimationsLocal();
        _session?.StopAllSelfAnimations();
        _avatarRenderer?.StopSelfAnimations();
        RefreshList();
    }
}
