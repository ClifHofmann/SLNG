using System.Collections.Generic;
using Godot;
using SLNG.App.UI.Prefs;
using SLNG.Core.Settings;

namespace SLNG.App.UI;

/// <summary>
/// The search of the Preferences window (FEAT-UI-80): a field on top of the category list that filters in place, the way
/// Firestorm's does (docs/specs/FEAT-UI-80-preferences-search.md). On every keystroke the categories and sections without a
/// hit are hidden, a hit row gets a tinted background, a hit inside a collapsed Advanced opens it, and the view scrolls to
/// the first hit. Clearing the field puts everything back.
///
/// <para><b>Writes nothing.</b> The category the search switches to is not remembered (<c>ShowCategory</c>, not
/// <see cref="SelectCategory"/>), and an Advanced fold-out the search opens is opened through the property, which does not
/// persist -- so closing the window or a crash during a search leaves <c>preferences.cfg</c> exactly as it was.</para>
/// </summary>
public partial class PreferencesWindow
{
    private const string EnglishLocale = "en-US";

    // Distance from the top of the page to a first hit that is scrolled to: room for the section heading above its first row.
    private const int ScrollLead = 40;

    private LineEdit _searchField = null!;
    private Label _noMatch = null!;
    private PrefsSearchIndex? _index;
    private string _builtLocale = EnglishLocale;
    private bool _searching;
    private bool _shown;
    private bool _scrollPending;

    /// <summary>The search field, the first child of <see cref="LeftColumn"/>.</summary>
    public LineEdit SearchField => _searchField;

    /// <summary>What is typed in the search field.</summary>
    public string SearchText => _searchField.Text;

    /// <summary>True while a non-empty query is applied.</summary>
    public bool IsSearching => _searching;

    /// <summary>True while the page area shows the "no setting matches" line instead of a category.</summary>
    public bool SearchFoundNothing => _noMatch.Visible;

    /// <summary>Whether the category's button is in the list (it is not while a search has no hit in the category).</summary>
    public bool IsCategoryListed(string id)
    {
        foreach (var (category, navButton, _) in _categories)
        {
            if (category.Id == id) return navButton.Visible;
        }
        return false;
    }

    /// <summary>Types <paramref name="text"/> into the search field and filters at once, as a keystroke would. An empty text
    /// clears the search.</summary>
    public void Search(string text)
    {
        _searchField.Text = text;
        ApplySearch();
    }

