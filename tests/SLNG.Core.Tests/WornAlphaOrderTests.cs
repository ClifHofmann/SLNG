using System;
using System.Linq;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// BUG-RENDER-50: the draw order of one avatar's worn rigged alpha. The case it was found on: a wearer
/// whose rear add-on (attachment point 28, four meshes in one linkset) has blended detail strips 2-4 cm
/// BEHIND her bikini bottom (point 23, one mesh, all faces blended). The old tie-break -- bake wearers at
/// 0, the rest at 3 cm + (localId % 256) x 0.1 mm -- put the bikini at 0.0328 and the add-on parts at
/// 0.0394-0.0449, so the strips were drawn after the bikini and painted over the fabric. The viewer
/// draws the higher attachment point first (llvoavatar.cpp:3046-3112, llspatialpartition.h:240-250).
/// </summary>
public class WornAlphaOrderTests
{
    // Local ids that reproduce the log's old offsets: 0.03 + (id % 256) * 1e-4.
    private const uint BikiniRoot = 256u * 4821 + 28;   // old offset 0.0328
    private const uint AddonRoot = 256u * 4823 + 94;    // old offset 0.0394
    private static readonly uint[] AddonParts = { AddonRoot, AddonRoot + 18, AddonRoot + 36, AddonRoot + 55 }; // .. 0.0449
    private const uint BodyRoot = 256u * 4700 + 200;    // a mesh body wearing the bake

    private static WornAlphaItem Bikini => new(false, 23, BikiniRoot, BikiniRoot);
    private static WornAlphaItem AddonPart(uint localId) => new(false, 28, AddonRoot, localId);
    private static WornAlphaItem Body => new(true, 10, BodyRoot, BodyRoot);

    private static float OldOffset(WornAlphaItem item) => (item.WearsBake ? 0f : 0.03f) + (item.LocalId % 256) * 0.0001f;

    [Fact]
    public void The_old_tie_break_drew_the_bikini_first()
    {
        Assert.Equal(0.0328f, OldOffset(Bikini), 4);
        Assert.Equal(0.0449f, OldOffset(AddonPart(AddonParts[^1])), 4);
        Assert.All(AddonParts, id => Assert.True(OldOffset(AddonPart(id)) > OldOffset(Bikini)));
    }

    [Fact]
    public void The_bikini_at_point_23_draws_after_every_part_of_the_point_28_addon()
    {
        var items = new[] { Bikini }.Concat(AddonParts.Select(AddonPart)).Append(Body).ToArray();
        var ranks = WornAlphaOrder.Rank(items);

        int bikini = ranks[0];
        for (int i = 1; i <= AddonParts.Length; i++)
            Assert.True(ranks[i] < bikini, $"add-on part {i} (rank {ranks[i]}) must draw before the bikini (rank {bikini})");

        // Every add-on surface and its depth pass come before the bikini's first surface.
        float bikiniFirst = WornAlphaOrder.SurfaceOffset(WornAlphaOrder.BaseOffset(bikini), 0);
        for (int i = 1; i <= AddonParts.Length; i++)
        {
            float b = WornAlphaOrder.BaseOffset(ranks[i]);
            Assert.True(WornAlphaOrder.SurfaceOffset(b, 7) < bikiniFirst);
            Assert.True(WornAlphaOrder.DepthOffset(b) < bikiniFirst);
        }
    }

    [Fact]
    public void A_bake_wearer_draws_first_whatever_its_point()
    {
        var items = new[] { Bikini, AddonPart(AddonRoot), Body with { AttachmentPoint = 2 } };
        var ranks = WornAlphaOrder.Rank(items);
        Assert.Equal(0, ranks[2]);
    }

    [Fact]
    public void Within_a_point_the_newer_linkset_draws_first_and_keeps_its_parts_together()
    {
        const uint older = 5000, newer = 9000;
        var items = new[]
        {
            new WornAlphaItem(false, 2, older, older),
            new WornAlphaItem(false, 2, older, older + 1),
            new WornAlphaItem(false, 2, newer, newer),
            new WornAlphaItem(false, 2, newer, newer + 1),
        };
        var ranks = WornAlphaOrder.Rank(items);
        Assert.True(ranks[2] < ranks[0] && ranks[3] < ranks[0] && ranks[2] < ranks[1] && ranks[3] < ranks[1],
            $"newer linkset first, as a block (ranks {string.Join(",", ranks)})");
    }

    [Fact]
    public void Offsets_are_distinct_ordered_and_inside_the_budget()
    {
        var items = new[] { Bikini }.Concat(AddonParts.Select(AddonPart)).Append(Body).ToArray();
        var ranks = WornAlphaOrder.Rank(items);
        Assert.Equal(items.Length, ranks.Distinct().Count());

        foreach (int rank in ranks)
        {
            float b = WornAlphaOrder.BaseOffset(rank);
            float next = WornAlphaOrder.BaseOffset(rank + 1);
            float previous = b;
            for (int s = 0; s < WornAlphaOrder.SurfaceSlots; s++)
            {
                float o = WornAlphaOrder.SurfaceOffset(b, s);
                Assert.True(o > previous, $"surface {s} of rank {rank} is not after the one before it");
                previous = o;
            }
            float depth = WornAlphaOrder.DepthOffset(b);
            Assert.True(depth > previous && depth < next, $"rank {rank}'s depth pass is not between its last surface and the next item");
            Assert.True(depth < 0.056f, "outside the 5.6 cm budget");
        }
    }

    [Fact]
    public void Ranks_clamp_so_a_huge_outfit_stays_inside_the_budget()
    {
        var items = Enumerable.Range(0, 700).Select(i => new WornAlphaItem(false, 2, (uint)(10_000 + i), (uint)(10_000 + i))).ToArray();
        var ranks = WornAlphaOrder.Rank(items);
        Assert.Equal(WornAlphaOrder.MaxRank, ranks.Max());
        Assert.True(WornAlphaOrder.DepthOffset(WornAlphaOrder.BaseOffset(ranks.Max())) < 0.056f);
        Assert.Equal(WornAlphaOrder.MaxRank + 1, ranks.Distinct().Count());
    }

    [Fact]
    public void Godot_float_depth_still_tells_every_step_apart_at_64_m()
    {
        // Godot sorts by distance - sorting_offset in single precision (render_forward_clustered.cpp:961-966).
        const float distance = 64f;
        float previous = float.PositiveInfinity;
        for (int rank = 0; rank < 4; rank++)
        {
            float b = WornAlphaOrder.BaseOffset(rank);
            for (int s = 0; s < WornAlphaOrder.SurfaceSlots; s++)
            {
                float depth = distance - WornAlphaOrder.SurfaceOffset(b, s);
                Assert.True(depth < previous);
                previous = depth;
            }
            float core = distance - WornAlphaOrder.DepthOffset(b);
            Assert.True(core < previous);
            previous = core;
        }
    }

    [Fact]
    public void Ranks_come_back_in_input_order()
    {
        var items = new[] { AddonPart(AddonRoot + 18), Bikini, AddonPart(AddonRoot) };
        var ranks = WornAlphaOrder.Rank(items);
        // Point 28 first, within its linkset the higher local id first; the bikini last.
        Assert.Equal(new[] { 0, 2, 1 }, ranks);
    }
}
