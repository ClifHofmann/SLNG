using System;
using System.Collections.Generic;
using System.Threading;
using Godot;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>
    /// FEAT-PERF-13: the render-thread plumbing, in whichever mode this run was started in
    /// (<c>--render-thread separate</c> exercises the second half; the default run still checks the
    /// first).
    ///
    /// <list type="number">
    /// <item>The mode the client reports is the mode the engine runs: a callable queued with
    /// <c>RenderingServer.CallOnRenderThread</c> executes inline on the main thread when rendering is on
    /// it, and on another thread when it is not.</item>
    /// <item>The saved choice round-trips through the override file format the engine reads
    /// (<c>RenderThread.SavePreference</c>), written to a temp path so the run leaves
    /// <c>user://</c> alone.</item>
    /// <item>Separate mode only: a <c>RenderingServer</c> getter (a round trip into the render thread)
    /// answers from the main thread and from <see cref="RenderTimeSampler"/>'s thread, and the
    /// sampler drops work instead of queueing without limit.</item>
    /// <item>A material fingerprint is stable per material and still tells two different values
    /// apart now that the uniform names come from a per-shader cache.</item>
    /// </list>
    /// </summary>
    private static Check CheckRenderThread()
    {
        const string Name = "render thread";
        var problems = new List<string>();
        string tempCfg = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"slng_selftest_override_{System.Environment.ProcessId}.cfg");
        try
        {
            // 1. The reported mode is the real one.
            int mainThread = System.Environment.CurrentManagedThreadId;
            int ranOn = -1;
            using var ran = new ManualResetEventSlim();
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                ranOn = System.Environment.CurrentManagedThreadId;
                ran.Set();
            }));
            if (!ran.Wait(5000))
                problems.Add("a callable queued with CallOnRenderThread never ran");
            else if ((ranOn != mainThread) != RenderThread.IsSeparate)
                problems.Add($"the client reports '{RenderThread.ModeName}' but the render callback ran on " +
                             (ranOn == mainThread ? "the main thread" : "another thread"));

            // 2. The saved choice survives the file format.
            RenderThread.SavePreference(true, tempCfg);
            if (!RenderThread.SavedPreferenceIsSeparate(tempCfg)) problems.Add("separate was saved but reads back as main");
            if (!System.IO.File.Exists(tempCfg)) problems.Add("no override file was written for 'separate'");
            RenderThread.SavePreference(false, tempCfg);
            if (RenderThread.SavedPreferenceIsSeparate(tempCfg)) problems.Add("main was saved but reads back as separate");
            if (System.IO.File.Exists(tempCfg)) problems.Add("the override file was left behind after going back to the default");

            // 3. Round trips, separate mode only. In the default mode a getter on another thread waits
            //    for the main thread to flush the queue, which this blocked check would never allow.
            if (RenderThread.IsSeparate)
            {
                _ = RenderingServer.GetVideoAdapterName(); // main thread: queued and awaited, must return

                var rid = Engine.GetMainLoop() is SceneTree t ? t.Root.GetViewportRid() : default;
                double measured = -1;
                using var sampler = new RenderTimeSampler();
                sampler.Post(() => measured = RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid));
                if (!sampler.WaitIdle(5000)) problems.Add("the sampler thread's getter did not come back");
                else if (measured < 0) problems.Add("the sampler thread's getter did not run");

                // More than MaxPending requests against a blocked sampler: the extras are dropped.
                using var gate = new ManualResetEventSlim();
                int accepted = 0;
                using (var jammed = new RenderTimeSampler())
                {
                    for (int i = 0; i < RenderTimeSampler.MaxPending * 3; i++)
                        if (jammed.Post(() => gate.Wait(2000))) accepted++;
                    gate.Set();
                    jammed.WaitIdle(5000);
                }
                if (accepted != RenderTimeSampler.MaxPending)
                    problems.Add($"the sampler accepted {accepted} requests while blocked (limit {RenderTimeSampler.MaxPending})");
            }

            // 4. The fingerprint cache.
            var shader = PrimShaderFamily.Select(PrimShaderFamily.Kind.Opaque, PrimShaderFamily.Surface.WorldPrim);
            var a = new ShaderMaterial { Shader = shader };
            var b = new ShaderMaterial { Shader = shader };
            var c = new ShaderMaterial { Shader = shader };
            c.SetShaderParameter(PrimShaderFamily.AlbedoColor, new Color(0.25f, 0.5f, 0.75f));
            string fa = MaterialFingerprint.Of(a);
            if (fa != MaterialFingerprint.Of(b)) problems.Add("two default materials of one shader fingerprint differently");
            if (fa == MaterialFingerprint.Of(c)) problems.Add("a material with another albedo colour has the same fingerprint");
            if (!fa.Contains("albedo_color")) problems.Add("the fingerprint does not list the shader's uniforms");
        }
        catch (Exception ex)
        {
            problems.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { if (System.IO.File.Exists(tempCfg)) System.IO.File.Delete(tempCfg); } catch { /* temp file */ }
        }

        return problems.Count == 0
            ? new Check(Name, true, $"rendering on the {RenderThread.ModeName} thread, callbacks, saved choice and fingerprints behave")
            : new Check(Name, false, string.Join("; ", problems));
    }

    /// <summary>FEAT-PERF-24 P0-a: FrameTimeline reads its render-thread markers at pre_draw on the
    /// grounds that the sync before it waits for everything queued earlier. The same holds for any
    /// synchronous RenderingServer call, so: queue two markers, make one sync call, and both must
    /// have run, in queue order, between being queued and the call returning. In the default mode
    /// the callables run inline, which the same ordering covers.</summary>
    private static Check CheckRenderMarkersBeforeSync()
    {
        const string Name = "render-thread markers run before a sync returns";
        long first = 0, second = 0;
        long queued = System.Diagnostics.Stopwatch.GetTimestamp();
        RenderingServer.CallOnRenderThread(Callable.From(() => Volatile.Write(ref first, System.Diagnostics.Stopwatch.GetTimestamp())));
        RenderingServer.CallOnRenderThread(Callable.From(() => Volatile.Write(ref second, System.Diagnostics.Stopwatch.GetTimestamp())));
        _ = RenderingServer.GetVideoAdapterName(); // a round trip in separate mode
        long returned = System.Diagnostics.Stopwatch.GetTimestamp();
        long a = Volatile.Read(ref first), b = Volatile.Read(ref second);

        if (a == 0 || b == 0) return new Check(Name, false, "a marker had not run when the sync call returned");
        if (!(queued <= a && a <= b && b <= returned))
            return new Check(Name, false, $"out of order: queued {queued}, markers {a} / {b}, returned {returned}");
        return new Check(Name, true, $"both markers ran in order before the sync returned ({RenderThread.ModeName} thread)");
    }

    /// <summary>
    /// BUG-PERF-22: verify that a 10s stall with the separate render thread saved switches
    /// engine_overrides.cfg back to the main thread and writes the fallback marker once,
    /// that other keys survive, and that consuming the marker cleans it up.
    /// </summary>
    private static Check CheckRenderThreadFallback()
    {
        const string Name = "render-thread fallback";
        var problems = new List<string>();
        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"slng_selftest_fallback_{System.Environment.ProcessId}_{System.Guid.NewGuid():N}");
        string tempCfg = System.IO.Path.Combine(tempDir, "engine_overrides.cfg");
        string tempMarker = System.IO.Path.Combine(tempDir, "render_thread_fallback.json");

        try
        {
            System.IO.Directory.CreateDirectory(tempDir);

            // 1. Separate saved + 10s stall: rewrites override (deletes if only key) and writes marker
            System.IO.File.WriteAllText(tempCfg, "[rendering]\ndriver/threads/thread_model=2\n");
            var watchdog1 = new MainThreadWatchdog();
            watchdog1.ConfigureFallback(
                isSeparate: true,
                savedPreferenceIsSeparate: true,
                overrideFilePath: tempCfg,
                markerFilePath: tempMarker,
                isHeadlessOrSelfTest: false);

            bool trig1 = watchdog1.EvaluateStall(MainThreadWatchdog.FallbackStallSeconds);
            if (!trig1) problems.Add("watchdog did not trigger fallback on 10s stall with separate saved");
            if (System.IO.File.Exists(tempCfg)) problems.Add("override file with only thread_model was not deleted");
            if (!System.IO.File.Exists(tempMarker)) problems.Add("fallback marker was not written");

            // 2. Second trigger on the same watchdog writes nothing
            bool trig2 = watchdog1.EvaluateStall(15.0);
            if (trig2) problems.Add("second trigger wrote fallback again instead of once per run");

            // 3. Main thread recovery logged/handled cleanly
            bool recovered = watchdog1.EvaluateStall(0.5);
            if (recovered) problems.Add("heartbeat recovery should return false");

            // 4. Consuming the marker reads data and deletes the file
            if (!SLNG.Core.RenderThreadFallback.TryConsumeMarker(tempMarker, out var marker) || marker == null)
            {
                problems.Add("failed to consume fallback marker");
            }
            else
            {
                if (marker.StalledSeconds < 9.9) problems.Add($"unexpected stall seconds: {marker.StalledSeconds}");
                if (System.IO.File.Exists(tempMarker)) problems.Add("fallback marker was not deleted upon consumption");
            }

            // 5. Other keys survive in engine_overrides.cfg
            System.IO.File.WriteAllText(tempCfg, "[rendering]\ndriver/threads/thread_model=2\n\n[display]\nwindow/size/width=1920\n");
            var watchdog2 = new MainThreadWatchdog();
            watchdog2.ConfigureFallback(
                isSeparate: true,
                savedPreferenceIsSeparate: true,
                overrideFilePath: tempCfg,
                markerFilePath: tempMarker,
                isHeadlessOrSelfTest: false);

            bool trig3 = watchdog2.EvaluateStall(10.0);
            if (!trig3) problems.Add("watchdog did not trigger on stall when other keys were present");
            if (!System.IO.File.Exists(tempCfg))
            {
                problems.Add("override file with other keys was deleted instead of rewritten");
            }
            else
            {
                string preserved = System.IO.File.ReadAllText(tempCfg);
                if (preserved.Contains("driver/threads/thread_model")) problems.Add("thread_model was not removed from override file");
                if (!preserved.Contains("[display]") || !preserved.Contains("window/size/width=1920"))
                    problems.Add("other keys in override file were not preserved");
            }
            // Clean up marker written by watchdog2
            if (System.IO.File.Exists(tempMarker)) System.IO.File.Delete(tempMarker);

            // 6. Command-line only (savedPreferenceIsSeparate: false) writes nothing
            System.IO.File.WriteAllText(tempCfg, "[display]\nwindow/size/width=1920\n");
            var watchdog3 = new MainThreadWatchdog();
            watchdog3.ConfigureFallback(
                isSeparate: true,
                savedPreferenceIsSeparate: false,
                overrideFilePath: tempCfg,
                markerFilePath: tempMarker,
                isHeadlessOrSelfTest: false);

            bool trig4 = watchdog3.EvaluateStall(10.0);
            if (trig4) problems.Add("watchdog triggered write for command-line only separate thread");
            if (System.IO.File.Exists(tempMarker)) problems.Add("marker was written when separate thread was not saved");
        }
        catch (Exception ex)
        {
            problems.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { if (System.IO.Directory.Exists(tempDir)) System.IO.Directory.Delete(tempDir, true); } catch { }
        }

        return problems.Count == 0
            ? new Check(Name, true, "10s stall switches saved choice, preserves other keys, writes and consumes marker")
            : new Check(Name, false, string.Join("; ", problems));
    }
}
