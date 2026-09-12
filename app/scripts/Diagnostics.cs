using Godot;

namespace SLNG.App;

/// <summary>
/// One switch for the whole performance-diagnostic apparatus built during FEAT-PERF-01: the stats
/// overlay's log lines, the main-thread watchdog, the frame-hitch and ground-clamp traces, and the
/// per-object asset logging.
///
/// It is a switch rather than a deletion on purpose. Those tools are the only reason the stutter
/// investigation converged -- a 12-second freeze was attributed to terrain collision, and a 226
/// ms/second cost to an entity scan, because the numbers were being written down. Removing them for
/// a release would mean rebuilding them the next time something is slow, and the next report will
/// arrive from a build nobody can reproduce locally.
///
/// Off by default, so a release is quiet: no per-frame log lines, no background watchdog thread, no
/// overlay on screen. Enable with <c>--diag</c> on the command line. The overlay itself stays
/// reachable either way via Ctrl+Shift+1, because a user reporting "it stutters" can be asked to
/// look at it without being asked to relaunch from a terminal.
/// </summary>
public static class Diagnostics
{
    private const string Flag = "--diag";

    /// <summary>True when the client was started with <c>--diag</c>. Read in hot paths, so it is a
    /// plain static bool rather than anything that re-reads the command line.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>True when the client was started with <c>--no-reattach</c>: the login
    /// Current-Outfit attachment reconcile is not armed at all. See <see cref="Initialize"/>.</summary>
    public static bool NoReattach { get; private set; }

    /// <summary>True when the client was started with <c>--no-ground-drop</c>. See
    /// <see cref="Initialize"/>.</summary>
    public static bool NoGroundDrop { get; private set; }

