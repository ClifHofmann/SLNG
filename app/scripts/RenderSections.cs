using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Godot;

namespace SLNG.App;

/// <summary>
/// FEAT-PERF-24: the render thread's <c>_draw</c> cut at the timestamps Godot captures inside it, so
/// the part of it no viewport timer covers has a name.
///
/// <para><see cref="FrameTimeline"/> brackets each draw on the render thread (<c>drawCallMs</c>).
/// Inside that bracket, <c>renderCpuMs</c> covers only the viewports' own renders (between each
/// <c>vp_begin</c> / <c>vp_end</c> capture of <c>RendererViewport::_draw_viewport</c>) and
/// <c>setupMs</c> the scene and canvas update. The rest of <c>RenderingServerDefault::_draw</c>
/// (4.7-stable, <c>rendering_server_default.cpp:76-150</c>) is <c>render_probes</c> before the first
/// viewport, and after the last one the blit to the screen (swap-chain acquire),
/// <c>canvas_render->update</c> and <c>end_frame</c>. <c>end_frame</c> is
/// <c>RenderingDevice::swap_buffers</c>: <c>draw_graph.end()</c>, which records every draw of the frame
/// into the Vulkan command buffer (the viewport timers only time adding them to the render graph),
/// then submit and present, then the fence wait for the next frame slot.</para>
///
/// <para>Each capture carries the CPU time it was taken at (<c>capture_timestamp</c>:
/// <c>OS::get_ticks_usec()</c>), so against the bracket the viewport captures give
/// <see cref="PreMs"/> / <see cref="ViewportsMs"/> / <see cref="PostMs"/>. Godot hands a frame's
/// captures out once that frame's slot comes round again (<c>RenderingDevice::_begin_frame</c>), a frame
/// or two later, so the brackets are kept in a short ring and each capture set is matched to the bracket
/// it lies in.</para>
///
/// <para>With Godot's own <c>--gpu-profile</c> every <c>RENDER_TIMESTAMP</c> inside the draw is captured
/// too ("Render ReflectionProbes", "&gt; Render Viewports", the scene passes), and <see cref="Describe"/>
/// names the CPU time from each capture to the next.</para>
///
/// <para><see cref="OnDrawEnd"/> runs on the render thread, from FrameTimeline's draw-end marker. It
/// calls only RenderingDevice's captured-timestamp getters, which Godot requires on that thread
/// (<c>ERR_RENDER_THREAD_GUARD</c>), and never the RenderingServer. --diag on a separate render thread
/// only.</para>
/// </summary>
internal static class RenderSections
{
    private const int RingSize = 8;
    private const string StartLabel = "(start)";

    // Render thread only.
    private static readonly (long Start, long End)[] _ring = new (long, long)[RingSize];
    private static int _ringNext;
    private static double[] _cpu = new double[64];
    private static string[] _names = new string[64];
    private static bool _failed;

    private static RenderingDevice? _rd;
    private static double _usecPerTick;
    private static double _offsetUsec; // Godot's get_ticks_usec minus Stopwatch time, set on main

    // Filled on the render thread, emptied by Settle on main.
    private static readonly object _gate = new();
    private static double _preSum, _viewportsSum, _postSum;
    private static int _frames;
    private static readonly Dictionary<string, double> _spanSums = new();
    private static int _spanFrames;
    private static bool _sawProfileCaptures;
    private static List<KeyValuePair<string, double>> _lastSpans = new();

    /// <summary>Per drawn frame of the last window, else -1 (every bracketed draw whose captures came
    /// back, which is not exactly <c>drawCallMs</c>'s set of frames, so the two can differ while the
    /// region loads): bracket start to the first viewport's
    /// <c>vp_begin</c> (scene/canvas update, probes), first <c>vp_begin</c> to last <c>vp_end</c> (the
    /// viewports, about <c>renderCpuMs</c>), last <c>vp_end</c> to bracket end (blit, canvas,
    /// <c>end_frame</c>).</summary>
    public static double PreMs { get; private set; } = -1;
    public static double ViewportsMs { get; private set; } = -1;
    public static double PostMs { get; private set; } = -1;

    /// <summary>On main, before the first draw-end marker is queued.</summary>
    public static void Install()
    {
        _rd = RenderingServer.GetRenderingDevice(); // null on the Compatibility renderer
        _usecPerTick = 1_000_000.0 / Stopwatch.Frequency;
        Calibrate();
    }

    /// <summary>Both clocks are the same counter on Windows (QueryPerformanceCounter); the offset is
    /// Godot's start time. Taken again every window, so it cannot drift on another platform.</summary>
    private static void Calibrate()
    {
        long before = Stopwatch.GetTimestamp();
        ulong godotUsec = Time.GetTicksUsec();
        long after = Stopwatch.GetTimestamp();
        Volatile.Write(ref _offsetUsec, godotUsec - (before + after) * 0.5 * _usecPerTick);
    }

