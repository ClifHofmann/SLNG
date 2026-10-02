using SLNG.Core.ChatLogs;
using Xunit;

namespace SLNG.Core.Tests.ChatLogs;

// FEAT-UI-41. The chat-log folder is chosen per saved account. The key is the grid plus the account
// name (what a login profile is made of); the map holds the rules, the app only persists the pairs.
public sealed class ChatLogFolderMapTests
{
    private const string Agni = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";
    private const string OsGrid = "http://hg.osgrid.org/";

    [Fact]
    public void The_same_account_name_on_two_grids_has_two_keys()
    {
        Assert.NotEqual(ChatLogAccountKey.Of(Agni, "Clifton", "Howlett"), ChatLogAccountKey.Of(OsGrid, "Clifton", "Howlett"));
    }

    [Fact]
    public void Two_accounts_on_one_grid_have_two_keys()
    {
        Assert.NotEqual(ChatLogAccountKey.Of(OsGrid, "Clifton", "Howlett"), ChatLogAccountKey.Of(OsGrid, "Reamon", "Bullmer"));
    }

    [Fact]
    public void Spellings_of_one_login_are_one_key()
    {
        Assert.Equal(ChatLogAccountKey.Of("http://hg.osgrid.org/", "Clifton", "Howlett"),
                     ChatLogAccountKey.Of("HG.OSGrid.org:80", " clifton ", "HOWLETT"));
    }

    [Theory]
    [InlineData("https://login.agni.lindenlab.com/cgi-bin/login.cgi", "Clifton", "Howlett")]
    [InlineData("http://hg.osgrid.org/", "Reamon", "Bullmer")]
    [InlineData("http://127.0.0.1:9000/", "Test", "User")]
    [InlineData("http://grid.example:8002/", "Weird name", "With=Chars; [and] spaces")]
    [InlineData("", "", "")]
    public void A_key_is_a_plain_string_that_is_safe_as_a_config_key(string uri, string first, string last)
    {
        string key = ChatLogAccountKey.Of(uri, first, last);

        Assert.NotEmpty(key);
        Assert.All(key, c => Assert.True(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '~' or '_' or '-', $"'{c}' in {key}"));
    }

    [Fact]
    public void A_chosen_folder_round_trips_through_the_stored_pairs()
    {
        var map = new ChatLogFolderMap();
        string key = ChatLogAccountKey.Of(OsGrid, "Clifton", "Howlett");
        map.Set(key, @"C:\Users\x\OneDrive\Firestorm ");

        var reloaded = ChatLogFolderMap.FromPairs(map.Entries);

        Assert.Equal(@"C:\Users\x\OneDrive\Firestorm", reloaded.Get(key));
        Assert.Equal(@"C:\Users\x\OneDrive\Firestorm", reloaded.Get(ChatLogAccountKey.Of("HG.OSGrid.org:80", "clifton", "howlett")));
    }

    [Fact]
    public void Two_accounts_on_two_grids_keep_separate_folders()
    {
        var map = new ChatLogFolderMap();
        string sl = ChatLogAccountKey.Of(Agni, "Clifton", "Howlett");
        string os = ChatLogAccountKey.Of(OsGrid, "Clifton", "Howlett");

        map.Set(sl, @"D:\logs\sl");
        map.Set(os, @"D:\logs\os");

        Assert.Equal(@"D:\logs\sl", map.Get(sl));
        Assert.Equal(@"D:\logs\os", map.Get(os));
        Assert.Null(map.Get(ChatLogAccountKey.Of(OsGrid, "Reamon", "Bullmer")));
    }

    [Fact]
    public void Clearing_one_account_falls_back_to_the_default_and_leaves_the_others()
    {
        var map = new ChatLogFolderMap();
        string a = ChatLogAccountKey.Of(OsGrid, "Clifton", "Howlett");
        string b = ChatLogAccountKey.Of(OsGrid, "Reamon", "Bullmer");
        map.Set(a, @"D:\a");
        map.Set(b, @"D:\b");

        map.Clear(a);

        Assert.Null(map.Get(a));
        Assert.Equal(@"D:\b", map.Get(b));
        // No folder chosen: the locator falls back to SLNG's own.
        Assert.StartsWith("own", ChatLogLocator.Resolve(map.Get(a), "own", "x", ImLogNameStyle.Legacy, "Grid").Directory);
    }

    [Fact]
    public void Setting_a_blank_folder_clears_it_and_blank_stored_pairs_are_dropped()
    {
        var map = new ChatLogFolderMap();
        map.Set("k", @"D:\a");
        map.Set("k", "  ");
        Assert.Null(map.Get("k"));

        Assert.Empty(ChatLogFolderMap.FromPairs(new[] { new KeyValuePair<string, string>("k", "") }).Entries);
    }
}
