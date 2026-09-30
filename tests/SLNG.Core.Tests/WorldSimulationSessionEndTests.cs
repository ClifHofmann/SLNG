using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// BUG-NET-24: when the grid ends the whole session, the world stays on screen as it was — the
/// reference viewer leaves it untouched behind its "logged out" message — and it goes when the
/// client leaves for the login screen. One region dropping out of a session that carries on is
/// still unloaded at once.
///
/// <para>Reported in-world: logged out by the grid, SLNG showed the message over a naked base
/// avatar in an empty sea, because every region was unloaded, attachments included.</para>
/// </summary>
public class WorldSimulationSessionEndTests
{
    private const ulong Here = 1000ul;
    private const ulong Neighbour = 2000ul;

    private static ObjectUpdateEvent Prim(ulong region, uint localId) => new(
        region, localId, new Vector3(10, 20, 30), Quaternion.Identity, Vector3.One, 1, false,
        Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One, ParentLocalId: 0, AttachmentPoint: 0);

    [Fact]
    public void A_region_that_goes_with_the_session_stays_as_it_was()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        session.RaiseObjectUpdate(Prim(Here, 1));
        simulation.Pump();
        world.GetOrCreateTerrain(Here);

        session.RaiseRegionDisconnected(new RegionDisconnectedEvent(Here, SessionEnded: true));
        simulation.Pump();

        Assert.NotNull(world.GetEntity(Here, 1));
        Assert.True(world.Terrains.ContainsKey(Here));
    }

    // What walking away from a border or teleporting does: the session carries on without that
    // region, and its content must not linger.
    [Fact]
    public void A_region_that_drops_out_of_a_live_session_is_unloaded_at_once()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        session.RaiseObjectUpdate(Prim(Neighbour, 1));
        simulation.Pump();
        world.GetOrCreateTerrain(Neighbour);

        session.RaiseRegionDisconnected(new RegionDisconnectedEvent(Neighbour));
        simulation.Pump();

        Assert.Null(world.GetEntity(Neighbour, 1));
        Assert.False(world.Terrains.ContainsKey(Neighbour));
    }

    // Once the user leaves the frozen world for the login screen, it goes the way a region always
    // went: everything but the local agent, which World.RemoveRegion never takes (BUG-NET-13).
    [Fact]
    public void Leaving_for_the_login_screen_clears_every_region_but_the_local_agent()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        session.RaiseObjectUpdate(Prim(Here, 1));
        session.RaiseObjectUpdate(Prim(Neighbour, 2));
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            Here, 7, Guid.NewGuid(), new Vector3(128, 128, 25), Quaternion.Identity, "Puris", "Viewer",
            IsLocalAgent: true));
        simulation.Pump();
        world.GetOrCreateTerrain(Here);
        world.GetOrCreateTerrain(Neighbour);
        session.RaiseRegionDisconnected(new RegionDisconnectedEvent(Here, SessionEnded: true));
        session.RaiseRegionDisconnected(new RegionDisconnectedEvent(Neighbour, SessionEnded: true));
        simulation.Pump();

        simulation.UnloadAllRegions();

        Assert.Null(world.GetEntity(Here, 1));
        Assert.Null(world.GetEntity(Neighbour, 2));
        Assert.Empty(world.Terrains);
        var self = Assert.Single(world.GetAllEntities());
        Assert.True(self.GetComponent<AvatarComponent>()?.IsLocalAgent);
    }
}
