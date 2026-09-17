using LibreMetaverse;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>Conversion of LibreMetaverse's <c>MediaEntry</c> to the neutral <see cref="MediaFace"/>
/// at the SLNG.Net boundary (AGENTS.md: no LibreMetaverse type crosses a public boundary).
/// Field names verified against the pinned 3.1.3 assembly by reflection, not a newer vendored
/// checkout -- see docs/specs/MVP3-3-shared-media-moap.md.</summary>
public class ObjectMediaConversionTests
{
    [Fact]
    public void NullEntry_MeansNoMediaOnThatFace()
    {
        // The GET response's own convention (llmediadataclient.cpp:942-964): a face with no media
        // is UNDEFINED in the positional array, which LibreMetaverse deserializes as a literal
        // null MediaEntry, not a default-valued one.
        Assert.Null(GridSession.ToMediaFace(null));
    }

    [Fact]
    public void PopulatedEntry_ConvertsEveryField()
    {
        var entry = new MediaEntry
        {
            HomeURL = "https://example.com/home",
            CurrentURL = "https://example.com/current",
            AutoPlay = true,
            AutoLoop = true,
            AutoScale = true,
            AutoZoom = true,
            InteractOnFirstClick = true,
            Controls = MediaControls.Mini,
            Width = 640,
            Height = 480,
            ControlPermissions = LibreMetaverse.MediaPermission.Owner,
            InteractPermissions = LibreMetaverse.MediaPermission.Anyone,
            EnableWhiteList = true,
            WhiteList = new[] { "https://example.com/*" },
            EnableAlternativeImage = true,
        };

        var face = GridSession.ToMediaFace(entry);

        Assert.NotNull(face);
        Assert.Equal("https://example.com/home", face!.Value.HomeUrl);
        Assert.Equal("https://example.com/current", face.Value.CurrentUrl);
        Assert.True(face.Value.AutoPlay);
        Assert.True(face.Value.AutoLoop);
        Assert.True(face.Value.AutoScale);
        Assert.True(face.Value.AutoZoom);
        Assert.True(face.Value.InteractOnFirstClick);
        Assert.Equal(MediaControlStyle.Mini, face.Value.Controls);
        Assert.Equal(640, face.Value.WidthPixels);
        Assert.Equal(480, face.Value.HeightPixels);
        Assert.Equal(SLNG.Core.MediaPermission.Owner, face.Value.ControlPermissions);
        Assert.Equal(SLNG.Core.MediaPermission.Anyone, face.Value.InteractPermissions);
        Assert.True(face.Value.EnableWhiteList);
        Assert.Equal(new[] { "https://example.com/*" }, face.Value.WhiteList);
        Assert.True(face.Value.EnableAlternativeImage);
    }

    [Fact]
    public void MissingUrlsAndWhitelist_ConvertToEmpty_NeverNull()
    {
        var entry = new MediaEntry();

        var face = GridSession.ToMediaFace(entry);

        Assert.NotNull(face);
        Assert.Equal("", face!.Value.HomeUrl);
        Assert.Equal("", face.Value.CurrentUrl);
        Assert.NotNull(face.Value.WhiteList);
    }
}
