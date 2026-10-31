using System.Collections.Generic;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-PERF-28: the arrival milestones, measured from the region handshake.</summary>
public class ArrivalTimelineTests
{
    private const long Mb = 1L << 20;
    private const long Region = 1000;

    private readonly List<string> _lines = new();
    private readonly ArrivalTimeline _timeline;

    public ArrivalTimelineTests() => _timeline = new ArrivalTimeline(_lines.Add);

    private static ArrivalSample Sample(int full = 0, int fullAllBakes = 0, int fullNoBakes = 0, int reduced = 0,
        int reducedNoBakes = 0, int facesWaiting = 0, int texInflight = 0, long bytes = 0) =>
        new(full, fullAllBakes, fullNoBakes, reduced, reducedNoBakes, facesWaiting, texInflight, bytes);

    private void Enter(double handshakeAt = 100, double currentAt = 100)
    {
        _timeline.NoteHandshake(Region, handshakeAt);
        _timeline.NoteRegionCurrent(Region, "Test Region", currentAt);
    }

    [Fact]
    public void Nothing_runs_before_a_region_is_entered()
    {
        _timeline.Update(Sample(full: 3), 5);

        Assert.False(_timeline.Active);
        Assert.Empty(_lines);
    }

    [Fact]
    public void The_clock_starts_at_the_handshake_when_it_came_just_before_the_region_became_current()
    {
        Enter(handshakeAt: 100, currentAt: 102);

        Assert.True(_timeline.Active);
        Assert.Equal(100, _timeline.StartedAt);
    }

    [Fact]
    public void The_clock_waits_for_the_handshake_when_the_region_became_current_first()
    {
        _timeline.NoteRegionCurrent(Region, "Test Region", 100);
        Assert.False(_timeline.Active);

        _timeline.NoteHandshake(Region, 101.5);

        Assert.True(_timeline.Active);
        Assert.Equal(101.5, _timeline.StartedAt);
    }

    [Fact]
    public void An_old_handshake_of_a_neighbour_that_became_current_does_not_start_the_clock_in_the_past()
    {
        _timeline.NoteHandshake(Region, 10);
        _timeline.NoteRegionCurrent(Region, "Test Region", 500);

        // The neighbour's handshake is minutes old: this is the moment of the crossing, not of the handshake.
        Assert.False(_timeline.Active);
        _timeline.NoteHandshake(Region, 500.2);
        Assert.Equal(500.2, _timeline.StartedAt);
    }

    [Fact]
    public void A_handshake_of_another_region_does_not_start_anything()
    {
        _timeline.NoteRegionCurrent(Region, "Test Region", 100);
        _timeline.NoteHandshake(2000, 100.5);

        Assert.False(_timeline.Active);
    }

    [Fact]
    public void One_arrival_line_gives_each_milestone_with_its_time_and_the_bytes_until_then()
    {
        Enter(100, 100);
        _timeline.Update(Sample(texInflight: 40, facesWaiting: 40, bytes: 10 * Mb), 101);      // baseline sample
        _timeline.Update(Sample(full: 2, fullAllBakes: 1, texInflight: 30, facesWaiting: 30, bytes: 60 * Mb), 110);
        // all full avatars have all bakes from t=120 on
        _timeline.Update(Sample(full: 2, fullAllBakes: 2, texInflight: 20, facesWaiting: 20, bytes: 110 * Mb), 120);
        _timeline.Update(Sample(full: 2, fullAllBakes: 2, texInflight: 20, facesWaiting: 20, bytes: 120 * Mb), 124);
        // no face waits from t=130 on
        _timeline.Update(Sample(full: 2, fullAllBakes: 2, texInflight: 10, facesWaiting: 0, bytes: 210 * Mb), 130);
        _timeline.Update(Sample(full: 2, fullAllBakes: 2, texInflight: 10, facesWaiting: 0, bytes: 215 * Mb), 134);
        // inflight back to 0 from t=150 on
        _timeline.Update(Sample(full: 2, fullAllBakes: 2, texInflight: 0, facesWaiting: 0, bytes: 310 * Mb), 150);
        _timeline.Update(Sample(full: 2, fullAllBakes: 2, texInflight: 0, facesWaiting: 0, bytes: 311 * Mb), 154);

        var line = Assert.Single(_lines, l => l.StartsWith("[Arrival] "));
        Assert.Contains("region=Test Region", line);
        // Times are from the handshake (t=100), bytes from the first sample (10 MB).
        Assert.Contains("bakes=+20.0s/100.0MB", line);
        Assert.Contains("noFaceWithoutTexture=+30.0s/200.0MB", line);
        Assert.Contains("texInflight0=+50.0s/300.0MB", line);
        Assert.Contains("fullAvatars=2", line);
    }

