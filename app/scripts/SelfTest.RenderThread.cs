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
}
