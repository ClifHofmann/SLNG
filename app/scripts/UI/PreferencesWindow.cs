using System.Collections.Generic;
using Godot;
using SLNG.App.UI.Prefs;

namespace SLNG.App.UI;

/// <summary>
/// Settings dialog shell (FEAT-UI-79): a category list on the left and, on the right, the selected category as one
/// scrolling column of sections. Godot's built-in TabContainer only offers a horizontal, top-mounted tab strip, so
/// the left column is hand-built instead.
///
/// <para>Deliberately generic -- this class knows nothing about toolbars, cameras, or inventory. Boot.SetupHud calls
/// <see cref="AddCategory"/> once per category and fills its <see cref="PrefsSection"/>s with
/// <see cref="PrefsRow"/>s (or, until a page is migrated, with the page embedded whole through
/// <see cref="PrefsSection.AddBlock"/>). The plan this follows is docs/specs/FEAT-UI-79-preferences-rework.md.</para>
///
/// <para>The selected category is remembered across restarts (<see cref="PrefsState"/>) and restored the first time
/// the window is shown.</para>
///
/// <para>The search field above the category list filters in place (FEAT-UI-80, <c>PreferencesWindow.Search.cs</c>).</para>
/// </summary>
public partial class PreferencesWindow : SLNGWindow
{
    private const int NavMinWidth = 160;

    private PanelContainer _navPanel = null!;
    private VBoxContainer _navList = null!;
    private Control _pageHost = null!;
    private readonly List<(PrefsCategory Category, Button NavButton, ScrollContainer Scroll)> _categories = new();
    private IReadOnlyList<PrefsCategory>? _categoryView;
    private string? _selectedId;
    private bool _restored;

    /// <summary>The column left of the pages: the search field on top (index 0), then the category list.</summary>
    public VBoxContainer LeftColumn { get; private set; } = null!;

    /// <summary>The categories, in the order of the list.</summary>
    public IReadOnlyList<PrefsCategory> Categories
    {
        get
        {
            if (_categoryView == null || _categoryView.Count != _categories.Count)
            {
                var list = new List<PrefsCategory>();
                foreach (var entry in _categories) list.Add(entry.Category);
                _categoryView = list;
            }
            return _categoryView;
        }
    }

    /// <summary>Every row of every category, in list order.</summary>
    public IEnumerable<PrefsRow> AllRows
    {
        get
        {
            foreach (var entry in _categories)
            {
                foreach (var row in entry.Category.Rows) yield return row;
            }
        }
    }

    /// <summary>Id of the category that is showing, or null before the first one was added.</summary>
    public string? SelectedCategoryId => _selectedId;

    public override void _EnterTree()
    {
        L10n.LanguageChanged += OnSearchLanguageChanged;
    }

    public override void _ExitTree()
    {
        L10n.LanguageChanged -= OnSearchLanguageChanged;
        base._ExitTree();
    }

    public override void _Ready()
    {
        // "_v2": the old window was 600 x 420 and a size saved for it would be applied again, clamped to the 640 x 420
        // minimum instead of opening at the 760 x 520 default of the seven-category layout.
        PersistId = "preferences_v2";
        base._Ready(); // SLNGWindow styling
        _builtLocale = L10n.CurrentLocale; // the language the rows' texts are in, whatever the person switches to later

        Title = L10n.Tr("ui.preferences.title");
        Visible = false;
        CustomMinimumSize = new Vector2(640, 420);
        Size = new Vector2(760, 520);
        Position = new Vector2(260, 160);

        OnCloseRequested = Hide;

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 0);
        ContentContainer.AddChild(hbox);

        // Left: the category list.
        var navPanel = new PanelContainer { CustomMinimumSize = new Vector2(NavMinWidth, 0) };
        _navPanel = navPanel;
        var navStyle = new StyleBoxFlat
        {
            BgColor = new Color(1, 1, 1, 0.03f),
            BorderWidthRight = 1,
            BorderColor = new Color(1, 1, 1, 0.08f),
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
        };
        navPanel.AddThemeStyleboxOverride("panel", navStyle);
        hbox.AddChild(navPanel);

        LeftColumn = new VBoxContainer();
        LeftColumn.AddThemeConstantOverride("separation", 8);
        navPanel.AddChild(LeftColumn);

        BuildSearchField();

