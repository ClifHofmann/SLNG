using SLNG.Core.ChatLogs;
using Xunit;

namespace SLNG.Core.Tests.ChatLogs;

// FEAT-UI-41. The folder suffix is the grid's LABEL (gridname). The XML is the shape of the real
// user_settings/grids.user.xml, cut down to three grids.
public sealed class GridLabelsTests
{
    private const string GridList = """
        <llsd>
          <map>
            <key>login.osgrid.org</key>
            <map>
              <key>gatekeeper</key><string>hg.osgrid.org</string>
              <key>gridname</key><string>OSGrid</string>
              <key>gridnick</key><string>osgrid</string>
              <key>loginuri</key><array><string>http://login.osgrid.org</string></array>
              <key>name</key><string>login.osgrid.org</string>
            </map>
            <key>localhost:9000</key>
            <map>
              <key>gridname</key><string>localhost</string>
              <key>gridnick</key><string>localhost</string>
              <key>loginuri</key><array><string>http://localhost:9000</string></array>
              <key>name</key><string>localhost:9000</string>
            </map>
            <key>www.alifevirtual.com:8002</key>
            <map>
              <key>gridname</key><string>Alife Virtual</string>
              <key>gridnick</key><string>AV</string>
              <key>loginuri</key><array><string>http://www.alifevirtual.com:8002/</string></array>
            </map>
          </map>
        </llsd>
        """;

    [Theory]
    [InlineData("https://login.agni.lindenlab.com/cgi-bin/login.cgi", "Second Life")]
    [InlineData("https://login.aditi.lindenlab.com/cgi-bin/login.cgi", "Second Life Beta")]
    [InlineData("http://hg.osgrid.org/", null)]
    public void The_two_linden_grids_have_their_firestorm_labels(string uri, string? label)
    {
        Assert.Equal(label, GridLabels.LindenLabel(uri));
    }

    [Theory]
    [InlineData("http://hg.osgrid.org/", "OSGrid")]                       // SLNG's host; matched by the gatekeeper
    [InlineData("http://login.osgrid.org", "OSGrid")]                     // Firestorm's own login URI
    [InlineData("hg.osgrid.org:80", "OSGrid")]
    [InlineData("http://127.0.0.1:9000/", "localhost")]                   // SLNG says 127.0.0.1, Firestorm localhost
    [InlineData("http://localhost:9000", "localhost")]
    [InlineData("http://www.alifevirtual.com:8002/", "Alife Virtual")]    // the label, not the nick "AV"
    [InlineData("http://localhost:9001/", null)]                          // another port is another grid
    [InlineData("http://unknown.example/", null)]
    public void A_grid_in_firestorms_own_list_gets_the_label_it_stored(string uri, string? label)
    {
        Assert.Equal(label, GridLabels.FindInGridLists(uri, new[] { GridList }));
    }

    [Fact]
    public void A_broken_or_missing_list_finds_nothing_and_does_not_throw()
    {
        Assert.Null(GridLabels.FindInGridLists("http://hg.osgrid.org/", new string?[] { null, "", "<llsd><map>" }));
    }

    [Fact]
    public void The_real_osgrid_grid_info_answer_names_the_grid()
    {
        const string answer = "<gridinfo><platform>OpenSim</platform><login>http://login.osgrid.org/</login>" +
                              "<gridname>OSGrid</gridname><gridnick>osgrid</gridnick><welcome>https://www.osgrid.org/splash/</welcome></gridinfo>";

        Assert.Equal("OSGrid", GridLabels.ParseGridInfoName(answer));
        Assert.Null(GridLabels.ParseGridInfoName("<gridinfo><gridnick>x</gridnick></gridinfo>"));
        Assert.Null(GridLabels.ParseGridInfoName("not xml"));
        Assert.Null(GridLabels.ParseGridInfoName(null));
    }

    [Fact]
    public void The_label_comes_from_linden_then_the_list_then_the_grid_then_the_host()
    {
        var lists = new[] { GridList };

        Assert.Equal("Second Life", GridLabels.Resolve("https://login.agni.lindenlab.com/cgi-bin/login.cgi", lists, "ignored"));
        Assert.Equal("OSGrid", GridLabels.Resolve("http://hg.osgrid.org/", lists, "Something Else"));
        Assert.Equal("Reported Name", GridLabels.Resolve("http://grid.example:8002/", lists, " Reported Name "));
        Assert.Equal("grid.example:8002", GridLabels.Resolve("http://grid.example:8002/", lists, null));
        Assert.Equal("grid.example", GridLabels.Resolve("http://grid.example/", lists, ""));        // a default port is not part of it
        Assert.Equal("unknown", GridLabels.Resolve("", lists, null));
    }

    [Fact]
    public void Only_a_grid_nobody_knows_needs_the_network()
    {
        var lists = new[] { GridList };

        Assert.False(GridLabels.NeedsProbe("https://login.agni.lindenlab.com/cgi-bin/login.cgi", lists));
        Assert.False(GridLabels.NeedsProbe("http://hg.osgrid.org/", lists));
        Assert.True(GridLabels.NeedsProbe("http://grid.example:8002/", lists));
    }

    [Theory]
    [InlineData("http://grid.example:8002/", "http://grid.example:8002/get_grid_info")]
    [InlineData("grid.example:8002", "http://grid.example:8002/get_grid_info")]
    [InlineData("http://hg.osgrid.org", "http://hg.osgrid.org/get_grid_info")]
    [InlineData("http://grid.example/login/", "http://grid.example/login/get_grid_info")]
    public void The_grid_info_address_is_the_login_address_with_get_grid_info_appended(string login, string expected)
    {
        Assert.Equal(expected, GridLabels.GridInfoUri(login)?.ToString());
    }

    [Fact]
    public void A_login_address_that_is_not_http_has_no_grid_info_address()
    {
        Assert.Null(GridLabels.GridInfoUri(""));
        Assert.Null(GridLabels.GridInfoUri("ftp://grid.example/"));
    }
}
