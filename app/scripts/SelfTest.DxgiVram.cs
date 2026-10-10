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

            return new Check(Name, true, $"probed: osBudget={budgetBytes >> 20} MB, osUsage={usageBytes >> 20} MB");
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"probe threw exception: {ex.Message}");
        }
    }
}