    /// <summary>Call once at startup, before anything that logs. Godot puts arguments after a bare
    /// <c>--</c> into GetCmdlineUserArgs and the rest into GetCmdlineArgs; both are checked so the
    /// flag works whether or not it is passed after the separator.</summary>
    public static void Initialize()
    {
        Enabled = HasFlag(OS.GetCmdlineArgs()) || HasFlag(OS.GetCmdlineUserArgs());

        // --no-reattach: suppress the login attachment reconcile (GridSession's
        // ReattachMissingCofAttachments). That pass is the ONE thing this client does to a live
        // avatar's outfit on its own, and it decides from LibreMetaverse's object cache 6 s after
        // login — on a busy Agni sim with a heavy mesh avatar that can be before every attachment
        // has been identified, and the re-attach then restarts the object's scripts (AO, ankle
        // lock, body) for everyone, not just here. BUG-AVATAR-07's A/B switch: relog with this and
        // a second viewer says whether the difference it sees is caused by us.
        NoReattach = HasFlag(OS.GetCmdlineArgs(), "--no-reattach")
                  || HasFlag(OS.GetCmdlineUserArgs(), "--no-reattach");

        // --no-ground-drop: stop AvatarController's ground clamp from pulling the local agent DOWN
        // to its own ground reading. Pushing UP out of geometry still happens. The reference viewer
        // has no equivalent of the downward pull at all — the simulator owns the agent's Z, and
        // LLWorld::resolveStepHeightGlobal's foot-plane maths feeds foot IK and shadows, never the
        // avatar's position. Measured live (BUG-AVATAR-07): the sim placed the agent at Z 1037.41,
        // the clamp dragged it to groundHeight 1035.84 + halfBody 0.885 = 1036.725, and the whole
        // avatar rendered 0.69 m low as a result.
        NoGroundDrop = HasFlag(OS.GetCmdlineArgs(), "--no-ground-drop")
                    || HasFlag(OS.GetCmdlineUserArgs(), "--no-ground-drop");

        // BUG-RENDER-16: read once here, alongside --diag, since this is already the "parse the
        // command line at startup" site. See RenderConfig.HighFrequencyFoliageAlpha for what each
        // mode does. With no arg the RenderConfig default (Hash + TAA) stands -- the switch's
        // fall-through keeps the current value, it does NOT reset to Scissor.
        //   --foliage-alpha=scissor|blend|hash|prepass|edge|blenddepth|blendcore  (--foliage-blend = =blend)
        string? foliageArg = FindValueArg("--foliage-alpha=", OS.GetCmdlineArgs())
                          ?? FindValueArg("--foliage-alpha=", OS.GetCmdlineUserArgs());
        RenderConfig.HighFrequencyFoliageAlpha = foliageArg switch
        {
            "blend" => RenderConfig.FoliageAlpha.Blend,
            "hash" => RenderConfig.FoliageAlpha.Hash,
            "prepass" => RenderConfig.FoliageAlpha.Prepass,
            "edge" => RenderConfig.FoliageAlpha.Edge,
            "blenddepth" => RenderConfig.FoliageAlpha.BlendDepth,
            "blendcore" => RenderConfig.FoliageAlpha.BlendCore,
            "scissor" => RenderConfig.FoliageAlpha.Scissor,
            _ when HasFlag(OS.GetCmdlineArgs(), "--foliage-blend")
                || HasFlag(OS.GetCmdlineUserArgs(), "--foliage-blend") => RenderConfig.FoliageAlpha.Blend,
            _ => RenderConfig.HighFrequencyFoliageAlpha,
        };
        string? hashScaleArg = FindValueArg("--foliage-hash-scale=", OS.GetCmdlineArgs())
                            ?? FindValueArg("--foliage-hash-scale=", OS.GetCmdlineUserArgs());
        if (hashScaleArg != null && float.TryParse(hashScaleArg, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float hs) && hs > 0f)
            RenderConfig.FoliageHashScale = hs;

        // BUG-RENDER-16: --foliage-core-alpha=N, the depth-pass threshold for --foliage-alpha=blendcore.
        string? coreAlphaArg = FindValueArg("--foliage-core-alpha=", OS.GetCmdlineArgs())
                            ?? FindValueArg("--foliage-core-alpha=", OS.GetCmdlineUserArgs());
        if (coreAlphaArg != null && float.TryParse(coreAlphaArg, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float ca) && ca >= 0f && ca <= 1f)
            RenderConfig.FoliageCoreAlpha = ca;

        // BUG-RENDER-16: --alpha-sort-hysteresis [=N]. Bare flag takes the reference viewer's own
        // 0.64 (llspatialpartition.cpp:667); =N overrides it. See RenderConfig.AlphaSortHysteresis
        // for what the number means -- it is a chord on the unit sphere (~37 deg), not radians.
        string? hystArg = FindValueArg("--alpha-sort-hysteresis=", OS.GetCmdlineArgs())
                       ?? FindValueArg("--alpha-sort-hysteresis=", OS.GetCmdlineUserArgs());
        if (hystArg != null && float.TryParse(hystArg, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float hy) && hy >= 0f)
            RenderConfig.AlphaSortHysteresis = hy;
        else if (HasFlag(OS.GetCmdlineArgs(), "--alpha-sort-hysteresis")
                 || HasFlag(OS.GetCmdlineUserArgs(), "--alpha-sort-hysteresis"))
            RenderConfig.AlphaSortHysteresis = RenderConfig.ViewerAlphaSortHysteresis;

        // BUG-RENDER-16: --alpha-sort-planar. Sorts transparent objects by view-axis depth like
        // the reference viewer (llspatialpartition.cpp:684-692) instead of Godot's radial distance.
        if (HasFlag(OS.GetCmdlineArgs(), "--alpha-sort-planar")
            || HasFlag(OS.GetCmdlineUserArgs(), "--alpha-sort-planar"))
            RenderConfig.AlphaSortPlanarDepth = true;

        // BUG-RENDER-16: --alpha-split=off. Keeps every sorted surface of a multi-surface object on
        // its parent instance (the pre-v0.22.26 behaviour) so the per-surface split can be A/B'd.
        string? splitArg = FindValueArg("--alpha-split=", OS.GetCmdlineArgs())
                        ?? FindValueArg("--alpha-split=", OS.GetCmdlineUserArgs());
        if (splitArg == "off") RenderConfig.SplitSortedSurfaces = false;

        // BUG-RENDER-16: --alpha-sort-freeze. Falsification test -- see RenderConfig.
        if (HasFlag(OS.GetCmdlineArgs(), "--alpha-sort-freeze")
            || HasFlag(OS.GetCmdlineUserArgs(), "--alpha-sort-freeze"))
            RenderConfig.AlphaSortFreezeDebug = true;

        // FEAT-PERF-06: --no-instancing keeps every prim on its own MeshInstance3D (the
        // pre-v0.22.29 behaviour) so the MultiMesh batching can be A/B'd in-world.
        if (HasFlag(OS.GetCmdlineArgs(), "--no-instancing")
            || HasFlag(OS.GetCmdlineUserArgs(), "--no-instancing"))
            RenderConfig.EnableInstancing = false;

        GD.Print($"[Diagnostics] high-frequency foliage alpha = {RenderConfig.HighFrequencyFoliageAlpha}" +
                 $" (hashScale={RenderConfig.FoliageHashScale}" +
                 $" coreAlpha={RenderConfig.FoliageCoreAlpha.ToString(System.Globalization.CultureInfo.InvariantCulture)})" +
                 $" alphaSortHysteresis={(RenderConfig.AlphaSortHysteresis > 0f ? RenderConfig.AlphaSortHysteresis.ToString(System.Globalization.CultureInfo.InvariantCulture) : "off")}" +
                 $" alphaSortPlanar={RenderConfig.AlphaSortPlanarDepth}" +
                 $" alphaSortFreeze={RenderConfig.AlphaSortFreezeDebug}" +
                 $" alphaSplit={RenderConfig.SplitSortedSurfaces}" +
                 $" (BUG-RENDER-16)");

        GD.Print($"[Diagnostics] instancing = {(RenderConfig.EnableInstancing ? "on" : "off (--no-instancing)")} (FEAT-PERF-06)");

        // Info is where the per-object asset logging lives -- texture fetches, sharpen decisions,
        // mesh sites. One session of it ran to 3.4 GB before it was throttled, and even throttled it
        // is thousands of lines nobody reads unless they are chasing something.
        // Debug, not Info: AvatarRenderer's 11 Logger.Debug sites -- mesh fetch, joints
        // resolved, dominant bone, bind-pose extent, orphaned verts -- were unreachable in EVERY
        // build configuration, because nothing anywhere ever set CurrentLevel below Info. The
        // avatar rigging diagnostics existed and could not be turned on. They are per-mesh
        // one-shots, not per-frame, so the volume they add to --diag is bounded by worn meshes.
        Logger.CurrentLevel = Enabled ? LogLevel.Debug : LogLevel.Warning;

        if (Enabled) GD.Print("[Diagnostics] enabled via --diag: perf logging, watchdog and overlay on");
    }

    private static bool HasFlag(string[] args) => HasFlag(args, Flag);

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (string arg in args)
        {
            if (arg == flag) return true;
        }
        return false;
    }

    /// <summary>Returns the part after <paramref name="prefix"/> for the first
    /// <c>--name=value</c> argument that matches, lower-cased; null if none. Used for the
    /// BUG-RENDER-16 foliage-alpha selector.</summary>
    private static string? FindValueArg(string prefix, string[] args)
    {
        foreach (string arg in args)
        {
            if (arg.StartsWith(prefix, System.StringComparison.Ordinal))
                return arg[prefix.Length..].ToLowerInvariant();
        }
        return null;
    }
}