    [Fact]
    public void A_milestone_that_flaps_back_before_it_settled_is_not_reached()
    {
        Enter(100, 100);
        _timeline.Update(Sample(texInflight: 5, facesWaiting: 5, bytes: Mb), 101);
        // zero for one second only, then the next wave of requests
        _timeline.Update(Sample(texInflight: 0, facesWaiting: 0, bytes: 2 * Mb), 110);
        _timeline.Update(Sample(texInflight: 7, facesWaiting: 7, bytes: 3 * Mb), 111);
        _timeline.Update(Sample(texInflight: 7, facesWaiting: 7, bytes: 4 * Mb), 120);

        Assert.DoesNotContain(_lines, l => l.StartsWith("[Arrival] "));
        Assert.True(_timeline.Active);
    }

    [Fact]
    public void A_milestone_that_was_never_armed_is_not_reached_by_an_empty_pipe_alone()
    {
        Enter(100, 100);
        // Nothing was ever requested: an idle pipe at t=0 is not "everything arrived".
        for (double t = 101; t < 120; t += 1) _timeline.Update(Sample(bytes: 0), t);

        Assert.DoesNotContain(_lines, l => l.StartsWith("[Arrival] "));
    }

    [Fact]
    public void Milestones_not_reached_by_the_give_up_time_print_as_never()
    {
        Enter(100, 100);
        _timeline.Update(Sample(full: 3, fullAllBakes: 1, texInflight: 50, facesWaiting: 50, bytes: Mb), 101);
        _timeline.Update(Sample(full: 3, fullAllBakes: 1, texInflight: 50, facesWaiting: 50, bytes: 900 * Mb), 100 + ArrivalTimeline.GiveUpSeconds + 1);

        var line = Assert.Single(_lines, l => l.StartsWith("[Arrival] "));
        Assert.Contains("bakes=never", line);
        Assert.Contains("noFaceWithoutTexture=never", line);
        Assert.Contains("texInflight0=never", line);
        Assert.Contains($"after {(int)ArrivalTimeline.GiveUpSeconds}s", line);
    }

    [Fact]
    public void A_region_without_full_avatars_never_reaches_the_bake_milestone()
    {
        Enter(100, 100);
        _timeline.Update(Sample(texInflight: 3, facesWaiting: 3, bytes: Mb), 101);
        _timeline.Update(Sample(full: 0, fullAllBakes: 0, texInflight: 0, facesWaiting: 0, bytes: 2 * Mb), 110);
        _timeline.Update(Sample(full: 0, fullAllBakes: 0, texInflight: 0, facesWaiting: 0, bytes: 2 * Mb), 114);
        _timeline.Update(Sample(full: 0, fullAllBakes: 0, texInflight: 0, facesWaiting: 0, bytes: 2 * Mb), 100 + ArrivalTimeline.GiveUpSeconds + 1);

        var line = Assert.Single(_lines, l => l.StartsWith("[Arrival] "));
        Assert.Contains("bakes=never", line);
        Assert.Contains("texInflight0=+", line);
    }

