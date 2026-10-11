using System;
using Godot;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>
    /// FEAT-PERF-17: Verifies that DxgiVideoMemory probes the adapter or gracefully falls back,
    /// and that the --vram-budget command line flag parses correctly.
    /// </summary>
    private static Check CheckDxgiVideoMemory()
    {
        const string Name = "dxgi video memory probe (FEAT-PERF-17)";

        // 1. Verify flag parser (A3)
        if (!Boot.TryParseVramArgs(new[] { "--vram-budget", "3500" }, out long mb1) || mb1 != 3500)
        {
            return new Check(Name, false, $"failed parsing '--vram-budget 3500': got {mb1}");
        }

        if (!Boot.TryParseVramArgs(new[] { "--vram-budget=4000" }, out long mb2) || mb2 != 4000)
        {
            return new Check(Name, false, $"failed parsing '--vram-budget=4000': got {mb2}");
        }

        if (Boot.TryParseVramArgs(new[] { "--other-flag", "value", "--vram-budget=-50" }, out _))
        {
            return new Check(Name, false, "parsed negative --vram-budget invalid value");
        }

        // 2. Verify DXGI probe (A2)
        if (!OperatingSystem.IsWindows())
        {
            return new Check(Name, true, "non-Windows skipped DXGI probe");
        }

        try
        {
            using var probe = DxgiVideoMemory.TryCreate(RenderingServer.GetVideoAdapterName());
            if (probe == null)
            {
                return new Check(Name, true, "probe returned null gracefully (no hardware DXGI adapter in headless/environment)");
            }

            bool ok = probe.TryQuery(out long budgetBytes, out long usageBytes);
            if (!ok)
            {
                return new Check(Name, false, "TryQuery failed on created probe");
            }

            // BUG-PERF-19: the whole card's usage through the performance counter, keyed by the adapter's LUID. It must
            // read at least what this process holds, and an unknown LUID must switch the probe off, not throw.
            using var counter = AdapterMemoryCounter.TryCreate(probe.LuidLow, probe.LuidHigh);
            string adapterNote = "adapter counter unavailable on this platform";
            if (counter != null)
            {
                long adapterBytes = -1;
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < 5000)
                {
                    counter.Request(unchecked((long)Time.GetTicksMsec()), 0);
                    if (counter.TryGetUsage(unchecked((long)Time.GetTicksMsec()), out adapterBytes, out _)) break;
                    System.Threading.Thread.Sleep(50);
                }
                if (adapterBytes < 0)
                    return new Check(Name, true, $"probed: osBudget={budgetBytes >> 20} MB, osUsage={usageBytes >> 20} MB; the whole-card counter gave no reading (switched itself off, which is allowed)");
                if (adapterBytes + (64L << 20) < usageBytes)
                    return new Check(Name, false, $"the whole card reads {adapterBytes >> 20} MB, below this process's own {usageBytes >> 20} MB");
                adapterNote = $"whole card {adapterBytes >> 20} of {probe.DedicatedBytes >> 20} MB";
            }

            using var unknown = AdapterMemoryCounter.TryCreate(0xDEADBEEF, 0x7EADBEEF);
            if (unknown != null)
            {
                unknown.Request(unchecked((long)Time.GetTicksMsec()), 0);
                System.Threading.Thread.Sleep(500);
                if (unknown.TryGetUsage(unchecked((long)Time.GetTicksMsec()), out long bogus, out _))
                    return new Check(Name, false, $"a counter for a LUID that does not exist read {bogus >> 20} MB");
            }

            return new Check(Name, true, $"probed: osBudget={budgetBytes >> 20} MB, osUsage={usageBytes >> 20} MB, {adapterNote}");
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"probe threw exception: {ex.Message}");
        }
    }
}
