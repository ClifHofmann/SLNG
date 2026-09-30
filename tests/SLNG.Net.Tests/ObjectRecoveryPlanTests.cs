using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

// BUG-NET-21. A region left and entered again streams back only what it had not sent before, so
// what the world was holding when we left has to be asked for by id. These pin the arithmetic of
// that ask; the network side of it is exercised in-world.
public class ObjectRecoveryPlanTests
{
    [Fact]
    public void Missing_is_what_was_known_and_has_not_come_back()
    {
        var known = new uint[] { 10, 11, 12, 13, 14 };
        var present = new HashSet<uint> { 11, 13 };

        Assert.Equal(new uint[] { 10, 12, 14 }, ObjectRecoveryPlan.MissingIds(known, present));
    }

    [Fact]
    public void Missing_keeps_the_order_it_was_given_so_the_nearest_objects_are_asked_first()
    {
        var known = new uint[] { 30, 5, 20, 7 };

        Assert.Equal(new uint[] { 30, 5, 20, 7 }, ObjectRecoveryPlan.MissingIds(known, new HashSet<uint>()));
    }

    [Fact]
    public void Missing_asks_for_an_id_once_even_if_it_was_remembered_twice()
    {
        var known = new uint[] { 4, 4, 9 };

        Assert.Equal(new uint[] { 4, 9 }, ObjectRecoveryPlan.MissingIds(known, new HashSet<uint>()));
    }

    [Fact]
    public void Nothing_is_missing_when_everything_came_back()
    {
        var known = new uint[] { 1, 2, 3 };

        Assert.Empty(ObjectRecoveryPlan.MissingIds(known, new HashSet<uint> { 1, 2, 3, 99 }));
    }

    [Fact]
    public void Chunks_are_no_bigger_than_asked_and_lose_nothing()
    {
        var ids = Enumerable.Range(0, 250).Select(i => (uint)i).ToArray();

        var chunks = ObjectRecoveryPlan.Chunk(ids, 100).ToList();

        Assert.Equal(new[] { 100, 100, 50 }, chunks.Select(c => c.Count));
        Assert.Equal(ids, chunks.SelectMany(c => c));
    }

    [Fact]
    public void An_empty_list_makes_no_chunks()
    {
        Assert.Empty(ObjectRecoveryPlan.Chunk(Array.Empty<uint>(), 100));
    }

    // Waiting for the simulator's own stream to end costs the user half a minute, so the ask goes
    // out at once when the simulator is plainly not going to resend what it sent before -- and only
    // then, because a simulator that does start over would answer it twice.
    [Fact]
    public void A_simulator_that_delivers_almost_nothing_we_knew_is_withholding()
    {
        var known = Enumerable.Range(0, 3000).Select(i => (uint)i).ToHashSet();
        var present = Enumerable.Range(10_000, 2003).Select(i => (uint)i).Concat(new uint[] { 1, 2, 3 }).ToList();

        Assert.True(ObjectRecoveryPlan.IsWithholding(present, known));
    }

    [Fact]
    public void A_simulator_that_starts_over_is_not_withholding()
    {
        var known = Enumerable.Range(0, 3000).Select(i => (uint)i).ToHashSet();
        var present = Enumerable.Range(0, 1200).Select(i => (uint)i).ToList();

        Assert.False(ObjectRecoveryPlan.IsWithholding(present, known));
    }

    [Fact]
    public void Too_little_delivered_is_not_enough_to_judge()
    {
        var known = Enumerable.Range(0, 3000).Select(i => (uint)i).ToHashSet();
        var present = Enumerable.Range(10_000, ObjectRecoveryPlan.MinSampleToJudge - 1).Select(i => (uint)i).ToList();

        Assert.False(ObjectRecoveryPlan.IsWithholding(present, known));
    }

    // The settle check: the stream is over once the object count has stopped moving for a few
    // polls in a row, never before a minimum time has passed, and always by a ceiling.
    [Theory]
    [InlineData(new[] { 10, 50, 120, 120, 120 }, 3, true)]   // flat for the last three polls
    [InlineData(new[] { 10, 50, 120, 120, 121 }, 3, false)]  // still growing
    [InlineData(new[] { 10, 10 }, 3, false)]                 // too few polls to say
    [InlineData(new[] { 0, 0, 0, 0 }, 3, false)]             // nothing has arrived yet: not settled, not started
    public void Stream_has_settled_when_the_count_is_flat_for_enough_polls(int[] counts, int polls, bool settled)
    {
        Assert.Equal(settled, ObjectRecoveryPlan.HasSettled(counts, polls));
    }
}