    private void BuildSearchField()
    {
        _searchField = new LineEdit
        {
            PlaceholderText = L10n.Tr("ui.preferences.search_placeholder"),
            ClearButtonEnabled = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _searchField.TextChanged += _ => ApplySearch();
        _searchField.GuiInput += OnSearchFieldInput;
        LeftColumn.AddChild(_searchField);
        LeftColumn.MoveChild(_searchField, 0);
    }

    /// <summary>Esc clears the field; in an empty one it gives the keyboard back to the world.</summary>
    private void OnSearchFieldInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Keycode: Key.Escape }) return;
        if (_searchField.Text.Length > 0) Search("");
        else _searchField.ReleaseFocus();
        _searchField.AcceptEvent();
    }

    /// <summary>Shown: the field takes the keyboard and the index is ready. Hidden: the search ends, so the window never
    /// comes back with categories missing.</summary>
    private void OnSearchVisibilityChanged()
    {
        if (Visible == _shown) return; // a change of an ancestor's visibility is not the window being shown or hidden
        _shown = Visible;
        if (_shown)
        {
            EnsureIndex();
            Callable.From(FocusSearch).CallDeferred();
        }
        else if (_searching)
        {
            Search("");
        }
    }

    private void FocusSearch()
    {
        if (!IsInstanceValid(_searchField) || !_searchField.IsVisibleInTree()) return;
        _searchField.GrabFocus();
        _searchField.SelectAll();
    }

    /// <summary>The texts changed language: the index is rebuilt (at once while a search is on, otherwise when next needed).</summary>
    private void OnSearchLanguageChanged()
    {
        _index = null;
        if (_searching) ApplySearch();
    }

    private PrefsSearchIndex EnsureIndex() => _index ??= new PrefsSearchIndex(Categories, SearchLocales());

    /// <summary>The language now, the one the rows' texts were built in (they stay in it until the windows are reopened) and
    /// English, so that a guide written for the English viewer can be followed in any language.</summary>
    private List<string> SearchLocales()
    {
        var locales = new List<string> { L10n.CurrentLocale };
        if (!locales.Contains(_builtLocale)) locales.Add(_builtLocale);
        if (!locales.Contains(EnglishLocale)) locales.Add(EnglishLocale);
        return locales;
    }

    private void ApplySearch()
    {
        var query = SettingsSearch.Parse(_searchField.Text);
        if (query.IsEmpty)
        {
            EndSearch();
            return;
        }

        var index = EnsureIndex();
        // The list is as wide as its widest button. Hide that button and the whole page would jump sideways on a keystroke,
        // so the width the list has now (every button shown) is kept until the search ends.
        if (!_searching) _navPanel.CustomMinimumSize = new Vector2(_navPanel.GetCombinedMinimumSize().X, 0);
        _searching = true;
        string typed = _searchField.Text.Trim();

        // 1. The rows whose own texts match. A block that filters itself (Keyboard) is asked, and told to filter; it counts as
        //    a hit but is never tinted.
        var hits = new HashSet<PrefsRow>();
        var selfFiltering = new HashSet<PrefsRow>();
        foreach (var (category, _, _) in _categories)
        {
            foreach (var section in category.Sections)
            {
                foreach (var row in section.Rows)
                {
                    if (row.SearchTarget is { } target)
                    {
                        if (row.Visible && target.MatchesSearch(query))
                        {
                            selfFiltering.Add(row);
                            target.ShowSearch(typed);
                        }
                        else
                        {
                            target.EndSearch();
                        }
                    }
                    else if (index.Matches(row, query))
                    {
                        hits.Add(row);
                    }
                }
            }
        }

        // 2. A hit on a row its switch has disabled (shadow distance with shadows off) shows the switch too, so it is clear
        //    what to turn on. Followed up the chain; the limit is only a guard against a cycle.
        var tinted = new HashSet<PrefsRow>(hits);
        foreach (var row in hits)
        {
            var current = row;
            for (int depth = 0; depth < 8 && !current.IsEnabled && current.Controller is { } controller; depth++)
            {
                tinted.Add(controller);
                current = controller;
            }
        }

        // 3. Categories, sections and Advanced fold-outs follow. A title that matches shows the whole section (or category);
        //    its rows are tinted only for their own hits.
        string? firstListed = null;
        bool selectedListed = false;
        foreach (var (category, navButton, _) in _categories)
        {
            bool categoryWhole = index.Matches(category, query);
            bool categoryHit = categoryWhole;
            foreach (var section in category.Sections)
            {
                bool sectionHit = categoryWhole || index.Matches(section, query);
                bool advancedHit = false;
                foreach (var row in section.Rows)
                {
                    bool tintedRow = tinted.Contains(row);
                    row.SetHighlighted(tintedRow);
                    if (!tintedRow && !selfFiltering.Contains(row)) continue;
                    sectionHit = true;
                    if (row.IsAdvanced) advancedHit = true;
                }
                section.Visible = sectionHit;
                if (sectionHit && advancedHit) section.OpenAdvancedForSearch();
                else section.RestoreAdvancedAfterSearch();
                categoryHit |= sectionHit;
            }
            navButton.Visible = categoryHit;
            if (!categoryHit) continue;
            firstListed ??= category.Id;
            if (category.Id == _selectedId) selectedListed = true;
        }

        // 4. The page: the selected category if it has a hit, else the first that has one (as the reference viewer's
        //    selectFirstTab), else the "no setting matches" line.
        _noMatch.Visible = firstListed == null;
        if (firstListed == null)
        {
            foreach (var (_, _, scroll) in _categories) scroll.Visible = false;
            return;
        }
        ShowCategory(selectedListed && _selectedId != null ? _selectedId : firstListed);
        ScrollToFirstHitSoon();
    }

    /// <summary>Puts the window back as it was before the search: every button, section and tint, the Advanced fold-outs the
    /// search opened, the Keyboard filter it set. The selected category stays, and so does the remembered one.</summary>
    private void EndSearch()
    {
        if (!_searching) return;
        _searching = false;

        foreach (var (category, navButton, _) in _categories)
        {
            navButton.Visible = true;
            foreach (var section in category.Sections)
            {
                section.Visible = true;
                section.RestoreAdvancedAfterSearch();
                foreach (var row in section.Rows)
                {
                    row.SetHighlighted(false);
                    row.SearchTarget?.EndSearch();
                }
            }
        }
        _noMatch.Visible = false;
        _navPanel.CustomMinimumSize = new Vector2(NavMinWidth, 0);
        if (_selectedId != null) ShowCategory(_selectedId);
    }

    /// <summary>Scrolls the shown category to its first hit once the layout has settled. A container sorts its children
    /// deferred, and what that changes makes the container above it sort a step later, so the scroll waits two frames; before
    /// that the positions of the rows are the old ones.</summary>
    private void ScrollToFirstHitSoon()
    {
        if (_scrollPending || !IsInsideTree()) return;
        _scrollPending = true;
        var tree = GetTree();
        tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(() =>
        {
            if (!IsInstanceValid(this) || !IsInsideTree()) { _scrollPending = false; return; }
            GetTree().Connect(SceneTree.SignalName.ProcessFrame, Callable.From(ScrollToFirstHit), (uint)GodotObject.ConnectFlags.OneShot);
        }), (uint)GodotObject.ConnectFlags.OneShot);
    }

    private void ScrollToFirstHit()
    {
        _scrollPending = false;
        if (!IsInstanceValid(this) || !_searching || _selectedId == null) return;

        foreach (var (category, _, scroll) in _categories)
        {
            if (category.Id != _selectedId) continue;
            PrefsRow? first = null;
            foreach (var section in category.Sections)
            {
                if (!section.Visible) continue;
                foreach (var row in section.Rows)
                {
                    if (!row.IsHighlighted) continue;
                    first = row;
                    break;
                }
                if (first != null) break;
            }

            // No tinted row: the category or a section matched by its title, and the page starts at the first shown section.
            if (first == null || scroll.GetChildCount() == 0 || scroll.GetChild(0) is not Control content)
            {
                scroll.ScrollVertical = 0;
                return;
            }
            float offset = first.GlobalPosition.Y - content.GlobalPosition.Y;
            scroll.ScrollVertical = Mathf.Max(0, (int)(offset - ScrollLead));
            return;
        }
    }
}