    /// <summary>Render thread: one draw has ended. Records its bracket, unless FrameTimeline found its
    /// end marker late (<paramref name="usable"/> false: the bracket may hold queued commands, and
    /// <c>drawCallMs</c> leaves that draw out too), and splits whichever earlier draw Godot's captures
    /// now describe. The captures are read every frame either way: each set is handed out only once.</summary>
    public static void OnDrawEnd(long start, long end, bool usable)
    {
        var rd = _rd;
        if (rd == null || _failed) return;
        if (usable)
        {
            _ring[_ringNext] = (start, end);
            _ringNext = (_ringNext + 1) % RingSize;
        }
        try
        {
            ReadCaptures(rd);
        }
        catch (Exception)
        {
            _failed = true; // nothing to report from this thread; the fields stay at -1
        }
    }

    private static void ReadCaptures(RenderingDevice rd)
    {
        int count = (int)rd.GetCapturedTimestampsCount();
        if (count == 0) return;
        if (_cpu.Length < count)
        {
            _cpu = new double[count * 2];
            _names = new string[count * 2];
        }
        for (int i = 0; i < count; i++)
        {
            _cpu[i] = rd.GetCapturedTimestampCpuTime((uint)i);
            _names[i] = rd.GetCapturedTimestampName((uint)i);
        }

        // The bracket these were captured in. Each set is handed out once, and each bracket is used once.
        double offset = Volatile.Read(ref _offsetUsec);
        double first = _cpu[0], last = _cpu[count - 1], start = 0, end = 0;
        int match = -1;
        for (int j = 0; j < RingSize && match < 0; j++)
        {
            var (s, e) = _ring[j];
            if (s == 0) continue;
            start = s * _usecPerTick + offset;
            end = e * _usecPerTick + offset;
            if (start <= first && last <= end) match = j;
        }
        if (match < 0) return;
        _ring[match] = default;

        double vpBegin = double.MaxValue, vpEnd = double.MinValue;
        bool profiled = false;
        for (int i = 0; i < count; i++)
        {
            string name = _names[i];
            if (name.StartsWith("vp_begin_", StringComparison.Ordinal)) vpBegin = Math.Min(vpBegin, _cpu[i]);
            else if (name.StartsWith("vp_end_", StringComparison.Ordinal)) vpEnd = Math.Max(vpEnd, _cpu[i]);
            else profiled = true;
        }

        lock (_gate)
        {
            if (vpBegin <= vpEnd)
            {
                _preSum += (vpBegin - start) / 1000.0;
                _viewportsSum += (vpEnd - vpBegin) / 1000.0;
                _postSum += (end - vpEnd) / 1000.0;
                _frames++;
            }
            _sawProfileCaptures |= profiled;
            AddSpan(StartLabel, _cpu[0] - start);
            for (int i = 0; i < count - 1; i++) AddSpan(_names[i], _cpu[i + 1] - _cpu[i]);
            AddSpan(_names[count - 1] + "->end", end - _cpu[count - 1]);
            _spanFrames++;
        }
    }

    private static void AddSpan(string name, double usec)
    {
        _spanSums.TryGetValue(name, out double sum);
        _spanSums[name] = sum + usec / 1000.0;
    }

    /// <summary>Main thread, once per window.</summary>
    public static void Settle()
    {
        if (_rd == null) return;
        Calibrate();
        lock (_gate)
        {
            PreMs = _frames > 0 ? _preSum / _frames : -1;
            ViewportsMs = _frames > 0 ? _viewportsSum / _frames : -1;
            PostMs = _frames > 0 ? _postSum / _frames : -1;
            _preSum = _viewportsSum = _postSum = 0;
            _frames = 0;

            _lastSpans = _spanFrames > 0 && _sawProfileCaptures
                ? _spanSums.Select(kv => new KeyValuePair<string, double>(kv.Key, kv.Value / _spanFrames))
                           .OrderByDescending(kv => kv.Value).ToList()
                : new List<KeyValuePair<string, double>>();
            _spanSums.Clear();
            _spanFrames = 0;
            _sawProfileCaptures = false;
        }
    }

    /// <summary>The <c>[RtSections]</c> line, or null when Godot captured nothing but the viewport
    /// timers (no <c>--gpu-profile</c>): then <see cref="PreMs"/> / <see cref="PostMs"/> already say it
    /// all. Each value is the CPU time from that capture to the next, per frame; viewport ids are shown
    /// by their RenderTimes name.</summary>
    public static string? Describe()
    {
        if (_lastSpans.Count == 0) return null;
        var sb = new StringBuilder("[RtSections] render-thread ms per frame from each capture inside _draw to the next:");
        foreach (var (name, ms) in _lastSpans.Take(24))
        {
            if (ms < 0.05) break;
            sb.Append(' ').Append(Label(name)).Append('=')
              .Append(ms.ToString("F2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>"vp_begin_1234" as "vp_begin:main"; spaces become underscores so the line splits on
    /// spaces.</summary>
    private static string Label(string name)
    {
        foreach (string prefix in new[] { "vp_begin_", "vp_end_" })
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string rest = name[prefix.Length..];
            string suffix = "";
            int arrow = rest.IndexOf("->", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                suffix = rest[arrow..];
                rest = rest[..arrow];
            }
            if (ulong.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)
                && RenderTimes.NameOf(id) is { } viewport)
            {
                return prefix[..^1] + ":" + viewport + suffix;
            }
        }
        return name.Replace(' ', '_');
    }
}
