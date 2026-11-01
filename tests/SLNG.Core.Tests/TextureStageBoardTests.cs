using System;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-PERF-30: where a texture a face asked for spends its time between the request and the bound texture.</summary>
public class TextureStageBoardTests
{
    private long _micros;
    private readonly TextureStageBoard _board;

    public TextureStageBoardTests() => _board = new TextureStageBoard(() => _micros);

    private void Advance(double ms) => _micros += (long)(ms * 1000);

    [Fact]
    public void A_texture_is_counted_in_the_stage_it_is_in_and_its_waiting_faces_with_it()
    {
        var id = Guid.NewGuid();
        _board.Open(id);
        _board.AddFace(id);
        _board.AddFace(id);
        _board.Enter(id, TexStage.DecodeWait);

        var s = _board.Gauge(TexStage.DecodeWait);

        Assert.Equal(1, s.Textures);
        Assert.Equal(2, s.Faces);
        Assert.Equal(0, _board.Gauge(TexStage.Pool).Textures);
    }

    [Fact]
    public void A_texture_starts_in_the_pool_stage_before_anything_picks_it_up()
    {
        _board.Open(Guid.NewGuid());

        Assert.Equal(1, _board.Gauge(TexStage.Pool).Textures);
    }

    [Fact]
    public void Faces_that_join_later_are_counted_where_the_texture_is_by_then()
    {
        var id = Guid.NewGuid();
        _board.Open(id);
        _board.AddFace(id);
        _board.Enter(id, TexStage.Decode);
        _board.AddFace(id);

        Assert.Equal(2, _board.Gauge(TexStage.Decode).Faces);
        Assert.Equal(0, _board.Gauge(TexStage.Pool).Faces);
    }

    [Fact]
    public void Leaving_a_stage_records_how_long_the_texture_spent_in_it()
    {
        var id = Guid.NewGuid();
        _board.Open(id);
        Advance(5);
        _board.Enter(id, TexStage.Disk);
        Advance(20);
        _board.Enter(id, TexStage.DecodeWait);
        Advance(400);
        _board.Enter(id, TexStage.Decode);

        Assert.Equal(5, _board.TakeStage(TexStage.Pool).P50, 3);
        Assert.Equal(20, _board.TakeStage(TexStage.Disk).P50, 3);
        Assert.Equal(400, _board.TakeStage(TexStage.DecodeWait).P50, 3);
    }

    [Fact]
    public void The_stage_a_texture_is_still_in_has_no_duration_yet_only_an_age()
    {
        var id = Guid.NewGuid();
        _board.Open(id);
        _board.Enter(id, TexStage.QueueWait);
        Advance(1500);

        var s = _board.TakeStage(TexStage.QueueWait);

        Assert.Equal(0, s.Count);
        Assert.Equal(1, s.OpenTextures);
        Assert.Equal(1500, s.OldestOpenMs, 3);
    }

    [Fact]
    public void Closing_a_texture_takes_it_out_of_the_gauges_and_records_its_last_stage_and_its_total()
    {
        var id = Guid.NewGuid();
        _board.Open(id);
        _board.AddFace(id);
        Advance(10);
        _board.Enter(id, TexStage.Upload);
        Advance(2);
        _board.Close(id);

        Assert.Equal(0, _board.Gauge(TexStage.Upload).Textures);
        Assert.Equal(0, _board.Gauge(TexStage.Upload).Faces);
        Assert.Equal(2, _board.TakeStage(TexStage.Upload).P50, 3);
        Assert.Equal(12, _board.TakeTotal().P50, 3);
    }

    [Fact]
    public void Entering_done_stops_the_clock_on_upload_but_keeps_the_texture_until_it_is_closed()
    {
        var id = Guid.NewGuid();
        _board.Open(id);
        _board.Enter(id, TexStage.Upload);
        Advance(3);
        _board.Enter(id, TexStage.Done);
        Advance(50);             // the continuation that resumes after the upload is not upload time
        _board.Close(id);

        Assert.Equal(3, _board.TakeStage(TexStage.Upload).P50, 3);
        Assert.Equal(0, _board.Gauge(TexStage.Upload).Textures);
    }

    [Fact]
    public void Entering_the_stage_it_is_already_in_changes_nothing()
    {
        var id = Guid.NewGuid();
        _board.Open(id);
        _board.AddFace(id);
        _board.Enter(id, TexStage.Net);
        Advance(10);
        _board.Enter(id, TexStage.Net);

        Assert.Equal(1, _board.Gauge(TexStage.Net).Textures);
        Assert.Equal(0, _board.TakeStage(TexStage.Net).Count);
    }

    [Fact]
    public void A_texture_nobody_opened_is_ignored_so_other_callers_of_the_asset_service_cost_nothing()
    {
        var stranger = Guid.NewGuid();

        _board.Enter(stranger, TexStage.Decode);
        _board.AddFace(stranger);
        _board.Close(stranger);

        Assert.Equal(0, _board.Gauge(TexStage.Decode).Textures);
        Assert.Equal(0, _board.TakeTotal().Count);
    }

    [Fact]
    public void A_face_that_arrives_after_the_texture_closed_is_not_counted_anywhere()
    {
        var id = Guid.NewGuid();
        _board.Open(id);
        _board.Close(id);

        _board.AddFace(id);

        for (var s = TexStage.Pool; s < TexStage.Done; s++) Assert.Equal(0, _board.Gauge(s).Faces);
    }

