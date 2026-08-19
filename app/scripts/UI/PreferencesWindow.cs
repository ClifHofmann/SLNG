using System;
using System.Collections.Generic;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// Settings dialog shell: a classic two-pane layout with vertical tab-select buttons on the
/// left and the selected tab's content on the right. Godot's built-in TabContainer only offers
/// a horizontal, top-mounted tab strip, so this hand-builds the left column instead.
///
/// Deliberately generic -- this class knows nothing about toolbars, cameras, or inventory. Call
/// <see cref="AddTab"/> once per tab (see Boot.SetupHud, which adds a "Toolbar" tab backed by
/// <see cref="ToolbarPreferencesPage"/>). Adding a second tab later is exactly one more AddTab
/// call plus whatever Control builds that tab's content -- no switch statement to extend here.
/// </summary>
public partial class PreferencesWindow : SLNGWindow
{
    private VBoxContainer _tabList = null!;
    private Control _pageHost = null!;
    /// <summary>Holds the ScrollContainer wrapping each page, not the page itself -- the wrapper is
    /// what gets shown and hidden.</summary>
    private readonly List<(Button TabButton, Control Page)> _tabs = new();

    public override void _Ready()
    {
        base._Ready(); // SLNGWindow styling

        Title = L10n.Tr("ui.preferences.title");
        Visible = false;
        CustomMinimumSize = new Vector2(560, 440);
        Size = new Vector2(560, 440);
        Position = new Vector2(260, 160);

        OnCloseRequested = Hide;

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 0);
        ContentContainer.AddChild(hbox);

        // Left: vertical tab strip.
        var tabListPanel = new PanelContainer { CustomMinimumSize = new Vector2(140, 0) };
        var tabListStyle = new StyleBoxFlat
        {
            BgColor = new Color(1, 1, 1, 0.03f),
            BorderWidthRight = 1,
            BorderColor = new Color(1, 1, 1, 0.08f),
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
        };
        tabListPanel.AddThemeStyleboxOverride("panel", tabListStyle);
        hbox.AddChild(tabListPanel);

        _tabList = new VBoxContainer();
        _tabList.AddThemeConstantOverride("separation", 4);
        tabListPanel.AddChild(_tabList);

        // Right: selected tab's page.
        var pageMargin = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        pageMargin.AddThemeConstantOverride("margin_left", 16);
        pageMargin.AddThemeConstantOverride("margin_right", 16);
        pageMargin.AddThemeConstantOverride("margin_top", 12);
        pageMargin.AddThemeConstantOverride("margin_bottom", 12);
        hbox.AddChild(pageMargin);

        _pageHost = new Control
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        pageMargin.AddChild(_pageHost);
    }

    /// <summary>Registers one tab: a left-column select button plus its content Control. The
    /// first tab ever added starts selected. <paramref name="page"/> should be populated (or
    /// populated right after this call returns) by its own Initialize-style method -- this only
    /// places it in the tree and wires visibility, matching how the rest of this codebase
    /// constructs a panel then calls a separate Initialize (see ItemPropertiesWindow).</summary>
    public void AddTab(string tabName, Control page)
    {
        bool isFirst = _tabs.Count == 0;

        // Each page lives in its own ScrollContainer. Without one a page taller than the dialog
        // does not clip -- it draws straight over the 3D world outside the window frame, which is
        // what the Graphics tab did as soon as it grew past four controls. Scrolling also means a
        // page can be extended later without anyone having to remember to resize this window.
        var scroll = new ScrollContainer
        {
            Visible = isFirst,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            ClipContents = true,
        };
        scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _pageHost.AddChild(scroll);

        // Inside a container, layout is the container's job -- an anchor preset on the child is
        // ignored at best and fights it at worst. Width comes from the fill flag; height is left to
        // the content, which is what gives the scroll bar something to scroll.
        page.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(page);

        var tabButton = new Button
        {
            Text = tabName,
            ToggleMode = true,
            ButtonPressed = isFirst,
            FocusMode = Control.FocusModeEnum.None,
            Alignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 32),
        };
        StyleTabButton(tabButton);
        tabButton.Pressed += () => SelectTab(scroll);
        _tabList.AddChild(tabButton);

        _tabs.Add((tabButton, scroll));
    }

    private static void StyleTabButton(Button btn)
    {
        var normalStyle = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), ContentMarginLeft = 12 };
        var hoverStyle = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.06f), ContentMarginLeft = 12, CornerRadiusTopLeft = 8, CornerRadiusBottomLeft = 8 };
        var pressedStyle = new StyleBoxFlat { BgColor = new Color(0.3f, 0.6f, 0.9f, 0.25f), ContentMarginLeft = 12, CornerRadiusTopLeft = 8, CornerRadiusBottomLeft = 8 };
        btn.AddThemeStyleboxOverride("normal", normalStyle);
        btn.AddThemeStyleboxOverride("hover", hoverStyle);
        btn.AddThemeStyleboxOverride("pressed", pressedStyle);
        btn.AddThemeStyleboxOverride("hover_pressed", pressedStyle);
        btn.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        btn.AddThemeColorOverride("font_pressed_color", new Color(1, 1, 1));
    }

    private void SelectTab(Control selectedPage)
    {
        foreach (var (tabButton, page) in _tabs)
        {
            bool selected = page == selectedPage;
            page.Visible = selected;
            tabButton.ButtonPressed = selected;
        }
    }
}
