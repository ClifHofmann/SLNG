using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class BuildInfoTests
{
    [Fact]
    public void Name_is_SLNG()
    {
        Assert.Equal("SLNG", BuildInfo.Name);
    }

    [Fact]
    public void Stage_is_not_empty()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.Stage));
    }
}
