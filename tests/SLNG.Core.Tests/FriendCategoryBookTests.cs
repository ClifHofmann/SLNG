using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// The Friends list's own filing (FEAT-UI-65): which friend is in which category and which categories are folded.
/// </summary>
public class FriendCategoryBookTests
{
    private static readonly FriendEntry Anna = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Anna", true);
    private static readonly FriendEntry Ben = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Ben", false);
    private static readonly FriendEntry Cleo = new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "Cleo", true);

    private static readonly FriendEntry[] All = { Anna, Ben, Cleo };

    [Fact]
    public void Add_TrimsAndKeepsOrder()
    {
        var book = new FriendCategoryBook();
        Assert.Equal("Family", book.Add("  Family "));
        Assert.Equal("Work", book.Add("Work"));
        Assert.Equal(new[] { "Family", "Work" }, book.Categories);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Add_RefusesABlankName(string? name)
    {
        var book = new FriendCategoryBook();
        Assert.Null(book.Add(name));
        Assert.Empty(book.Categories);
    }

    [Fact]
    public void Add_RefusesADuplicateIgnoringCase()
    {
        var book = new FriendCategoryBook();
        book.Add("Family");
        Assert.Null(book.Add("family"));
        Assert.Single(book.Categories);
    }

    [Fact]
    public void Add_CutsALongName()
    {
        var book = new FriendCategoryBook();
        string stored = book.Add(new string('x', FriendCategoryBook.MaxNameLength + 10))!;
        Assert.Equal(FriendCategoryBook.MaxNameLength, stored.Length);
    }

    [Fact]
    public void Assign_PutsAFriendInOneCategoryOnly()
    {
        var book = new FriendCategoryBook();
        book.Add("Family");
        book.Add("Work");

        Assert.True(book.Assign(Anna.Id, "Family"));
        Assert.True(book.Assign(Anna.Id, "work")); // moves, ignoring case; stored under the real name

        Assert.Equal("Work", book.CategoryOf(Anna.Id));
        Assert.Null(book.CategoryOf(Ben.Id));
    }

    [Fact]
    public void Assign_ToNull_TakesAFriendOutOfTheirCategory()
    {
        var book = new FriendCategoryBook();
        book.Add("Family");
        book.Assign(Anna.Id, "Family");

        Assert.True(book.Assign(Anna.Id, null));

        Assert.Null(book.CategoryOf(Anna.Id));
    }

    [Fact]
    public void Assign_ToAMissingCategory_ChangesNothing()
    {
        var book = new FriendCategoryBook();
        book.Add("Family");
        book.Assign(Anna.Id, "Family");

        Assert.False(book.Assign(Anna.Id, "Nope"));

        Assert.Equal("Family", book.CategoryOf(Anna.Id));
    }

    [Fact]
    public void Rename_CarriesFriendsAndFoldStateAlong()
    {
        var book = new FriendCategoryBook();
        book.Add("Family");
        book.Assign(Anna.Id, "Family");
        book.SetCollapsed("Family", true);

        Assert.True(book.Rename("Family", "Relatives"));

        Assert.Equal(new[] { "Relatives" }, book.Categories);
        Assert.Equal("Relatives", book.CategoryOf(Anna.Id));
        Assert.True(book.IsCollapsed("Relatives"));
        Assert.False(book.IsCollapsed("Family"));
    }

    [Fact]
    public void Rename_RefusesAnotherCategorysName_ButAllowsAChangeOfCase()
    {
        var book = new FriendCategoryBook();
        book.Add("Family");
        book.Add("Work");

        Assert.False(book.Rename("Family", "WORK"));
        Assert.True(book.Rename("Family", "FAMILY"));
        Assert.Equal(new[] { "FAMILY", "Work" }, book.Categories);
    }

    [Fact]
    public void Remove_SendsItsFriendsBackToNoCategory()
    {
        var book = new FriendCategoryBook();
        book.Add("Family");
        book.Add("Work");
        book.Assign(Anna.Id, "Family");
        book.Assign(Ben.Id, "Work");
        book.SetCollapsed("Family", true);

        Assert.True(book.Remove("Family"));

        Assert.Equal(new[] { "Work" }, book.Categories);
        Assert.Null(book.CategoryOf(Anna.Id));
        Assert.Equal("Work", book.CategoryOf(Ben.Id));
        Assert.Empty(book.CollapsedCategories);
    }

    [Fact]
    public void Group_WithoutCategories_IsOneOpenBlockAsBeforeCategoriesExisted()
    {
        var sections = new FriendCategoryBook().Group(All);

        var only = Assert.Single(sections);
        Assert.Null(only.Category);
        Assert.False(only.Collapsed);
        Assert.Equal(All, only.Friends);
    }

    [Fact]
    public void Group_ListsCategoriesInOrder_ThenTheUncategorised_KeepingTheCallersOrder()
    {
        var book = new FriendCategoryBook();
        book.Add("Work");
        book.Add("Family");
        book.Assign(Cleo.Id, "Family");
        book.Assign(Anna.Id, "Family");
        book.Assign(Ben.Id, "Work");

        var sections = book.Group(All);

        Assert.Equal(new string?[] { "Work", "Family" }, sections.Select(s => s.Category));
        Assert.Equal(new[] { Ben }, sections[0].Friends);
        Assert.Equal(new[] { Anna, Cleo }, sections[1].Friends); // the order handed in, not the order assigned
    }

    [Fact]
    public void Group_ShowsTheUncategorisedBlockOnlyWhenItHasSomeone()
    {
        var book = new FriendCategoryBook();
        book.Add("Family");
        book.Assign(Anna.Id, "Family");
        book.Assign(Ben.Id, "Family");

        Assert.DoesNotContain(book.Group(new[] { Anna, Ben }), s => s.Category == null);

        var withRest = book.Group(All);
        Assert.Equal(new string?[] { "Family", null }, withRest.Select(s => s.Category));
        Assert.Equal(new[] { Cleo }, withRest[1].Friends);
    }

    [Fact]
    public void Group_ShowsAnEmptyCategory_SoItCanStillBeFilledOrDeleted()
    {
        var book = new FriendCategoryBook();
        book.Add("Family");

        var sections = book.Group(All);

        Assert.Equal("Family", sections[0].Category);
        Assert.Empty(sections[0].Friends);
        Assert.Equal(All, sections[1].Friends);
    }

    [Fact]
    public void Group_HideEmpty_DropsBlocksWithoutAHit()
    {
        var book = new FriendCategoryBook();
        book.Add("Family");
        book.Add("Work");
        book.Assign(Anna.Id, "Family");

        var sections = book.Group(new[] { Anna }, hideEmpty: true);

        var only = Assert.Single(sections);
        Assert.Equal("Family", only.Category);
    }

    [Fact]
    public void Group_ReportsFoldState_UnlessToldToIgnoreIt()
    {
        var book = new FriendCategoryBook();
        book.Add("Family");
        book.SetCollapsed("Family", true);
        book.UncategorizedCollapsed = true;

        var folded = book.Group(All);
        Assert.All(folded, s => Assert.True(s.Collapsed));

        var forFilter = book.Group(All, ignoreCollapsed: true);
        Assert.All(forFilter, s => Assert.False(s.Collapsed));
    }

    [Fact]
    public void SetCollapsed_OnAMissingCategory_IsRefused()
    {
        Assert.False(new FriendCategoryBook().SetCollapsed("Nope", true));
    }

    [Fact]
    public void StoredState_RoundTripsThroughTheConstructor()
    {
        var book = new FriendCategoryBook();
        book.Add("Family");
        book.Add("Work");
        book.Assign(Anna.Id, "Family");
        book.Assign(Ben.Id, "Work");
        book.SetCollapsed("Work", true);
        book.UncategorizedCollapsed = true;

        var copy = new FriendCategoryBook(book.Categories, book.Assignments, book.CollapsedCategories, book.UncategorizedCollapsed);

        Assert.Equal(book.Categories, copy.Categories);
        Assert.Equal("Family", copy.CategoryOf(Anna.Id));
        Assert.Equal("Work", copy.CategoryOf(Ben.Id));
        Assert.True(copy.IsCollapsed("Work"));
        Assert.False(copy.IsCollapsed("Family"));
        Assert.True(copy.UncategorizedCollapsed);
    }

    [Fact]
    public void Constructor_IgnoresWhatDoesNotFit()
    {
        var book = new FriendCategoryBook(
            categories: new[] { "Family", "  ", "family", "Work" },
            assignments: new[]
            {
                new KeyValuePair<Guid, string>(Anna.Id, "Family"),
                new KeyValuePair<Guid, string>(Ben.Id, "Deleted long ago"),
            },
            collapsed: new[] { "Work", "Deleted long ago" },
            uncategorizedCollapsed: false);

        Assert.Equal(new[] { "Family", "Work" }, book.Categories);
        Assert.Equal("Family", book.CategoryOf(Anna.Id));
        Assert.Null(book.CategoryOf(Ben.Id));
        Assert.Equal(new[] { "Work" }, book.CollapsedCategories);
    }

    [Fact]
    public void Json_RoundTripsEverything()
    {
        var book = new FriendCategoryBook();
        book.Add("Family");
        book.Add("Work");
        book.Assign(Anna.Id, "Family");
        book.SetCollapsed("Work", true);
        book.UncategorizedCollapsed = true;

        var copy = FriendCategoryBook.FromJson(book.ToJson());

        Assert.Equal(new[] { "Family", "Work" }, copy.Categories);
        Assert.Equal("Family", copy.CategoryOf(Anna.Id));
        Assert.True(copy.IsCollapsed("Work"));
        Assert.True(copy.UncategorizedCollapsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"Categories\": 7}")]
    [InlineData("null")]
    public void FromJson_GivesAnEmptyBookForAnythingUnreadable(string? json)
    {
        var book = FriendCategoryBook.FromJson(json);

        Assert.Empty(book.Categories);
        Assert.Empty(book.Assignments);
    }
}
