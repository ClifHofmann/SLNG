using LibreMetaverse;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>BUG-AVATAR-12: a seated avatar's world transform is composed like the viewer's drawables
/// (<c>LLXformMatrix::updateMatrix</c>): the avatar's own rotation first, then each ancestor's, and every
/// ancestor turns the position below it. Expectations are built with LibreMetaverse's
/// <c>Vector3 * Quaternion</c>, which is the same code as LL's (rotate a vector by a quaternion), and
/// never with its <c>Quaternion * Quaternion</c>, whose order is the reverse of LL's.</summary>
public class SeatedTransformTests
{
    private const float Tolerance = 1e-4f;

    private static Quaternion About(Vector3 axis, float degrees) =>
        Quaternion.CreateFromAxisAngle(axis, degrees * (float)Math.PI / 180f);

    private static Quaternion Conjugate(Quaternion q) => new(-q.X, -q.Y, -q.Z, q.W);

    /// <summary>Rotates <paramref name="v"/> by each rotation in turn, LL's meaning of <c>v * a * b</c>.</summary>
    private static Vector3 Apply(Vector3 v, params Quaternion[] rotations)
    {
        foreach (var q in rotations) v = v * q;
        return v;
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, string what)
    {
        Assert.True(Vector3.Distance(expected, actual) < Tolerance, $"{what}: expected {expected}, got {actual}");
    }

    private static Simulator NewSim(GridClient client) =>
        new(client, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 9000), 1234UL);

    [Fact]
    public void An_avatar_sat_upright_on_a_pitched_seat_stays_upright()
    {
        // The hammock case: the seat's root is pitched 90 degrees, and the sit rotation undoes that
        // pitch and turns the avatar 90 degrees about Z. In LL terms rel = yaw * ~seat, so the world
        // rotation rel * seat is the yaw alone -- the avatar's up must stay world up.
        using var client = new GridClient();
        var sim = NewSim(client);
        var seatRot = About(Vector3.UnitX, 90f);
        var seat = new Primitive { LocalID = 10, Position = new Vector3(118f, 42f, 22.5f), Rotation = seatRot };
        sim.ObjectsPrimitives[10] = seat;
        var yaw = About(Vector3.UnitZ, 90f);
        var relRot = Conjugate(seatRot) * yaw; // LibreMetaverse's product: this is LL's yaw * ~seat
        var relPos = new Vector3(0.4f, -0.2f, 0.6f);

        Assert.True(GridSession.ResolveSeatedTransform(sim, relPos, relRot, 10, out var pos, out var rot));

        AssertClose(Vector3.UnitZ, Vector3.UnitZ * rot, "the avatar's up");
        AssertClose(Vector3.UnitX * yaw, Vector3.UnitX * rot, "the avatar's forward");
        AssertClose(seat.Position + relPos * seatRot, pos, "the seat point");
    }

    [Fact]
    public void Any_seat_rotation_applies_the_avatars_own_rotation_first()
    {
        using var client = new GridClient();
        var sim = NewSim(client);
        var seatRot = About(Vector3.Normalize(new Vector3(1f, 2f, 0.5f)), 70f);
        sim.ObjectsPrimitives[10] = new Primitive { LocalID = 10, Position = new Vector3(10f, 20f, 30f), Rotation = seatRot };
        var relRot = About(Vector3.Normalize(new Vector3(-0.3f, 0.2f, 1f)), 40f);

        Assert.True(GridSession.ResolveSeatedTransform(sim, Vector3.Zero, relRot, 10, out _, out var rot));

        foreach (var v in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            AssertClose(Apply(v, relRot, seatRot), v * rot, $"{v} rotated");
    }

    [Fact]
    public void A_child_prim_seat_is_turned_by_its_root_too()
    {
        using var client = new GridClient();
        var sim = NewSim(client);
        var rootRot = About(Vector3.UnitY, -90f);
        var root = new Primitive { LocalID = 10, Position = new Vector3(100f, 50f, 22f), Rotation = rootRot };
        var childRot = About(Vector3.UnitZ, 30f);
        var child = new Primitive { LocalID = 11, ParentID = 10, Position = new Vector3(1f, 0f, 0.5f), Rotation = childRot };
        sim.ObjectsPrimitives[10] = root;
        sim.ObjectsPrimitives[11] = child;
        var relPos = new Vector3(0.2f, 0f, 0.4f);
        var relRot = About(Vector3.UnitZ, 10f);

        Assert.True(GridSession.ResolveSeatedTransform(sim, relPos, relRot, 11, out var pos, out var rot));

        AssertClose(root.Position + (child.Position + relPos * childRot) * rootRot, pos, "position");
        foreach (var v in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            AssertClose(Apply(v, relRot, childRot, rootRot), v * rot, $"{v} rotated");
    }

    [Fact]
    public void An_unknown_ancestor_is_reported_unresolved()
    {
        using var client = new GridClient();
        var sim = NewSim(client);
        sim.ObjectsPrimitives[11] = new Primitive { LocalID = 11, ParentID = 99, Position = new Vector3(1f, 0f, 0f), Rotation = Quaternion.Identity };

        Assert.False(GridSession.ResolveSeatedTransform(sim, Vector3.Zero, Quaternion.Identity, 11, out _, out _));
    }

    [Fact]
    public void A_standing_avatar_is_returned_unchanged()
    {
        using var client = new GridClient();
        var sim = NewSim(client);
        var relPos = new Vector3(128f, 64f, 23f);
        var relRot = About(Vector3.UnitZ, 45f);

        Assert.True(GridSession.ResolveSeatedTransform(sim, relPos, relRot, 0, out var pos, out var rot));

        Assert.Equal(relPos, pos);
        Assert.Equal(relRot, rot);
    }
}
