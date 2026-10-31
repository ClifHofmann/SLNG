using System;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class MeshFetchUrlTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void CapWithoutQuery_GetsMeshIdAppended()
    {
        var url = MeshFetchUrl.Build(new Uri("https://sim.example/cap/abc"), Id);

        Assert.Equal("https://sim.example/cap/abc?mesh_id=11111111-2222-3333-4444-555555555555", url.ToString());
    }

    [Fact]
    public void CapWithTrailingSlash_KeepsIt_NoDoubleSlash()
    {
        var url = MeshFetchUrl.Build(new Uri("https://sim.example/cap/abc/"), Id);

        Assert.Equal("https://sim.example/cap/abc/?mesh_id=11111111-2222-3333-4444-555555555555", url.ToString());
    }

    [Fact]
    public void CapThatAlreadyHasAQuery_ExtendsItWithAmpersand()
    {
        var url = MeshFetchUrl.Build(new Uri("https://sim.example/cap?x=1"), Id);

        Assert.Equal("https://sim.example/cap?x=1&mesh_id=11111111-2222-3333-4444-555555555555", url.ToString());
    }

    [Theory]
    [InlineData(503, true)]
    [InlineData(502, true)]
    [InlineData(504, true)]
    [InlineData(429, true)]
    [InlineData(408, true)]
    [InlineData(500, true)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(410, false)]
    [InlineData(400, false)]
    public void Transient_SaysWhichStatusesAreWorthAnotherTry(int status, bool expected)
    {
        Assert.Equal(expected, MeshFetchUrl.IsTransient(status));
    }
}
