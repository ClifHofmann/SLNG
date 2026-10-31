using System;

namespace SLNG.Core;

/// <summary>
/// FEAT-PERF-27: the one notion of "the avatar the person is looking at", read by every queue that can serve it first
/// (the main-thread work queue, the rig claim, the avatar cap). Written by the renderer when its
/// <see cref="FocusTracker"/> changes its mind -- a few times a minute, never per frame -- and read from any thread.
/// </summary>
public static class PrioritySubject
{
    // A Guid is not atomic; a reference swap is.
    private sealed record Holder(Guid Id);
    private static volatile Holder _focus = new(Guid.Empty);

    /// <summary>The focus avatar's agent id, or <see cref="Guid.Empty"/>.</summary>
    public static Guid Focus => _focus.Id;

    public static void Set(Guid agentId) => _focus = new Holder(agentId);

    public static bool IsFocus(Guid agentId) => agentId != Guid.Empty && _focus.Id == agentId;

    /// <summary>Priority for a fetch or decode of the focus avatar's texture: above any scenery (its on-screen area),
    /// below the local avatar's own (<c>GpuCache.SelfTexturePriority</c>).</summary>
    public const float FocusTexturePriority = 5.0e8f;
}

/// <summary>
/// FEAT-PERF-27 diagnostics: how long the focus avatar took from the moment it became the focus until it was
/// complete (nothing of it left in the queues) and stayed so for <see cref="StableSeconds"/>. Pure: the caller decides what
/// "complete" means and when to look.
/// </summary>
public sealed class FocusCompletionTimer
{
    public const double StableSeconds = 1.0;

    /// <param name="Subject">The avatar.</param>
    /// <param name="Seconds">Focus start to the first moment of the final, stable completeness; or focus start to
    /// the loss of the focus when it never completed.</param>
    /// <param name="Completed">False when the focus moved on first.</param>
    public readonly record struct Report(Guid Subject, double Seconds, bool Completed);

    private Guid _subject;
    private double _startedAt;
    private double _completeSince = double.NaN;
    private bool _reported;

    /// <summary>The focus is now <paramref name="subject"/>. Returns the report of the previous focus when it never
    /// completed, else null. The same subject again changes nothing.</summary>
    public Report? Track(Guid subject, double nowSeconds)
    {
        if (subject == _subject) return null;
        Report? lost = null;
        if (_subject != Guid.Empty && !_reported)
            lost = new Report(_subject, nowSeconds - _startedAt, Completed: false);
        _subject = subject;
        _startedAt = nowSeconds;
        _completeSince = double.NaN;
        _reported = false;
        return lost;
    }

    /// <summary>Called a few times a second with whether the focus avatar is complete right now. Returns the report
    /// once, when it has been complete for <see cref="StableSeconds"/>.</summary>
    public Report? Update(double nowSeconds, bool complete)
    {
        if (_subject == Guid.Empty || _reported) return null;
        if (!complete)
        {
            _completeSince = double.NaN;
            return null;
        }
        if (double.IsNaN(_completeSince)) _completeSince = nowSeconds;
        if (nowSeconds - _completeSince < StableSeconds) return null;
        _reported = true;
        return new Report(_subject, _completeSince - _startedAt, Completed: true);
    }
}
