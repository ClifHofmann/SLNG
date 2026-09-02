using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// BUG-RENDER-02 — every tree on the sim rendered as a hazy, water-like plane because nothing
/// recognised a Tree/NewTree/Grass pcode as needing different handling from a normal prim. These
/// pin the values <see cref="PrimPCode.IsFoliage"/> depends on, verified by reflection against the
/// pinned LibreMetaverse 3.1.3 assembly's own <c>PCode</c> enum (not the vendored source, not the
/// wire spec from memory): <c>Tree</c> = 255, <c>NewTree</c> = 111, <c>Grass</c> = 95.
/// </summary>
public class PrimPCodeTests
{
    [Theory]
    [InlineData(PrimPCode.Tree)]
    [InlineData(PrimPCode.NewTree)]
    [InlineData(PrimPCode.Grass)]
    public void Recognises_every_foliage_pcode(byte pcode)
    {
        Assert.True(PrimPCode.IsFoliage(pcode));
    }

    [Theory]
    [InlineData((byte)0)]   // primitive (a normal box/sphere/etc.)
    [InlineData((byte)9)]   // avatar
    [InlineData((byte)13)]  // sculpt (legacy pcode value some grids still send)
    public void Does_not_flag_ordinary_pcodes(byte pcode)
    {
        Assert.False(PrimPCode.IsFoliage(pcode));
    }

    [Fact]
    public void Constants_match_the_pinned_LibreMetaverse_assembly()
    {
        // Verified via reflection against LibreMetaverse 3.1.3's own PCode enum -- see the class
        // doc comment. If this ever fails, the package changed the values and every place that
        // special-cases foliage needs to be revisited, not just this constant.
        Assert.Equal(255, PrimPCode.Tree);
        Assert.Equal(111, PrimPCode.NewTree);
        Assert.Equal(95, PrimPCode.Grass);
    }
}
