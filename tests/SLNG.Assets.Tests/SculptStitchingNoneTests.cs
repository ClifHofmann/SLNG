using LibreMetaverse.Rendering;
using SLNG.Assets;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// A sculpt whose stitching type is None (0) must still be sculpted, and must be wrapped exactly
/// like a Plane.
///
/// <para>Found on OSGrid (The Dangazi Forest, 2026-08-23): a reef rock rendered as a smooth
/// 26x7x71 ellipse because it reached the renderer as a plain torus prim. Firestorm's build
/// floater reported the same object as "Geformt" (sculpted) with stitching "Plane/None" and
/// Invert set -- a sculpt type byte of 0x40, whose low three bits are zero. The wire path used to
/// require a non-None type before treating the prim as sculpted, so the geometry was discarded
/// and the prim fell back to its underlying profile/path curve.</para>
///
/// <para>The viewer gates sculpting on the block being PRESENT, not on its stitching value
/// (<c>LLVOVolume::isSculpted</c>, llvovolume.cpp:3633), and stitching 0 takes the same wrapping
/// path as Plane, because <c>sculptGenerateMapVertices</c> special-cases only Sphere, Torus and
/// Cylinder (llvolume.cpp:3072-3113).</para>
/// </summary>
public class SculptStitchingNoneTests
{
    private const byte StitchingNone = 0x00;
    private const byte StitchingPlane = 0x03;
    private const byte FlagInvert = 0x40;

    /// <summary>A sculpt map with enough variation that a wrong wrap shows up as different
    /// vertex positions rather than as an identical flat sheet.</summary>
    private static byte[] Map(int size)
    {
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                rgba[i + 0] = (byte)(x * 255 / (size - 1));
                rgba[i + 1] = (byte)(y * 255 / (size - 1));
                rgba[i + 2] = (byte)((x ^ y) & 0xFF);
                rgba[i + 3] = 255;
            }
        }
        return rgba;
    }

    [Fact]
    public void StitchingNone_ProducesGeometry()
    {
        var mesh = PrimMeshService.GenerateSculpt(Map(64), 64, 64, StitchingNone, DetailLevel.High);

        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh!.Submeshes);
        Assert.True(mesh.Submeshes[0].Indices.Length >= 3, "stitching None produced no triangles");
    }

    [Fact]
    public void StitchingNone_IsWrappedLikePlane()
    {
        var none = PrimMeshService.GenerateSculpt(Map(64), 64, 64, StitchingNone, DetailLevel.High);
        var plane = PrimMeshService.GenerateSculpt(Map(64), 64, 64, StitchingPlane, DetailLevel.High);

        Assert.NotNull(none);
        Assert.NotNull(plane);
        Assert.Equal(plane!.Submeshes[0].Positions.Length, none!.Submeshes[0].Positions.Length);
        Assert.Equal(plane.Submeshes[0].Positions, none.Submeshes[0].Positions);
    }

    [Fact]
    public void StitchingNone_KeepsTheInvertFlag()
    {
        // 0x40 is the byte the real object carried. The low bits select the (None) stitching and
        // the flag must survive alongside them -- if the type were read through a masking
        // accessor, Invert would be lost and the sculpt would be built inside out.
        var plain = PrimMeshService.GenerateSculpt(Map(64), 64, 64, StitchingNone, DetailLevel.High);
        var inverted = PrimMeshService.GenerateSculpt(Map(64), 64, 64, StitchingNone | FlagInvert, DetailLevel.High);

        Assert.NotNull(plain);
        Assert.NotNull(inverted);
        Assert.NotEqual(plain!.Submeshes[0].Indices, inverted!.Submeshes[0].Indices);
    }
}
