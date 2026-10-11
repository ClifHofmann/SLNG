namespace SLNG.Core;

/// <summary>
/// BUG-PERF-19: changing what the render buffers are made of (MSAA, SSAO / SSIL / SSR / glow, the shadow atlas)
/// without handing the old buffers back first costs the process memory it never gets back.
///
/// <para>Measured on the RTX 4070 (docs/specs/BUG-PERF-19, 2560 x 1440 main buffers): switching Ultra -> High -> Ultra
/// directly left the process holding 1784 MB for an Ultra that needs 1272 MB (+512 MB), and 1324 MB for a High that needs
/// 812 MB; the extra never came back, because the allocator keeps the blocks the old set sat in and the new set is placed
/// beside them. Going through a state without any of those buffers for a few frames first -- MSAA off, the post effects
/// off -- gave the old set back before the new one was made: 1272 MB for Ultra, 812-1068 MB for High, at every switch.
/// On a 12 GB card shared with a streaming encoder and a slicer (BUG-PERF-19's in-world case) the half gigabyte decides
/// whether Windows has to page.</para>
///
/// <para>This is the bookkeeping for that two-step switch, with no engine in it: <see cref="Request"/> is asked
/// whenever the graphics options are applied and says whether to apply them now, release the buffers first, or
/// wait (a release is already under way); <see cref="OnFrame"/> says when the wait is over and the real options
/// go in.</para>
/// </summary>
public sealed class RenderBufferSwitch
{
    /// <summary>Frames spent without the buffers. The engine frees a render target's textures a few frames after
    /// the frame that stopped using them, and with the render thread separate the main thread runs a frame ahead;
    /// the probe waited 8 and the freed blocks were back.</summary>
    public const int ReleaseFrames = 8;

    public enum Step
    {
        /// <summary>Put the options in the engine now: nothing about the buffers changes (or it is the first apply).</summary>
        ApplyNow,

        /// <summary>Put the buffer-free state in the engine now, and the real options when <see cref="OnFrame"/> says so.</summary>
        ReleaseThenApply,

        /// <summary>A release is already under way: do nothing, the real options are read when its wait ends.</summary>
        Pending,
    }

    private string _applied = "";
    private int _framesLeft;

    /// <summary>True from <see cref="Step.ReleaseThenApply"/> until <see cref="OnFrame"/> reports the end of the wait.</summary>
    public bool Pending => _framesLeft > 0;

    /// <summary>The signature of the buffers the engine was last given ("" before the first apply).</summary>
    public string Applied => _applied;

    /// <summary>A graphics change wants <paramref name="targetSignature"/> (see <see cref="Signature"/>).</summary>
    public Step Request(string targetSignature)
    {
        if (_framesLeft > 0)
        {
            // Another change while the buffers are released: the real options are read when the wait ends, so
            // just let it start over -- a user dragging through three presets pays one release, not three.
            _framesLeft = ReleaseFrames;
            return Step.Pending;
        }

        if (_applied.Length == 0 || targetSignature == _applied)
        {
            _applied = targetSignature;
            return Step.ApplyNow;
        }

        _framesLeft = ReleaseFrames;
        return Step.ReleaseThenApply;
    }

    /// <summary>Once per frame. True on the frame the wait is over: the caller puts the real options in the engine
    /// and reports their signature with <see cref="SetApplied"/>.</summary>
    public bool OnFrame()
    {
        if (_framesLeft <= 0) return false;
        return --_framesLeft == 0;
    }

    /// <summary>The signature of what the caller has just put in the engine.</summary>
    public void SetApplied(string signature) => _applied = signature;

    /// <summary>What decides the render buffers' size and number: the one string two option sets are compared by.</summary>
    public static string Signature(int msaa, bool ssao, bool ssil, bool ssr, bool glow, int shadowAtlasSize)
        => $"msaa {LowVramPolicy.MsaaName(msaa)}, ssao {OnOff(ssao)}, ssil {OnOff(ssil)}, ssr {OnOff(ssr)}, glow {OnOff(glow)}, " +
           $"shadows {(shadowAtlasSize > 0 ? shadowAtlasSize.ToString() : "off")}";

    private static string OnOff(bool on) => on ? "on" : "off";
}
