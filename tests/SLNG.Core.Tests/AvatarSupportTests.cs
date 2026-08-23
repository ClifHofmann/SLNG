using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>Covers the collision plane the simulator sends with every avatar update — the server's
/// own answer to what the avatar is standing on.
///
/// Why it matters: the client used to answer that question itself, by raycasting its own colliders
/// and falling back to the terrain heightmap when they missed. On login and teleport the prim under
/// the avatar has no collider yet, so the fallback answered "the ground is the land" and the clamp
/// dragged the avatar off the prim at 9.81 m/s². The real viewer never probes the scene for this:
/// <c>LLWorld::resolveStepHeightGlobal</c> corrects the land height with this plane, and the plane
/// arrives from the simulator's Havok physics, needing none of our colliders to exist.</summary>
public class AvatarSupportTests
{
    private const float Tolerance = 1e-4f;

    /// <summary>A flat floor at <paramref name="z"/>, the shape almost every real plane has.</summary>
    private static Vector4 FlatFloorAt(float z) => new(0f, 0f, 1f, z);

    [Fact]
    public void AFlatFloor_PutsTheSurfaceAtThePlaneConstant()
    {
        // Standing on a prim whose top is at 21.5, avatar cylinder centre 0.95 above it.
        float? support = AvatarSupport.SupportHeight(FlatFloorAt(21.5f), new Vector3(128f, 128f, 22.45f));

        Assert.NotNull(support);
        // The viewer's Havok tolerance shifts the answer down by exactly that much, deliberately.
        Assert.Equal(21.5f - AvatarSupport.HavokPlaneTolerance, support!.Value, Tolerance);
    }

    [Fact]
    public void TheAnswerDoesNotDependOnHowHighTheAvatarIs()
    {
        // The plane describes the surface, not the avatar. Two very different avatar heights over
        // the same plane must resolve to the same floor -- otherwise the clamp would chase itself.
        var plane = FlatFloorAt(10f);
        float? low = AvatarSupport.SupportHeight(plane, new Vector3(50f, 50f, 11f));
        float? high = AvatarSupport.SupportHeight(plane, new Vector3(50f, 50f, 90f));

        Assert.NotNull(low);
        Assert.NotNull(high);
        Assert.Equal(low!.Value, high!.Value, Tolerance);
    }

    [Fact]
    public void AnUnnormalisedPlane_DoesNotScaleTheHeight()
    {
        // Havok hands the viewer a unit normal, so the viewer does not normalise. We cannot assume
        // that after a decode and an interpolation, and getting it wrong is a silent vertical
        // offset rather than a visible failure.
        float? unit = AvatarSupport.SupportHeight(new Vector4(0f, 0f, 1f, 15f), new Vector3(0f, 0f, 16f));
        float? scaled = AvatarSupport.SupportHeight(new Vector4(0f, 0f, 3f, 45f), new Vector3(0f, 0f, 16f));

        Assert.NotNull(unit);
        Assert.NotNull(scaled);
        Assert.Equal(unit!.Value, scaled!.Value, Tolerance);
    }

    [Fact]
    public void ASlopedFloor_UsesThePerpendicularDistance_LikeTheViewerDoes()
    {
        // A 45-degree ramp through the origin: normal (0,0,1) tipped toward -x, constant 0, so the
        // surface is the plane z = x and at x = 4 it is geometrically 4 m up.
        //
        // The answer is NOT 4, and that is not a bug. dot(P, n) - w is the distance PERPENDICULAR
        // to the plane, and subtracting it from the avatar's Z is only the vertical drop when the
        // plane is horizontal. The viewer makes exactly the same approximation --
        // LLWorld::resolveStepHeightGlobal does `intersection.z -= norm_dist_from_plane *
        // segment_length` with that same perpendicular distance (llworld.cpp:578-582) -- so
        // "correcting" this by dividing through n.z would be a deviation from SL, not a fix.
        // Exact on flat ground, which is where avatars almost always are; increasingly loose as the
        // slope steepens, in the same direction and by the same amount as the real viewer.
        float c = 1f / MathF.Sqrt(2f);
        var ramp = new Vector4(-c, 0f, c, 0f);
        var position = new Vector3(4f, 0f, 5f);

        float? support = AvatarSupport.SupportHeight(ramp, position);

        float perpendicularDistance = (-c * position.X + c * position.Z)
                                      + AvatarSupport.HavokPlaneTolerance;
        Assert.NotNull(support);
        Assert.Equal(position.Z - perpendicularDistance, support!.Value, Tolerance);

        // Still bounded by sense: the surface sits between the true height and the avatar.
        Assert.InRange(support.Value, 4f - AvatarSupport.HavokPlaneTolerance, position.Z);
    }

    [Fact]
    public void AWall_IsRefused_RatherThanTreatedAsAFloor()
    {
        // The simulator can legitimately report a near-vertical plane. Reading it as a floor would
        // put the avatar's feet somewhere absurd, so it has to come back as "not told".
        var wall = new Vector4(1f, 0f, 0f, 5f);
        Assert.Null(AvatarSupport.SupportHeight(wall, new Vector3(6f, 0f, 20f)));
    }

    [Fact]
    public void ADegenerateOrNonFinitePlane_IsRefused()
    {
        // All-zero is what LibreMetaverse leaves behind when the update layout carried no plane at
        // all. It is not a plane through the origin, it is an absence.
        Assert.Null(AvatarSupport.SupportHeight(Vector4.Zero, new Vector3(1f, 2f, 3f)));
        Assert.Null(AvatarSupport.SupportHeight(
            new Vector4(float.NaN, 0f, 1f, 0f), new Vector3(1f, 2f, 3f)));
    }

    [Fact]
    public void AStandingAvatar_ResolvesToItsOwnFeet_SoTheClampIsANoOp()
    {
        // The property that makes this safe to feed the clamp: when the simulator has placed the
        // avatar correctly, the derived surface plus half the body height is the avatar's own Z,
        // so the clamp finds nothing to correct and stops fighting the server.
        const float halfBodyZ = 0.95f;
        const float floorZ = 30f;
        var position = new Vector3(64f, 64f, floorZ + halfBodyZ);

        float? support = AvatarSupport.SupportHeight(FlatFloorAt(floorZ), position);

        Assert.NotNull(support);
        float clampTargetZ = support!.Value + halfBodyZ;
        Assert.Equal(position.Z, clampTargetZ, AvatarSupport.HavokPlaneTolerance + Tolerance);
        // And it never resolves ABOVE the avatar, which would push it upward every frame.
        Assert.True(clampTargetZ <= position.Z);
    }
}
