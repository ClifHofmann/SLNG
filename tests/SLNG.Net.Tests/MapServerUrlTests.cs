using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>FEAT-UI-39 — <see cref="GridSession.ChooseMapServerUrl"/> mirrors the reference viewer's
/// login handling (llstartup.cpp:4044-4056): the grid's own <c>map-server-url</c> wins, otherwise
/// the viewer's default. The default only makes sense on a Linden grid, so any other grid without
/// an answer gets none and falls back to the region's map image asset.</summary>
public class MapServerUrlTests
{
    [Fact]
    public void The_grids_answer_wins_on_any_grid()
    {
        Assert.Equal("https://tiles.example/", GridSession.ChooseMapServerUrl("https://tiles.example/", isLindenGrid: true));
        Assert.Equal("http://127.0.0.1:9000/", GridSession.ChooseMapServerUrl("http://127.0.0.1:9000/", isLindenGrid: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_linden_grid_without_an_answer_gets_the_viewer_default(string? fromLogin)
    {
        Assert.Equal(GridSession.DefaultLindenMapServerUrl, GridSession.ChooseMapServerUrl(fromLogin, isLindenGrid: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Another_grid_without_an_answer_gets_none(string? fromLogin)
    {
        Assert.Equal("", GridSession.ChooseMapServerUrl(fromLogin, isLindenGrid: false));
    }

    [Fact]
    public void The_default_is_an_https_url_with_a_trailing_slash()
    {
        Assert.StartsWith("https://", GridSession.DefaultLindenMapServerUrl);
        Assert.EndsWith("/", GridSession.DefaultLindenMapServerUrl);
    }
}
