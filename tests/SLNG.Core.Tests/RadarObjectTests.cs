using System;
using System.Collections.Generic;
using System.Numerics;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using Xunit;

namespace SLNG.Core.Tests;

// The radar's object layer (FEAT-UI-39 phase 2b): which prims it shows, how big it draws them and
// where, per LLViewerObject::setScale and LLViewerObjectList::renderObjectsForMap.
public class RadarObjectTests
{
    private static readonly ulong Home = RegionHandle.FromOrigin(256000, 256000);

    private static PrimShape Shape(byte pcode = 0) =>
        new(0, 0, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, pcode);

    /// <summary>Adds a prim to the world and returns its entity. Position is region-local, as the
    /// world stores it.</summary>
    private static Entity AddPrim(World world, uint localId, Vector3 position, Vector3 scale,
        bool youOwn = false, ulong? region = null, byte pcode = 0, bool phantom = false,
        byte attachmentPoint = 0, uint parentLocalId = 0)
    {
        var entity = world.GetOrCreateEntity(region ?? Home, localId);
        entity.SetComponent(new PrimitiveComponent(scale, 0, shape: Shape(pcode))
        {
            YouAreOwner = youOwn,
            IsPhantom = phantom,
            AttachmentPoint = attachmentPoint,
        });
        entity.SetComponent(new TransformComponent(position, Quaternion.Identity) { ParentLocalId = parentLocalId });
        return entity;
    }

    // The smallest preset, the viewer's own threshold, unless a test says otherwise.
    private const float ViewerSize = 7.5f;

    private static List<RadarObject> Collect(World world, float? viewerZ = null, ulong? current = null,
        float minSize = ViewerSize)
    {
        var list = new List<RadarObject>();
        RadarObjects.Collect(world, current ?? Home, viewerZ, minSize, list);
        return list;
    }

    // ---- Which prims are listed ---------------------------------------------------------------

    [Fact]
    public void An_owned_prim_is_listed_however_small()
    {
        Assert.True(RadarObjects.IsListed(youOwn: true, new Vector3(0.1f, 0.1f, 0.1f), 30f));
    }

    [Fact]
    public void A_prim_you_do_not_own_is_listed_only_when_it_is_big()
    {
        Assert.False(RadarObjects.IsListed(false, new Vector3(1, 1, 1), ViewerSize));
        Assert.False(RadarObjects.IsListed(false, new Vector3(5, 5, 0.5f), ViewerSize));   // footprint 7.07 m: still small
        Assert.True(RadarObjects.IsListed(false, new Vector3(6, 6, 0.5f), ViewerSize));    // footprint 8.49 m
    }

    [Fact]
    public void The_size_threshold_is_the_footprint_and_exclusive()
    {
        Assert.Equal(7.5f, RadarObjects.MinSizePresetsMetres[0]);                           // the viewer's own
        Assert.False(RadarObjects.IsListed(false, new Vector3(7.5f, 0, 0), ViewerSize));    // exactly 7.5 m
        Assert.True(RadarObjects.IsListed(false, new Vector3(7.51f, 0, 0), ViewerSize));
    }

    [Theory]
    [InlineData(0.6f, 0.6f, 12f)]     // a lamp post
    [InlineData(5f, 5f, 15f)]         // a mesh tree: tall, but only 7 m across
    [InlineData(0.3f, 0.3f, 400f)]    // a beanstalk
    public void A_tall_thin_prim_is_not_a_big_object(float x, float y, float z)
    {
        Assert.False(RadarObjects.IsListed(false, new Vector3(x, y, z), ViewerSize));
    }

    [Fact]
    public void A_bigger_size_leaves_out_the_mid_sized_pieces()
    {
        var wall = new Vector3(20, 1, 3);        // footprint 20.02 m
        var rock = new Vector3(10, 10, 6);       // footprint 14.14 m

        Assert.True(RadarObjects.IsListed(false, rock, 7.5f));
        Assert.False(RadarObjects.IsListed(false, rock, 15f));
        Assert.True(RadarObjects.IsListed(false, wall, 15f));
        Assert.False(RadarObjects.IsListed(false, wall, 30f));
    }

