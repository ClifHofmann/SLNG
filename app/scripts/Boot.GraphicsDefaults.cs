using System;
using Godot;
using SLNG.App;
using SLNG.Core;

/// <summary>
/// FEAT-PERF-26: a new install starts on the graphics preset its machine suits, the way the reference viewer
/// picks one on its first start. <see cref="SLNG.App.UI.GraphicsSettings.IsFirstRun"/> is true only when no graphics
/// setting was ever saved; whoever has chosen anything keeps it.
/// </summary>
public partial class Boot
{
    private void PickFirstRunGraphics()
    {
        if (!_graphicsSettings.IsFirstRun) return;

        string adapter = RenderingServer.GetVideoAdapterName();
        bool integrated = RenderingServer.GetVideoAdapterType() == RenderingDevice.DeviceType.IntegratedGpu;

        // Dedicated video memory through DXGI, as the VRAM budget reads it. Elsewhere it stays 0, "unknown".
        long vramMb = 0;
        if (OperatingSystem.IsWindows())
        {
            using var probe = DxgiVideoMemory.TryCreate(adapter);
            if (probe != null) vramMb = probe.DedicatedBytes >> 20;
        }

        int cores = OS.GetProcessorCount();
        long physical = OS.GetMemoryInfo().TryGetValue("physical", out var value) ? (long)value : -1;
        long ramMb = physical > 0 ? physical >> 20 : -1;

        var tier = GraphicsRecommendation.Recommend(vramMb, integrated, cores, ramMb);
        _graphicsSettings.ApplyRecommended(tier);
        GD.Print($"[Graphics] first start: {adapter} ({(integrated ? "integrated" : "discrete")}), " +
                 $"{vramMb} MB video memory, {cores} logical cores, {ramMb} MB RAM -> {tier}");
    }
}
