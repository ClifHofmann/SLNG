using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-UI-65: which friends the list shows and in what order -- the filter, "only online", and the online-first sort.
/// </summary>
public class FriendListViewTests
{
    private static FriendEntry F(string login, bool online) => new(Guid.NewGuid(), login, online);

    // The list shows a Display Name where there is one; the tests give two friends one.
    private static string Shown(FriendEntry f) => f.Name switch
    {
        "anna.resident" => "Zora",
        "ben.builder" => "alpha",
        _ => f.Name,
    };

    private static readonly FriendEntry Anna = F("anna.resident", online: false);   // shown "Zora"
    private static readonly FriendEntry Ben = F("ben.builder", online: true);       // shown "alpha"
    private static readonly FriendEntry Cleo = F("cleo.scripter", online: true);
    private static readonly FriendEntry Dan = F("Dan.Dancer", online: false);

    private static readonly FriendEntry[] All = { Anna, Ben, Cleo, Dan };

    [Fact]
    public void WithoutAnyFilter_EveryoneIsShown_OnlineFirst_ThenByShownName()
    {
        var shown = FriendListView.Select(All, Shown, "", onlyOnline: false);

        // online: alpha (Ben), cleo.scripter; offline: Dan.Dancer, Zora (Anna) -- sorted by what is SHOWN, ignoring case
        Assert.Equal(new[] { Ben, Cleo, Dan, Anna }, shown);
    }

    [Fact]
    public void TheSortUsesTheShownName_NotTheLoginName()
    {
        var shown = FriendListView.Select(new[] { Anna, Dan }, Shown, null, onlyOnline: false);

        Assert.Equal(new[] { Dan, Anna }, shown); // "Dan.Dancer" before "Zora", though "anna" < "dan" by login
    }

    [Fact]
    public void TheFilterMatchesTheShownName_IgnoringCase()
    {
        Assert.Equal(new[] { Anna }, FriendListView.Select(All, Shown, "ZOR", false));
    }

    [Fact]
    public void TheFilterAlsoMatchesTheLoginName_BehindADisplayName()
    {
        // "anna" is nowhere in the shown name "Zora", only in the login name.
        Assert.Equal(new[] { Anna }, FriendListView.Select(All, Shown, "anna", false));
    }

    [Fact]
    public void TheFilterTextIsTrimmed_AndBlankMatchesEveryone()
    {
        Assert.Equal(new[] { Cleo }, FriendListView.Select(All, Shown, "  cleo  ", false));
        Assert.Equal(4, FriendListView.Select(All, Shown, "   ", false).Count);
        Assert.Equal(4, FriendListView.Select(All, Shown, null, false).Count);
    }

    [Fact]
    public void OnlyOnline_HidesTheOffline()
    {
        Assert.Equal(new[] { Ben, Cleo }, FriendListView.Select(All, Shown, "", onlyOnline: true));
    }

    [Fact]
    public void OnlyOnline_CombinesWithTheFilter()
    {
        Assert.Equal(new[] { Cleo }, FriendListView.Select(All, Shown, "cle", onlyOnline: true));
        Assert.Empty(FriendListView.Select(All, Shown, "zora", onlyOnline: true)); // matches, but offline
    }

    [Fact]
    public void NobodyMatching_GivesAnEmptyList()
    {
        Assert.Empty(FriendListView.Select(All, Shown, "nobody", false));
        Assert.Empty(FriendListView.Select(Array.Empty<FriendEntry>(), Shown, "", false));
    }

    [Fact]
    public void ANameThatIsEmpty_DoesNotBreakTheMatch()
    {
        var unnamed = new FriendEntry(Guid.NewGuid(), "", true); // the grid has not told us the name yet
        var shown = FriendListView.Select(new[] { unnamed, Cleo }, f => f.Name.Length == 0 ? f.Id.ToString() : f.Name, "cleo", false);

        Assert.Equal(new[] { Cleo }, shown);
    }
}
