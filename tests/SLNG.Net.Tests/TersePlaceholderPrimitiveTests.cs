using System.Linq;
using LibreMetaverse;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// A terse update can arrive for an object no full <c>ObjectUpdate</c> has been seen for yet
/// (it moves into view, crosses a region border, or its full update was lost). LibreMetaverse's
/// <c>ImprovedTerseObjectUpdateHandler</c> resolves the object with
/// <c>GetPrimitive(sim, localID, UUID.Zero)</c>, and that helper <b>creates</b> one when the
/// localID is unknown — <c>new Primitive { LocalID, RegionHandle }</c> followed by
/// <c>prim.ID = fullID</c> (ObjectManager.cs:2686-2726). Every other field, <c>PrimData</c>
/// included, is left at its default.
///
/// <para>Verified against both v3.1.3 and v3.1.6 — identical in each, which is what rules the
/// LibreMetaverse upgrade out as the cause: the warning that prompted this was first seen on a
/// 3.1.6 build, but the behaviour is unchanged between the two.</para>
///
/// <para>Publishing an <c>ObjectUpdateEvent</c> from such an object hands the world model a prim
/// with no shape, no scale and no textures, which is how a placeholder cylinder and a
/// <c>[PrimMeshFallback] … pathScale=(0,0)</c> warning reached the log. These tests pin both the
/// detection and the diagnosis.</para>
/// </summary>
public class TersePlaceholderPrimitiveTests
{
    /// <summary>Exactly what <c>GetPrimitive</c> builds on a cache miss from the terse path.</summary>
    private static Primitive LmvPlaceholder(uint localId = 1234, ulong regionHandle = 11437119954698752UL)
        => new() { LocalID = localId, RegionHandle = regionHandle, ID = UUID.Zero };

    private static Primitive RealPrim(PCode pcode = PCode.Prim)
    {
        var prim = new Primitive
        {
            LocalID = 1234,
            RegionHandle = 11437119954698752UL,
            ID = new UUID("4c7a2bc5-0000-4000-8000-000000000001"),
            Scale = new Vector3(0.5f, 0.5f, 0.5f),
        };
        var pd = prim.PrimData;
        pd.PCode = pcode;
        pd.PathScaleX = 1.0f;
        pd.PathScaleY = 1.0f;
        prim.PrimData = pd;
        return prim;
    }

    [Fact]
    public void LmvPlaceholderIsDetected()
        => Assert.True(GridSession.IsUnpopulatedPrimitive(LmvPlaceholder()));

    [Fact]
    public void BareNewPrimitiveIsDetected()
        => Assert.True(GridSession.IsUnpopulatedPrimitive(new Primitive()));

    [Fact]
    public void RealPrimIsNotDetected()
        => Assert.False(GridSession.IsUnpopulatedPrimitive(RealPrim()));

    /// <summary>Foliage carries a shape the prim mesher deliberately ignores (PrimPCode.IsFoliage —
    /// trees and grass get crossed planes, not profile/path geometry). It must not be mistaken for
    /// an unpopulated object just because its construction data looks unusual.</summary>
    [Theory]
    [InlineData(PCode.Tree)]
    [InlineData(PCode.NewTree)]
    [InlineData(PCode.Grass)]
    public void FoliageIsNotDetected(PCode pcode)
        => Assert.False(GridSession.IsUnpopulatedPrimitive(RealPrim(pcode)));

    /// <summary>A real UUID cannot rescue an object whose construction data never arrived:
    /// <c>PCode.None</c> is 0 and no renderable object has it.</summary>
    [Fact]
    public void RealIdWithNoPCodeIsStillDetected()
    {
        var prim = RealPrim();
        var pd = prim.PrimData;
        pd.PCode = PCode.None;
        prim.PrimData = pd;

        Assert.True(GridSession.IsUnpopulatedPrimitive(prim));
    }

    /// <summary>Pins the diagnosis rather than the fix: a default <c>ConstructionData</c> produces
    /// precisely the field values that appeared in the OSGrid log
    /// (<c>profile=0 path=0 pathScale=(0,0) cut=(0..0) hollow=0 revs=0</c>), which is what
    /// identified the warning as an unpopulated struct rather than an exotic prim. If a future
    /// LibreMetaverse gives <c>ConstructionData</c> real defaults, this test fails and the
    /// reasoning above has to be revisited.</summary>
    [Fact]
    public void DefaultConstructionDataMatchesTheLoggedFallbackLine()
    {
        var pd = new Primitive().PrimData;

        Assert.Equal(0, pd.profileCurve);
        // 0 is not merely an odd PathCurve, it is not a PathCurve at all: the enum's members are
        // Line 16, Circle 32, Circle2 48, Test 64, Flexible 128 (no zero member). A decoded prim
        // can never hold this value.
        Assert.Equal(0, (byte)pd.PathCurve);
        Assert.DoesNotContain(0, System.Enum.GetValues<PathCurve>().Select(v => (int)v));
        Assert.Equal(0f, pd.PathScaleX);
        Assert.Equal(0f, pd.PathScaleY);
        Assert.Equal(0f, pd.PathBegin);
        Assert.Equal(0f, pd.PathEnd);
        Assert.Equal(0f, pd.ProfileHollow);
        Assert.Equal(0f, pd.PathRevolutions);
        Assert.Equal(PCode.None, pd.PCode);
    }

    /// <summary>A real prim's path scale is 1, not 0 — the single field that made the logged line
    /// impossible to explain as genuine content.</summary>
    [Fact]
    public void RealPrimHasNonZeroPathScale()
    {
        var pd = RealPrim().PrimData;
        Assert.Equal(1.0f, pd.PathScaleX);
        Assert.Equal(1.0f, pd.PathScaleY);
    }
}
