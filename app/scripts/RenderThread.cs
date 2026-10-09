using System;
using System.Threading;
using Godot;

namespace SLNG.App;

/// <summary>
/// FEAT-PERF-13: whether Godot renders on its own thread, and how the person picks it.
///
/// <para>Godot 4.7 has two modes (<c>rendering/driver/threads/thread_model</c>, source:
/// <c>main/main.cpp</c> 4.7-stable): <b>Safe</b> (1, the default) runs the RenderingServer on the main
/// thread, <b>Separate</b> (2) pumps it from a high-priority WorkerThreadPool task. The old
/// "Unsafe" mode is gone. With Separate the main thread only queues commands and the frame becomes
/// roughly max(main, render) instead of main + render; <c>Main::iteration</c> still calls
/// <c>RenderingServer::sync()</c> right before it queues the next draw, so the main thread never
/// runs more than one frame ahead.</para>
///
/// <para>The engine marks the mode experimental ("known bugs which can lead to crashing, especially
/// when using particles or resizing the window"), which is why it ships as an A/B choice and not as
/// a default.</para>
///
/// <para><b>Choosing it.</b> The engine reads the mode before any managed code runs, so a setting
/// the client keeps in <c>preferences.cfg</c> is too late. Two ways in, neither of which means
/// editing <c>project.godot</c> by hand:</para>
/// <list type="bullet">
/// <item>The engine's own flag, <c>--render-thread separate</c> (or <c>safe</c>), for one run. It wins
/// over everything else.</item>
/// <item>The Graphics page writes <see cref="OverridePath"/>, a <c>ConfigFile</c> in the engine's
/// project-settings format that <c>project.godot</c> names in
/// <c>application/config/project_settings_override</c>. The engine merges it over the project
/// settings at startup, so the choice survives restarts and needs one.</item>
/// </list>
/// </summary>
public static class RenderThread
{
    /// <summary>Engine-level overrides the client writes. Deleted, not rewritten, when the person goes
    /// back to the default, so a fresh install has no file at all.</summary>
    public const string OverridePath = "user://engine_overrides.cfg";

    private const string OverrideSection = "rendering";
    private const string OverrideKey = "driver/threads/thread_model";
    private const string SettingPath = "rendering/driver/threads/thread_model";
    private const string Flag = "--render-thread";
    private const long ModeSeparate = 2;

    /// <summary>True when this process renders on a separate thread. Fixed for the process's life.
    /// Found by asking the engine, not by reading the inputs the engine read: the
    /// <c>--render-thread</c> flag is consumed by the engine and does not appear in
    /// <see cref="OS.GetCmdlineArgs"/>, and the project setting does not see it either. See
    /// <see cref="Probe"/>.</summary>
    public static bool IsSeparate { get; } = Probe();

    /// <summary>"separate" or "main", the word the <c>[Perf]</c> line and the boot line use.</summary>
    public static string ModeName => IsSeparate ? "separate" : "main";

    /// <summary>What the next start will use according to the saved choice, ignoring a command-line
    /// flag. The Graphics page shows this, and says "restart" when it differs from
    /// <see cref="IsSeparate"/>.</summary>
    public static bool SavedPreferenceIsSeparate(string path = OverridePath)
    {
        var cfg = new ConfigFile();
        if (cfg.Load(path) != Error.Ok) return false;
        return cfg.GetValue(OverrideSection, OverrideKey, 1).AsInt64() == ModeSeparate;
    }

    /// <summary>Writes (or removes) the override file the next start reads.</summary>
    public static void SavePreference(bool separate, string path = OverridePath)
    {
        var cfg = new ConfigFile();
        cfg.Load(path); // keep any other engine override a later task adds
        if (separate)
        {
            cfg.SetValue(OverrideSection, OverrideKey, (int)ModeSeparate);
            cfg.Save(path);
            return;
        }

        if (cfg.HasSectionKey(OverrideSection, OverrideKey)) cfg.EraseSectionKey(OverrideSection, OverrideKey);
        if (cfg.GetSections().Length == 0)
        {
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
            return;
        }
        cfg.Save(path);
    }

    /// <summary>One line for the boot log: the mode and where it came from.</summary>
    public static string Describe()
    {
        bool setting = SettingIsSeparate();
        string source = setting != IsSeparate ? $"command line ({Flag})"
            : SavedPreferenceIsSeparate() && setting ? $"saved choice ({OverridePath})"
            : "project default";
        return $"{ModeName} ({source})";
    }

    /// <summary>Runs one callback through <c>RenderingServer.CallOnRenderThread</c> and compares the
    /// thread it ran on with this one. With rendering on the main thread the engine runs it inline;
    /// otherwise it runs on the render thread, which is a different thread. At boot the render
    /// thread is idle, so the wait is far under a millisecond; if the callback has not run after two
    /// seconds the project setting is the best remaining guess.</summary>
    /// <remarks>This is the only managed code the client ever runs on the render thread, and it is
    /// deliberately trivial: a field write and an event, no Godot API. The event is never disposed
    /// so a callback that arrives after the timeout cannot touch a disposed object.</remarks>
    private static bool Probe()
    {
        int mainThread = System.Environment.CurrentManagedThreadId;
        int ranOn = -1;
        var ran = new ManualResetEventSlim();
        try
        {
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                try
                {
                    ranOn = System.Environment.CurrentManagedThreadId;
                    ran.Set();
                }
                catch (Exception)
                {
                    // Nothing to report from here; the caller times out and falls back.
                }
            }));
        }
        catch (Exception)
        {
            return SettingIsSeparate();
        }

        return ran.Wait(2000) ? Volatile.Read(ref ranOn) != mainThread : SettingIsSeparate();
    }

    private static bool SettingIsSeparate() =>
        ProjectSettings.GetSetting(SettingPath, 1).AsInt64() == ModeSeparate;
}
