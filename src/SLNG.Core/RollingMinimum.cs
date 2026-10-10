using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>
/// FEAT-PERF-17: the smallest value seen within a sliding time window.
///
/// <para>Windows' per-process video-memory budget (DXGI <c>QueryVideoMemoryInfo</c>) moves by several
/// hundred MB from one reading to the next while another program is busy on the card (measured
/// 2026-10-10 with Firestorm open: 8.8 / 9.5 / 9.7 / 9.0 / 9.2 GB two seconds apart). Fed straight into
/// <see cref="VramBudgetPolicy"/>, the texture budget followed it every 2 s, and each step down started a
/// shrink pass that the next step up undid. The minimum over the window falls at once and only rises
/// after the low readings have aged out, which is the conservative reading the budget needs.</para>
/// </summary>
public sealed class RollingMinimum
{
    private readonly double _windowSeconds;
    private readonly Queue<(double Time, long Value)> _samples = new();

    public RollingMinimum(double windowSeconds) => _windowSeconds = windowSeconds;

    /// <summary>Adds a reading taken at <paramref name="time"/> (seconds, monotonic) and returns the
    /// minimum of the readings no older than the window, this one included.</summary>
    public long Add(double time, long value)
    {
        _samples.Enqueue((time, value));
        while (_samples.Count > 1 && time - _samples.Peek().Time > _windowSeconds)
            _samples.Dequeue();

        long min = long.MaxValue;
        foreach (var (_, v) in _samples)
            if (v < min) min = v;
        return min;
    }

    public void Clear() => _samples.Clear();
}
