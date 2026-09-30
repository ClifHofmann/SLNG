using SLNG.Core.ECS;
using SLNG.Net;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// BUG-NET-21: leave a region and come back, and the ground is gone. OpenSim treats the returning
/// agent as still present, aborts its <c>CompleteMovement</c> ("already root") and so sends no
/// terrain and no region handshake -- the terrain and its textures and water height have to come
/// from what the client held when it left. They are kept aside at departure and put back when the
/// region connects again; anything the simulator does send still wins.
/// </summary>
public class WorldSimulationTerrainReturnTests
{
    private const ulong Here = 1000ul;

    private static readonly Guid Rock = Guid.NewGuid();

    private static float[] Flat(float height) => Enumerable.Repeat(height, 16 * 16).ToArray();

    private static TerrainPatchEvent Patch(ulong region, float height) => new(region, 0, 0, 16, Flat(height));

    private static TerrainSettingsEvent Settings(ulong region, Guid detail0, float water) => new(
        region, detail0, Guid.Empty, Guid.Empty, Guid.Empty,
        new float[] { 1, 2, 3, 4 }, new float[] { 5, 6, 7, 8 }, water);

    // What LibreMetaverse reports for a region whose handshake never came: nothing filled in.
    private static TerrainSettingsEvent UnfilledSettings(ulong region) => new(
        region, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty,
        new float[4], new float[4], 0f);

    private static void Leave(GridSession session, WorldSimulation simulation, ulong region)
    {
        session.RaiseRegionDisconnected(new RegionDisconnectedEvent(region));
        simulation.Pump();
    }

    [Fact]
    public void Terrain_the_simulator_does_not_resend_comes_back_with_the_region()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        session.RaiseTerrainSettings(Settings(Here, Rock, 17f));
        session.RaiseTerrainPatch(Patch(Here, 30f));
        simulation.Pump();
        Leave(session, simulation, Here);
        Assert.False(world.Terrains.ContainsKey(Here));

        session.RaiseTerrainSettings(Settings(Here, Rock, 17f)); // the connect itself, no patches follow
        simulation.Pump();

        Assert.True(world.Terrains[Here].TryGetKnownHeight(0, 0, out var height));
        Assert.Equal(30f, height);
    }

    [Fact]
    public void Settings_a_region_never_filled_in_do_not_wipe_the_ones_held()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        session.RaiseTerrainSettings(Settings(Here, Rock, 17f));
        simulation.Pump();
        Leave(session, simulation, Here);

        session.RaiseTerrainSettings(UnfilledSettings(Here));
        simulation.Pump();

        var terrain = world.Terrains[Here];
        Assert.Equal(Rock, terrain.TerrainDetail0);
        Assert.Equal(17f, terrain.WaterHeight);
        Assert.Equal(new float[] { 1, 2, 3, 4 }, terrain.TerrainStartHeights);
    }

    [Fact]
    public void Settings_a_region_does_fill_in_replace_the_ones_held()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        session.RaiseTerrainSettings(Settings(Here, Rock, 17f));
        simulation.Pump();
        Leave(session, simulation, Here);
        var newRock = Guid.NewGuid();

        session.RaiseTerrainSettings(Settings(Here, newRock, 21f));
        simulation.Pump();

        Assert.Equal(newRock, world.Terrains[Here].TerrainDetail0);
        Assert.Equal(21f, world.Terrains[Here].WaterHeight);
    }

    [Fact]
    public void A_patch_the_simulator_does_send_wins_over_the_one_held()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        session.RaiseTerrainPatch(Patch(Here, 30f));
        simulation.Pump();
        Leave(session, simulation, Here);

        session.RaiseTerrainPatch(Patch(Here, 12f)); // the land was changed while we were away
        simulation.Pump();

        Assert.True(world.Terrains[Here].TryGetKnownHeight(0, 0, out var height));
        Assert.Equal(12f, height);
    }

    [Fact]
    public void Only_the_last_few_regions_left_are_kept()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        for (ulong region = 1; region <= 6; region++)
        {
            session.RaiseTerrainPatch(Patch(region, 30f));
            simulation.Pump();
            Leave(session, simulation, region);
        }

        session.RaiseTerrainSettings(Settings(1, Rock, 17f)); // the oldest is long gone
        session.RaiseTerrainSettings(Settings(6, Rock, 17f)); // the newest is still held
        simulation.Pump();

        Assert.False(world.Terrains[1].TryGetKnownHeight(0, 0, out _));
        Assert.True(world.Terrains[6].TryGetKnownHeight(0, 0, out _));
    }

    [Fact]
    public void Leaving_for_the_login_screen_forgets_what_was_kept()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        session.RaiseTerrainPatch(Patch(Here, 30f));
        simulation.Pump();
        Leave(session, simulation, Here);

        simulation.UnloadAllRegions();
        session.RaiseTerrainSettings(Settings(Here, Rock, 17f));
        simulation.Pump();

        Assert.False(world.Terrains[Here].TryGetKnownHeight(0, 0, out _));
    }
}