        _navList = new VBoxContainer();
        _navList.AddThemeConstantOverride("separation", 4);
        LeftColumn.AddChild(_navList);

        // Right: the selected category.
        var pageMargin = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        // Left only: the gap between the category list and the page. Right, top and bottom are the
        // window's standard inset (SLNGWindow); a margin there would stack a second one on it.
        pageMargin.AddThemeConstantOverride("margin_left", 16);
        hbox.AddChild(pageMargin);

        _pageHost = new Control
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        pageMargin.AddChild(_pageHost);

        _noMatch = PrefsStyle.MakeNoMatch(L10n.Tr("ui.preferences.search_no_match"));
        _pageHost.AddChild(_noMatch);

        VisibilityChanged += OnVisibilityChanged;
    }

    /// <summary>Appends a category: a button in the list plus its page. The first category ever added starts
    /// selected; the one the person left open last time replaces it when the window is first shown. Fill the returned
    /// category with sections.</summary>
    public PrefsCategory AddCategory(string id, string titleKey)
    {
        foreach (var existing in _categories)
        {
            if (existing.Category.Id == id) GD.PushError($"[Preferences] category '{id}' exists twice");
        }

        bool isFirst = _categories.Count == 0;
        var category = new PrefsCategory(id, titleKey);

        // Each category lives in its own ScrollContainer. Without one a page taller than the dialog
        // does not clip -- it draws straight over the 3D world outside the window frame, which is
        // what the Graphics tab did as soon as it grew past four controls. Scrolling also means a
        // page can be extended later without anyone having to remember to resize this window.
        var scroll = new ScrollContainer
        {
            Visible = isFirst,
            // ShowNever, not Disabled. Disabled makes the ScrollContainer adopt its content's
            // minimum WIDTH -- it cannot scroll horizontally, so the content has to fit -- and that
            // minimum then propagates up through the margin, the row and the window's own box until
            // the dialog's contents are wider than the dialog. A single long label was enough to do
            // it. ShowNever keeps the horizontal bar hidden without making that demand.
            HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever,
            ClipContents = true,
        };
        scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _pageHost.AddChild(scroll);

        // Inside a container, layout is the container's job -- an anchor preset on the child is
        // ignored at best and fights it at worst. Width comes from the fill flag; height is left to
        // the content, which is what gives the scroll bar something to scroll.
        // The gutter keeps the rows (value texts, the right end of a hint) off the scroll bar.
        var gutter = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        gutter.AddThemeConstantOverride("margin_right", PrefsStyle.PageGutter);
        gutter.AddChild(category);
        scroll.AddChild(gutter);

        var navButton = new Button
        {
            Text = L10n.Tr(titleKey),
            ToggleMode = true,
            ButtonPressed = isFirst,
            FocusMode = Control.FocusModeEnum.None,
            Alignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 32),
        };
        StyleNavButton(navButton);
        navButton.Pressed += () => SelectCategory(id);
        _navList.AddChild(navButton);

        _categories.Add((category, navButton, scroll));
        if (isFirst) _selectedId = id;
        return category;
    }

    /// <summary>Shows a category and remembers it for the next start. An unknown id changes nothing. Choosing a
    /// category here, before the window was first shown, also stops the remembered one from replacing it.</summary>
    public void SelectCategory(string id)
    {
        if (!ShowCategory(id)) return;
        _restored = true;
        PrefsState.SetLastCategory(id);
        if (_searching) ScrollToFirstHitSoon(); // a deliberate choice during a search: land on its first hit
    }

    private bool ShowCategory(string id)
    {
        if (!_categories.Exists(entry => entry.Category.Id == id)) return false;

        foreach (var (category, navButton, scroll) in _categories)
        {
            bool selected = category.Id == id;
            scroll.Visible = selected;
            navButton.ButtonPressed = selected;
        }
        _selectedId = id;
        return true;
    }

    /// <summary>The first time the window is shown, reopens the category the person had open. Reads only: restoring
    /// is not a change, so nothing is written.</summary>
    private void OnVisibilityChanged()
    {
        OnSearchVisibilityChanged();
        if (!Visible || _restored) return;
        _restored = true;
        string? last = PrefsState.LastCategory;
        if (last != null) ShowCategory(last);
    }

    private static void StyleNavButton(Button btn)
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
}
