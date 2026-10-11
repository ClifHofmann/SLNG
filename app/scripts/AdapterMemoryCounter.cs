using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace SLNG.App;

/// <summary>
/// BUG-PERF-19: how much of the card's dedicated video memory ALL processes hold, not just ours.
/// DXGI's <c>QueryVideoMemoryInfo</c> (<see cref="DxgiVideoMemory"/>) is per process; in-world 2026-10-11 the
/// card was 11.3 of 12 GB full of the viewer, Sunshine, a slicer and a browser, the viewer's own budget still
/// read 9.0 GB, and the frame rate fell from 40 to 7 because the GPU was paging. The figure Task Manager shows
/// for the whole GPU is the performance counter <c>\GPU Adapter Memory(*)\Dedicated Usage</c>, one instance per
/// adapter and node (<c>luid_0x00000000_0x00013E3C_phys_0</c>), read here through <c>pdh.dll</c>.
///
/// <para>No unsafe code and no new package: the formatted-array buffer is walked with <see cref="Marshal"/>.
/// A refresh is a few hundred microseconds to a few milliseconds, so it runs on the thread pool, at most one at
/// a time and at most every few seconds; the caller reads the last result. Anything that goes wrong (counter
/// missing, access denied, a non-Windows machine) switches the probe off for the session with one log line and
/// <see cref="TryGetUsage"/> keeps returning false, which leaves the texture budget exactly as it was before
/// the probe existed.</para>
/// </summary>
public sealed class AdapterMemoryCounter : IDisposable
{
    private const string CounterPath = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const uint PdhFmtLarge = 0x00000400;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhCstatusValidData = 0;
    private const uint PdhCstatusNewData = 1;

    // PDH_FMT_COUNTERVALUE_ITEM_W: LPWSTR szName; PDH_FMT_COUNTERVALUE { DWORD CStatus; union (8 bytes, 8-aligned) }.
    private const int ItemStride = 24;
    private const int ItemStatusOffset = 8;
    private const int ItemValueOffset = 16;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    private readonly string _instancePrefix;
    private readonly object _gate = new();
    private IntPtr _query;
    private IntPtr _counter;
    private int _inFlight;
    private volatile bool _failed;
    private volatile bool _disposed;
    private long _usageBytes = -1;
    private long _sampledAtMs;
    private long _requestedAtMs = -1_000_000_000L;

    private AdapterMemoryCounter(uint luidLow, int luidHigh)
    {
        _instancePrefix = $"luid_0x{unchecked((uint)luidHigh):X8}_0x{luidLow:X8}_phys_";
    }

    /// <summary>The probe for the adapter with this LUID (<c>DXGI_ADAPTER_DESC1</c>), or null off Windows.
    /// Nothing is opened until the first <see cref="Request"/>.</summary>
    public static AdapterMemoryCounter? TryCreate(uint luidLow, int luidHigh)
        => OperatingSystem.IsWindows() ? new AdapterMemoryCounter(luidLow, luidHigh) : null;

    /// <summary>Asks for a fresh reading unless one is already on its way or the last request was less than
    /// <paramref name="minIntervalMs"/> ago. Returns at once; the reading arrives on a pool thread.</summary>
    public void Request(long nowMs, long minIntervalMs)
    {
        if (_failed || _disposed) return;
        if (nowMs - _requestedAtMs < minIntervalMs) return;
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return;
        _requestedAtMs = nowMs;
        Task.Run(() =>
        {
            try { Sample(); }
            finally { Interlocked.Exchange(ref _inFlight, 0); }
        });
    }

    /// <summary>The last reading of the whole adapter's dedicated usage and how old it is. False before the
    /// first reading arrives, after the probe switched itself off, and (for the caller to decide) when the age
    /// is too large to believe.</summary>
    public bool TryGetUsage(long nowMs, out long usageBytes, out double ageSeconds)
    {
        usageBytes = Interlocked.Read(ref _usageBytes);
        ageSeconds = (nowMs - Interlocked.Read(ref _sampledAtMs)) / 1000.0;
        return !_failed && usageBytes >= 0;
    }

    private void Sample()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || _failed) return;
                if (_query == IntPtr.Zero && !Open()) return;

                uint rc = PdhCollectQueryData(_query);
                if (rc != 0) { Fail($"PdhCollectQueryData 0x{rc:X8}"); return; }

                uint size = 0, count = 0;
                rc = PdhGetFormattedCounterArrayW(_counter, PdhFmtLarge, ref size, ref count, IntPtr.Zero);
                if (rc != PdhMoreData || size == 0) { Fail($"PdhGetFormattedCounterArray (size) 0x{rc:X8}"); return; }

                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    rc = PdhGetFormattedCounterArrayW(_counter, PdhFmtLarge, ref size, ref count, buffer);
                    if (rc != 0) { Fail($"PdhGetFormattedCounterArray 0x{rc:X8}"); return; }

                    long total = 0;
                    bool matched = false;
                    for (int i = 0; i < (int)count; i++)
                    {
                        IntPtr item = IntPtr.Add(buffer, i * ItemStride);
                        string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                        if (name == null || !name.StartsWith(_instancePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                        uint status = unchecked((uint)Marshal.ReadInt32(item, ItemStatusOffset));
                        if (status != PdhCstatusValidData && status != PdhCstatusNewData) continue;
                        total += Marshal.ReadInt64(item, ItemValueOffset);
                        matched = true;
                    }

                    if (!matched) { Fail($"no counter instance for {_instancePrefix}* among {count}"); return; }

                    Interlocked.Exchange(ref _usageBytes, total);
                    Interlocked.Exchange(ref _sampledAtMs, unchecked((long)Time.GetTicksMsec()));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private bool Open()
    {
        uint rc = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (rc != 0) { _query = IntPtr.Zero; Fail($"PdhOpenQuery 0x{rc:X8}"); return false; }
        rc = PdhAddEnglishCounterW(_query, CounterPath, IntPtr.Zero, out _counter);
        if (rc != 0) { Fail($"PdhAddEnglishCounter 0x{rc:X8}"); return false; }
        return true;
    }

    private void Fail(string why)
    {
        if (_failed) return;
        _failed = true;
        GD.PrintErr($"[VramBudget] adapter-wide video memory counter unavailable ({why}); the texture budget uses the per-process figures only");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            if (_query != IntPtr.Zero)
            {
                try { PdhCloseQuery(_query); } catch { }
                _query = IntPtr.Zero;
                _counter = IntPtr.Zero;
            }
        }
    }
}
