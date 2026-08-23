using LibreMetaverse.Rendering;
using SLNG.Assets;
using SLNG.Core;
using Xunit;
using Xunit.Abstractions;

namespace SLNG.Assets.Tests;

/// <summary>
/// Every one of SL's seven basic prim types must produce real geometry.
///
/// <para>Written while chasing a reef that rendered as smooth discs: a prim whose meshing fails
/// falls back to a placeholder cylinder/sphere/box at the object's own scale, which at scenery
/// size reads as content rather than as a failure. That made "is our mesher simply refusing this
/// shape?" worth ruling out before looking further -- it was not the cause there, but nothing
/// covered it, and the fallback is silent by design.</para>
/// </summary>
public class BasicPrimTypeMeshingTests
{
    private readonly ITestOutputHelper _out;
    public BasicPrimTypeMeshingTests(ITestOutputHelper output) => _out = output;

    // SL prim types are (profile curve, path curve) pairs.
    // Box 1/16, Cylinder 0/16, Prism 3/16, Sphere 5/32, Torus 0/32, Tube 1/32, Ring 3/32.
    [Theory]
    [InlineData("box", (byte)1, (byte)16)]
    [InlineData("cylinder", (byte)0, (byte)16)]
    [InlineData("sphere", (byte)5, (byte)32)]
    [InlineData("torus", (byte)0, (byte)32)]
    [InlineData("tube", (byte)1, (byte)32)]
    [InlineData("ring", (byte)3, (byte)32)]
    public void EveryBasicPrimType_Meshes(string name, byte profileCurve, byte pathCurve)
    {
        // The SL defaults a freshly-rezzed prim of each type carries.
        var shape = new PrimShape(
            ProfileCurve: profileCurve,
            PathCurve: pathCurve,
            PathBegin: 0f, PathEnd: 1f,
            PathScaleX: pathCurve == 32 ? 1f : 1f,
            PathScaleY: pathCurve == 32 ? 0.5f : 1f,
            PathShearX: 0f, PathShearY: 0f,
            PathTaperX: 0f, PathTaperY: 0f,
            PathTwist: 0, PathTwistBegin: 0,
            PathRadiusOffset: 0f, PathSkew: 0f, PathRevolutions: 1f,
            ProfileBegin: 0f, ProfileEnd: 1f, ProfileHollow: 0f,
            PCode: 9);

        var mesh = PrimMeshService.Generate(shape, DetailLevel.High);

        int verts = 0, tris = 0;
        if (mesh != null)
            foreach (var sub in mesh.Submeshes) { verts += sub.Positions.Length; tris += sub.Indices.Length / 3; }
        _out.WriteLine($"{name}: mesh={(mesh == null ? "NULL" : "ok")} submeshes={mesh?.Submeshes.Count ?? 0} verts={verts} tris={tris}");

        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh!.Submeshes);
        Assert.True(tris > 0, $"{name} produced no triangles");
    }
}