    [Fact]
    public void Reduced_and_bakeless_full_avatars_are_counted_at_plus_30_and_plus_60_seconds()
    {
        Enter(100, 100);
        _timeline.Update(Sample(full: 13, fullAllBakes: 10, fullNoBakes: 3, reduced: 56, reducedNoBakes: 50, texInflight: 900, facesWaiting: 400, bytes: Mb), 101);
        _timeline.Update(Sample(full: 13, fullAllBakes: 10, fullNoBakes: 3, reduced: 56, reducedNoBakes: 50, texInflight: 900, facesWaiting: 400, bytes: 2 * Mb), 129);
        Assert.DoesNotContain(_lines, l => l.StartsWith("[ArrivalCount]"));

        _timeline.Update(Sample(full: 13, fullAllBakes: 10, fullNoBakes: 3, reduced: 56, reducedNoBakes: 50, texInflight: 900, facesWaiting: 400, bytes: 3 * Mb), 131);
        _timeline.Update(Sample(full: 13, fullAllBakes: 12, fullNoBakes: 1, reduced: 50, reducedNoBakes: 44, texInflight: 800, facesWaiting: 300, bytes: 4 * Mb), 161);

        var counts = _lines.FindAll(l => l.StartsWith("[ArrivalCount]"));
        Assert.Equal(2, counts.Count);
        Assert.Contains("+30s", counts[0]);
        Assert.Contains("reduced=56", counts[0]);
        Assert.Contains("reducedNoBakes=50", counts[0]);
        Assert.Contains("fullNoBakes=3", counts[0]);
        Assert.Contains("+60s", counts[1]);
        Assert.Contains("reduced=50", counts[1]);
        Assert.Contains("fullNoBakes=1", counts[1]);
    }

    [Fact]
    public void Each_count_is_written_once_however_often_it_is_sampled_afterwards()
    {
        Enter(100, 100);
        for (double t = 101; t < 200; t += 1) _timeline.Update(Sample(full: 1, reduced: 1, texInflight: 5, facesWaiting: 5, bytes: Mb), t);

        Assert.Equal(2, _lines.FindAll(l => l.StartsWith("[ArrivalCount]")).Count);
    }

    [Fact]
    public void The_next_region_closes_an_unfinished_arrival_with_what_it_had()
    {
        Enter(100, 100);
        _timeline.Update(Sample(full: 2, fullAllBakes: 2, texInflight: 5, facesWaiting: 5, bytes: Mb), 101);
        _timeline.Update(Sample(full: 2, fullAllBakes: 2, texInflight: 5, facesWaiting: 5, bytes: 5 * Mb), 106);

        _timeline.NoteHandshake(2000, 200);
        _timeline.NoteRegionCurrent(2000, "Next", 200);

        var line = Assert.Single(_lines, l => l.StartsWith("[Arrival] "));
        Assert.Contains("bakes=+", line);
        Assert.Contains("texInflight0=never", line);
        Assert.Contains("left for another region", line);
        Assert.True(_timeline.Active);
        Assert.Equal(200, _timeline.StartedAt);
    }

    [Fact]
    public void A_new_arrival_starts_from_scratch()
    {
        Enter(100, 100);
        _timeline.Update(Sample(texInflight: 5, facesWaiting: 5, bytes: Mb), 101);
        _timeline.Update(Sample(texInflight: 0, facesWaiting: 0, bytes: 2 * Mb), 110);
        _timeline.Update(Sample(texInflight: 0, facesWaiting: 0, bytes: 2 * Mb), 114);

        _timeline.NoteHandshake(2000, 300);
        _timeline.NoteRegionCurrent(2000, "Next", 300);   // closes the first arrival (its own line)
        int before = _lines.Count;
        _timeline.Update(Sample(texInflight: 0, facesWaiting: 0, bytes: 50 * Mb), 301);
        _timeline.Update(Sample(texInflight: 0, facesWaiting: 0, bytes: 50 * Mb), 310);

        // Nothing carries over: the new arrival has not seen anything wait yet, so it has reached nothing.
        Assert.DoesNotContain(_lines.GetRange(before, _lines.Count - before), l => l.StartsWith("[Arrival] "));
    }
}
