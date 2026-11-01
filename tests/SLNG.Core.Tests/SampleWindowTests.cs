using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-PERF-30: the median and 95th percentile of one reporting interval.</summary>
public class SampleWindowTests
{
    [Fact]
    public void An_empty_window_reports_nothing()
    {
        var s = new SampleWindow().Take();

        Assert.Equal(0, s.Count);
        Assert.Equal(0, s.P50);
        Assert.Equal(0, s.P95);
        Assert.Equal(0, s.Max);
    }

    [Fact]
    public void One_sample_is_every_percentile()
    {
        var w = new SampleWindow();
        w.Add(7);

        var s = w.Take();

        Assert.Equal(1, s.Count);
        Assert.Equal(7, s.P50);
        Assert.Equal(7, s.P95);
        Assert.Equal(7, s.Max);
    }

    [Fact]
    public void Percentiles_use_the_same_index_rule_as_the_fetch_pipe_lines_so_the_figures_compare()
    {
        var w = new SampleWindow();
        for (int i = 1; i <= 100; i++) w.Add(i);   // added in order; the window sorts

        var s = w.Take();

        // FetchPipeStats picks sorted[(int)(n * p)]: 51st and 96th of 100.
        Assert.Equal(100, s.Count);
        Assert.Equal(51, s.P50);
        Assert.Equal(96, s.P95);
        Assert.Equal(100, s.Max);
    }

    [Fact]
    public void The_order_samples_arrive_in_does_not_matter()
    {
        var w = new SampleWindow();
        foreach (double v in new[] { 50.0, 10, 90, 30, 70 }) w.Add(v);

        var s = w.Take();

        Assert.Equal(50, s.P50);
        Assert.Equal(90, s.Max);
    }

    [Fact]
    public void Taking_the_window_starts_a_new_interval()
    {
        var w = new SampleWindow();
        w.Add(100);
        w.Take();
        w.Add(3);

        var s = w.Take();

        Assert.Equal(1, s.Count);
        Assert.Equal(3, s.Max);
    }

    [Fact]
    public void Past_the_capacity_the_count_stays_true_and_the_percentiles_describe_the_newest_samples()
    {
        var w = new SampleWindow(capacity: 8);
        for (int i = 1; i <= 20; i++) w.Add(i);

        var s = w.Take();

        Assert.Equal(20, s.Count);
        Assert.Equal(20, s.Max);          // the newest eight are 13..20
        Assert.True(s.P50 >= 13);
    }
}