    [Fact]
    public void Opening_a_texture_that_is_already_open_does_not_start_it_over()
    {
        var id = Guid.NewGuid();
        _board.Open(id);
        Advance(100);
        _board.Enter(id, TexStage.Disk);
        _board.Open(id);

        Assert.Equal(1, _board.Gauge(TexStage.Disk).Textures);
        Assert.Equal(0, _board.Gauge(TexStage.Pool).Textures);
    }

    [Fact]
    public void Faces_waiting_in_every_stage_add_up_to_the_faces_of_the_open_textures()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        foreach (var id in new[] { a, b, c }) { _board.Open(id); _board.AddFace(id); }
        _board.AddFace(b);
        _board.Enter(a, TexStage.Disk);
        _board.Enter(b, TexStage.DecodeWait);
        _board.Enter(c, TexStage.QueueWait);
        _board.Close(c);

        long faces = 0;
        for (var s = TexStage.Pool; s < TexStage.Done; s++) faces += _board.Gauge(s).Faces;
        Assert.Equal(3, faces);
    }

    [Fact]
    public void Reading_an_interval_starts_the_next_one_but_leaves_the_gauges()
    {
        var id = Guid.NewGuid();
        _board.Open(id);
        Advance(4);
        _board.Enter(id, TexStage.Disk);

        Assert.Equal(1, _board.TakeStage(TexStage.Pool).Count);
        Assert.Equal(0, _board.TakeStage(TexStage.Pool).Count);
        Assert.Equal(1, _board.Gauge(TexStage.Disk).Textures);
    }

    [Fact]
    public void The_ways_a_texture_was_produced_and_the_gpu_cache_lookups_are_counted_per_interval()
    {
        _board.NoteLookup(hit: true);
        _board.NoteLookup(hit: true);
        _board.NoteLookup(hit: false);
        _board.NotePath(TexPath.Copy);
        _board.NotePath(TexPath.DecodedCache);
        _board.NotePath(TexPath.DecodedCache);
        _board.NotePath(TexPath.Http);

        var c = _board.TakeCounters();

        Assert.Equal(3, c.Lookups);
        Assert.Equal(2, c.LookupHits);
        Assert.Equal(1, c.Copy);
        Assert.Equal(2, c.DecodedCache);
        Assert.Equal(0, c.Decode);
        Assert.Equal(1, c.Http);
        Assert.Equal(0, _board.TakeCounters().Lookups);
    }

    [Fact]
    public void A_copy_attempt_that_found_nothing_is_counted_apart_from_a_hit()
    {
        _board.NoteCopyAttempt(found: false);
        _board.NoteCopyAttempt(found: true);
        _board.NoteCopyAttempt(found: false);

        var c = _board.TakeCounters();

        Assert.Equal(3, c.CopyAttempts);
        Assert.Equal(1, c.CopyFound);
    }

    [Fact]
    public void The_arrival_count_names_only_the_stages_that_hold_faces()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        _board.Open(a); _board.AddFace(a); _board.AddFace(a);
        _board.Open(b); _board.AddFace(b);
        _board.Enter(a, TexStage.DecodeWait);
        _board.Enter(b, TexStage.QueueWait);

        string text = _board.DescribeWaiting();

        Assert.Equal("decodeWait=2f/1t queueWait=1f/1t", text);
    }

    [Fact]
    public void The_arrival_count_says_so_when_nothing_waits()
    {
        Assert.Equal("none", _board.DescribeWaiting());
    }

    [Fact]
    public void A_stage_line_carries_the_interval_the_gauge_and_the_oldest_wait()
    {
        var id = Guid.NewGuid();
        _board.Open(id);
        _board.AddFace(id);
        Advance(2);
        _board.Enter(id, TexStage.Disk);
        Advance(30);
        _board.Enter(id, TexStage.DecodeWait);
        Advance(700);

        string? disk = _board.TakeStageLine(TexStage.Disk);
        string? wait = _board.TakeStageLine(TexStage.DecodeWait);

        Assert.StartsWith("[TexStage.Disk] n=1 p50=30ms p95=30ms max=30ms open=0", disk);
        Assert.StartsWith("[TexStage.DecodeWait] n=0 p50=0ms p95=0ms max=0ms open=1 faces=1 oldest=700ms", wait);
    }

    [Fact]
    public void A_stage_with_no_traffic_and_nothing_open_prints_no_line()
    {
        Assert.Null(_board.TakeStageLine(TexStage.Decode));
    }

    [Fact]
    public void Counts_stay_exact_when_many_threads_open_enter_and_close_textures()
    {
        var board = new TextureStageBoard();
        System.Threading.Tasks.Parallel.For(0, 2000, i =>
        {
            var id = Guid.NewGuid();
            board.Open(id);
            board.AddFace(id);
            board.Enter(id, TexStage.Disk);
            board.AddFace(id);
            board.Enter(id, TexStage.Decode);
            board.Enter(id, TexStage.Done);
            board.Close(id);
        });

        for (var s = TexStage.Pool; s < TexStage.Done; s++)
        {
            Assert.Equal(0, board.Gauge(s).Textures);
            Assert.Equal(0, board.Gauge(s).Faces);
        }
        Assert.Equal(2000, board.TakeTotal().Count);
    }
}
