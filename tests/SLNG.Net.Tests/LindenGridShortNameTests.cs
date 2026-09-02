using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-AVATAR-02 — <see cref="GridSession.ParseLindenGridShortName"/> is the piece
/// <c>FetchBakeTextureDataAsync</c> needs to build <c>bake-texture.glb.{grid}.lindenlab.com</c>.
/// </summary>
public class LindenGridShortNameTests
{
    [Theory]
    [InlineData(LoginCredentials.SecondLifeLoginUri, "agni")]
    [InlineData(LoginCredentials.SecondLifeBetaLoginUri, "aditi")]
    public void Parses_the_grid_name_from_a_known_Linden_login_uri(string uri, string expected)
    {
        Assert.Equal(expected, GridSession.ParseLindenGridShortName(uri));
    }

    [Theory]
    [InlineData(LoginCredentials.OpenSimLocalLoginUri)]
    [InlineData("http://hg.osgrid.org/")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a uri at all")]
    public void Returns_null_for_anything_that_is_not_a_Linden_login_host(string? uri)
    {
        Assert.Null(GridSession.ParseLindenGridShortName(uri));
    }
}
