using System;

namespace SLNG.Core;

/// <summary>
/// FEAT-PERF-30: the durations seen in one reporting interval, reduced to a count, a median, a 95th percentile and a maximum.
///
/// <para>The percentile rule is <c>sorted[(int)(n * p)]</c>, the same as <see cref="FetchPipeStats"/>, so a stage's p95 can be read
/// beside the <c>[TexWait]</c> and <c>[TexHttp]</c> figures of the same interval. Past <c>capacity</c> samples a new one overwrites an
/// old one: the percentiles then describe the interval's tail, while <see cref="Summary.Count"/> and <see cref="Summary.Max"/> stay
/// true to every sample.</para>
///
/// <para>Thread-safe; the lock is held for a few instructions per sample.</para>
/// </summary>
public sealed class SampleWindow
{
    /// <summary>What <see cref="Take"/> hands back.</summary>
    public readonly record struct Summary(int Count, double P50, double P95, double Max);

    private readonly object _lock = new();
    private readonly double[] _samples;
    private int _count;
    private double _max;

    public SampleWindow(int capacity = 4096) => _samples = new double[Math.Max(8, capacity)];

    public void Add(double value)
    {
        lock (_lock)
        {
            _samples[_count % _samples.Length] = value;
            _count++;
            if (value > _max) _max = value;
        }
    }

    /// <summary>The summary of the samples since the previous call; starts a new interval.</summary>
    public Summary Take()
    {
        double[] copy;
        int total;
        double max;
        lock (_lock)
        {
            total = _count;
            max = _max;
            int n = Math.Min(total, _samples.Length);
            copy = new double[n];
            Array.Copy(_samples, copy, n);
            _count = 0;
            _max = 0;
        }
        if (copy.Length == 0) return default;
        Array.Sort(copy);
        int last = copy.Length - 1;
        return new Summary(total, copy[Math.Min(last, (int)(copy.Length * 0.50))], copy[Math.Min(last, (int)(copy.Length * 0.95))], max);
    }
}