    // ---- The offered sizes ----------------------------------------------------------------------

    [Fact]
    public void There_are_three_sizes_and_the_default_is_the_middle_one()
    {
        Assert.Equal(new[] { 7.5f, 15f, 30f }, RadarObjects.MinSizePresetsMetres);
        Assert.Equal(15f, RadarObjects.DefaultMinSizeMetres);
        Assert.Equal(15f, new RadarViewSettings().ObjectMinSizeMetres);
    }

    [Theory]
    [InlineData(7.5f, 7.5f)]
    [InlineData(15f, 15f)]
    [InlineData(30f, 30f)]
    [InlineData(0f, 7.5f)]
    [InlineData(11f, 7.5f)]
    [InlineData(12f, 15f)]
    [InlineData(500f, 30f)]
    [InlineData(float.NaN, 7.5f)]
    public void A_stored_size_snaps_to_an_offered_one(float stored, float expected)
    {
        Assert.Equal(expected, RadarObjects.ClampMinSize(stored));
        var settings = new RadarViewSettings { ObjectMinSizeMetres = stored };
        Assert.Equal(expected, settings.ObjectMinSizeMetres);
    }

    // ---- How big each is drawn ----------------------------------------------------------------

    [Fact]
    public void The_radius_is_the_mean_horizontal_side_halved_with_the_viewers_fudge()
    {
        // (4 + 6) / 2 * 0.5 * 1.3 = 3.25
        Assert.Equal(3.25f, RadarObjects.RadiusFor(new Vector3(4, 6, 99), youOwn: false), 3);
    }

    [Fact]
    public void An_owned_prim_is_never_drawn_smaller_than_two_metres()
    {
        Assert.Equal(2f, RadarObjects.RadiusFor(new Vector3(0.2f, 0.2f, 0.2f), youOwn: true), 3);
        Assert.Equal(1.3f, RadarObjects.RadiusFor(new Vector3(2, 2, 2), youOwn: false), 3);
    }

    [Fact]
    public void A_megaprim_is_capped_at_sixteen_metres()
    {
        Assert.Equal(16f, RadarObjects.RadiusFor(new Vector3(400, 400, 1), youOwn: false), 3);
        Assert.Equal(16f, RadarObjects.RadiusFor(new Vector3(400, 400, 1), youOwn: true), 3);
    }

    // ---- Height and pacing --------------------------------------------------------------------

    [Theory]
    [InlineData(100f, 100f, true)]
    [InlineData(356f, 100f, true)]    // exactly 256 above
    [InlineData(356.1f, 100f, false)]
    [InlineData(-156.1f, 100f, false)]
    public void A_prim_far_above_or_below_the_avatar_is_left_out(float objectZ, float viewerZ, bool expected)
    {
        Assert.Equal(expected, RadarObjects.InVerticalRange(objectZ, viewerZ));
    }

