using LibreMetaverse.StructuredData;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-AVATAR-03 — the SSB rebake nudge is a direct <c>{ "cof_version": N }</c> POST to the
/// <c>UpdateAvatarAppearance</c> cap (mirroring <c>LLAppearanceMgr::serverAppearanceUpdateCoro</c>:
/// <c>postData["cof_version"] = cofVersion</c>), NOT LibreMetaverse's <c>RequestSetAppearance</c>,
/// which reconciles the worn set and drops attachments on a rate-limited grid.
/// </summary>
public class ServerAppearanceUpdateTests
{
    [Fact]
    public void Body_is_a_map_with_cof_version_as_an_integer()
    {
        var body = GridSession.BuildServerAppearanceUpdate(42);

        Assert.True(body.ContainsKey("cof_version"));
        Assert.Equal(OSDType.Integer, body["cof_version"].Type);
        Assert.Equal(42, body["cof_version"].AsInteger());
    }

    [Fact]
    public void Body_carries_only_cof_version()
        => Assert.Single(GridSession.BuildServerAppearanceUpdate(1).Keys);

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(1234)]
    public void Round_trips_through_llsd_xml(int version)
    {
        var xml = OSDParser.SerializeLLSDXmlString(GridSession.BuildServerAppearanceUpdate(version));
        var back = (OSDMap)OSDParser.DeserializeLLSDXml(xml);

        Assert.Equal(version, back["cof_version"].AsInteger());
    }
}
