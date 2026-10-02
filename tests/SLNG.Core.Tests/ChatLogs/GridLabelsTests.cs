using SLNG.Core.ChatLogs;
using Xunit;

namespace SLNG.Core.Tests.ChatLogs;

// FEAT-UI-41. The folder suffix is the grid's LABEL (gridname), found WITHOUT any Firestorm file: a short
// built-in table, the grid's own get_grid_info, the host.
public sealed class GridLabelsTests
{
    [Theory]
    [InlineData("https://login.agni.lindenlab.com/cgi-bin/login.cgi", "Second Life")]
    [InlineData("https://login.aditi.lindenlab.com/cgi-bin/login.cgi", "Second Life Beta")]
    [InlineData("http://hg.osgrid.org/", "OSGrid")]                  // SLNG's own OSGrid entry; the grid answers the same
    [InlineData("http://login.osgrid.org", "OSGrid")]
    [InlineData("hg.osgrid.org:80", "OSGrid")]
    [InlineData("http://127.0.0.1:9000/", "localhost")]              // SLNG says 127.0.0.1, the label is localhost
    [InlineData("http://localhost:9000", "localhost")]
    [InlineData("http://localhost:9001/", null)]                     // another port is another grid: ask it
    [InlineData("http://www.alifevirtual.com:8002/", null)]          // not built in: its get_grid_info says "Alife Virtual"
    [InlineData("http://unknown.example/", null)]
    public void A_few_grids_have_a_label_without_asking_anybody(string uri, string? label)
    {
        Assert.Equal(label, GridLabels.BuiltInLabel(uri));
        Assert.Equal(label is null, GridLabels.NeedsProbe(uri));
    }

    [Fact]
    public void Only_the_linden_grids_are_linden()
    {
        Assert.True(GridLabels.IsLindenGrid("https://login.agni.lindenlab.com/cgi-bin/login.cgi"));
        Assert.True(GridLabels.IsLindenGrid("https://login.aditi.lindenlab.com/cgi-bin/login.cgi"));
        Assert.False(GridLabels.IsLindenGrid("http://hg.osgrid.org/"));
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
    public void The_real_alife_virtual_answer_gives_the_label_not_the_nick()
    {
        // Its nick is "AV"; Firestorm's folder for it is cilian_dupont.alife_virtual.
        const string answer = "<gridinfo><platform>OpenSim</platform><login>http://www.alifevirtual.com:8002/</login>" +
                              "<gridname>Alife Virtual</gridname><gridnick>AV</gridnick></gridinfo>";

        Assert.Equal("Alife Virtual", GridLabels.ParseGridInfoName(answer));
        Assert.Equal("cilian_dupont.alife_virtual",
            FirestormLogLayout.AccountFolderName("Cilian", "Dupont", GridLabels.Resolve("http://www.alifevirtual.com:8002", GridLabels.ParseGridInfoName(answer))));
    }

    [Fact]
    public void The_label_comes_from_the_built_in_table_then_the_grid_then_the_host()
    {
        Assert.Equal("Second Life", GridLabels.Resolve("https://login.agni.lindenlab.com/cgi-bin/login.cgi", "ignored"));
        Assert.Equal("OSGrid", GridLabels.Resolve("http://hg.osgrid.org/", "Something Else"));
        Assert.Equal("Alife Virtual", GridLabels.Resolve("http://www.alifevirtual.com:8002/", " Alife Virtual "));
        Assert.Equal("grid.example:8002", GridLabels.Resolve("http://grid.example:8002/", null));
        Assert.Equal("grid.example", GridLabels.Resolve("http://grid.example/", ""));   // a default port is not part of it
        Assert.Equal("unknown", GridLabels.Resolve("", null));
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