    [Fact]
    public void Without_a_known_avatar_height_nothing_is_left_out_for_height()
    {
        Assert.True(RadarObjects.InVerticalRange(4000f, null));
    }

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(0.001, 0.5)]      // a cheap scan changes nothing: the minimum wait
    [InlineData(0.01, 2.0)]       // 10 ms may use at most half a percent of the time
    [InlineData(1.0, 5.0)]        // a stalled scan is still not left to starve the layer for good
    [InlineData(-1.0, 0.5)]
    [InlineData(double.NaN, 0.5)]
    public void The_wait_between_scans_grows_with_what_the_last_scan_cost(double lastScan, double expected)
    {
        Assert.Equal(expected, RadarObjects.NextScanDelaySeconds(lastScan), 6);
    }

    // ---- Collecting from the world ------------------------------------------------------------

    [Fact]
    public void An_owned_prim_lands_at_its_position_with_its_ownership()
    {
        var world = new World();
        AddPrim(world, 1, new Vector3(10, 20, 30), new Vector3(1, 1, 1), youOwn: true);

        var objects = Collect(world);

        var only = Assert.Single(objects);
        Assert.Equal(new Vector2(10, 20), only.Position);
        Assert.True(only.IsYours);
        Assert.Equal(2f, only.Radius, 3);
    }

    [Fact]
    public void The_chosen_size_decides_which_unowned_prims_are_collected()
    {
        var world = new World();
        AddPrim(world, 1, new Vector3(10, 10, 25), new Vector3(10, 10, 6));      // footprint 14 m
        AddPrim(world, 2, new Vector3(50, 50, 25), new Vector3(20, 20, 1));      // footprint 28 m
        AddPrim(world, 3, new Vector3(90, 90, 25), new Vector3(40, 40, 1));      // footprint 57 m

        Assert.Equal(3, Collect(world, minSize: 7.5f).Count);
        Assert.Equal(2, Collect(world, minSize: 15f).Count);
        Assert.Single(Collect(world, minSize: 30f));
    }

    [Fact]
    public void A_prim_you_own_is_collected_whatever_size_is_chosen()
    {
        var world = new World();
        AddPrim(world, 1, new Vector3(10, 10, 25), new Vector3(0.5f, 0.5f, 0.5f), youOwn: true);

        Assert.Single(Collect(world, minSize: 30f));
    }

    [Fact]
    public void A_small_prim_that_is_not_yours_is_not_collected_but_a_big_one_is()
    {
        var world = new World();
        AddPrim(world, 1, new Vector3(5, 5, 25), new Vector3(1, 1, 1));
        AddPrim(world, 2, new Vector3(50, 60, 25), new Vector3(20, 20, 0.5f));

        var objects = Collect(world);

        var only = Assert.Single(objects);
        Assert.Equal(new Vector2(50, 60), only.Position);
        Assert.False(only.IsYours);
    }

    [Theory]
    [InlineData(PrimPCode.Tree)]
    [InlineData(PrimPCode.NewTree)]
    [InlineData(PrimPCode.Grass)]
    public void Foliage_is_not_a_building(byte pcode)
    {
        var world = new World();
        AddPrim(world, 1, new Vector3(5, 5, 25), new Vector3(20, 20, 20), youOwn: true, pcode: pcode);

        Assert.Empty(Collect(world));
    }

    [Fact]
    public void A_worn_prim_is_not_part_of_the_land()
    {
        var world = new World();
        AddPrim(world, 1, new Vector3(5, 5, 25), new Vector3(1, 1, 1), youOwn: true, attachmentPoint: 5);
        var child = AddPrim(world, 2, new Vector3(5, 5, 25), new Vector3(1, 1, 1), youOwn: true);
        child.SetComponent(new AttachmentComponent(Guid.NewGuid(), 5)); // a linked child of a worn root

        Assert.Empty(Collect(world));
    }

    [Fact]
    public void An_avatar_is_not_collected_as_an_object()
    {
        var world = new World();
        var avatar = world.GetOrCreateEntity(Home, 7);
        avatar.SetComponent(new TransformComponent(new Vector3(5, 5, 25), Quaternion.Identity));

        Assert.Empty(Collect(world));
    }

    [Fact]
    public void A_linked_child_waits_for_its_root_but_is_collected_once_the_root_is_there()
    {
        var world = new World();
        AddPrim(world, 2, new Vector3(5, 5, 25), new Vector3(1, 1, 1), youOwn: true, parentLocalId: 1);

        Assert.Empty(Collect(world));

        AddPrim(world, 1, new Vector3(5, 5, 25), new Vector3(1, 1, 1), youOwn: true);
        Assert.Equal(2, Collect(world).Count);
    }

    [Fact]
    public void A_prim_far_above_the_avatar_is_left_out()
    {
        var world = new World();
        AddPrim(world, 1, new Vector3(5, 5, 3000), new Vector3(30, 30, 1), youOwn: true);
        AddPrim(world, 2, new Vector3(8, 8, 40), new Vector3(30, 30, 1), youOwn: true);

        var objects = Collect(world, viewerZ: 30f);

        Assert.Equal(new Vector2(8, 8), Assert.Single(objects).Position);
        Assert.Equal(2, Collect(world, viewerZ: null).Count);
    }

    [Fact]
    public void A_prim_under_the_water_line_is_marked_so()
    {
        var world = new World();
        world.GetOrCreateTerrain(Home).WaterHeight = 20f;
        AddPrim(world, 1, new Vector3(5, 5, 10), new Vector3(1, 1, 1), youOwn: true);
        AddPrim(world, 2, new Vector3(9, 9, 20), new Vector3(1, 1, 1), youOwn: true);   // at the line: above

        var objects = Collect(world);

        Assert.Single(objects, o => o.BelowWater);
        Assert.Single(objects, o => !o.BelowWater);
    }

    [Fact]
    public void Without_a_terrain_nothing_is_called_under_water()
    {
        var world = new World();
        AddPrim(world, 1, new Vector3(5, 5, -5), new Vector3(1, 1, 1), youOwn: true);

        Assert.False(Assert.Single(Collect(world)).BelowWater);
    }

    [Fact]
    public void A_phantom_prim_says_so()
    {
        var world = new World();
        AddPrim(world, 1, new Vector3(5, 5, 25), new Vector3(30, 30, 1), phantom: true);

        Assert.True(Assert.Single(Collect(world)).Phantom);
    }

    [Fact]
    public void A_neighbours_prim_is_placed_in_the_current_regions_metres()
    {
        var east = RegionHandle.FromOrigin(256256, 256000);
        var south = RegionHandle.FromOrigin(256000, 255744);
        var world = new World();
        AddPrim(world, 1, new Vector3(10, 20, 25), new Vector3(30, 30, 1), region: east);
        AddPrim(world, 2, new Vector3(10, 20, 25), new Vector3(30, 30, 1), region: south);

        var objects = Collect(world);

        Assert.Contains(objects, o => o.Position == new Vector2(266, 20));    // 256 m east of here
        Assert.Contains(objects, o => o.Position == new Vector2(10, -236));   // 256 m south of here
    }

    [Fact]
    public void A_prim_with_a_broken_position_is_skipped()
    {
        var world = new World();
        AddPrim(world, 1, new Vector3(float.NaN, 5, 25), new Vector3(30, 30, 1), youOwn: true);
        AddPrim(world, 2, new Vector3(5, float.PositiveInfinity, 25), new Vector3(30, 30, 1), youOwn: true);
        AddPrim(world, 3, new Vector3(5, 5, 25), new Vector3(30, 30, 1), youOwn: true);

        Assert.Equal(new Vector2(5, 5), Assert.Single(Collect(world)).Position);
    }

    [Fact]
    public void Collecting_replaces_what_the_list_held_and_reports_the_count()
    {
        var world = new World();
        AddPrim(world, 1, new Vector3(5, 5, 25), new Vector3(1, 1, 1), youOwn: true);
        var list = new List<RadarObject> { default, default, default };

        int count = RadarObjects.Collect(world, Home, null, ViewerSize, list);

        Assert.Equal(1, count);
        Assert.Single(list);
    }

    // ---- The view's reach and the setting -----------------------------------------------------

    [Fact]
    public void Reach_covers_a_square_canvas_by_its_range_and_a_wide_one_by_its_corner()
    {
        // Square: the corner is only 0.71 of the range away, so the range itself is the reach.
        Assert.Equal(100f, RadarProjection.ViewReachMetres(new Vector2(400, 400), 100f), 3);
        // 800x200 canvas showing 50 m on its short side: the corners are 103 m from the centre.
        Assert.Equal(103.08f, RadarProjection.ViewReachMetres(new Vector2(800, 200), 50f), 2);
    }

    [Fact]
    public void Reach_with_no_canvas_falls_back_to_the_range()
    {
        Assert.Equal(64f, RadarProjection.ViewReachMetres(Vector2.Zero, 64f), 3);
    }

    [Fact]
    public void Objects_are_shown_by_default_as_in_the_reference_viewer()
    {
        Assert.True(new RadarViewSettings().ShowObjects);
    }
}
