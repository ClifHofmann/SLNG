using System;

namespace SLNG.Core;

/// <summary>What one animation says about how it plays: its length, its loop and its fades. Plain
/// numbers, so the lifecycle below needs neither the asset type nor an engine.</summary>
/// <param name="Duration">Length of the clip in seconds.</param>
/// <param name="Loop">Whether the clip repeats between <paramref name="LoopIn"/> and <paramref name="LoopOut"/>.</param>
/// <param name="LoopIn">Where the repeat jumps back to.</param>
/// <param name="LoopOut">Where the repeat ends (the clip's end for one that does not loop).</param>
/// <param name="EaseIn">Seconds the motion takes to fade in.</param>
/// <param name="EaseOut">Seconds the motion takes to fade out once stopped.</param>
public readonly record struct MotionTiming(float Duration, bool Loop, float LoopIn, float LoopOut, float EaseIn, float EaseOut);

/// <summary>
/// BUG-ANIM-08: one playing motion's clock and fade weight -- the half of the reference viewer's
/// <c>LLMotionController</c> that decides how much a motion counts, not what it says.
/// </summary>
/// <remarks>
/// <para>A motion that starts rises from weight 0 to 1 over its ease-in duration, one that is stopped
/// falls from where it stood to 0 over its ease-out, both along <see cref="CubicStep"/>
/// (<c>LLMotionController::updateMotionsByType</c>, llmotioncontroller.cpp:645-734). Without this a
/// new loop took the whole body the instant it began and the old one vanished the instant it ended --
/// the jolt between two loops.</para>
///
/// <para>Time starts at 0, not at the loop's in point: the viewer hands the motion the time since its
/// activation (llkeyframemotion.cpp:676, 710), so the intro before the in point plays once. Past the
/// out point a looping motion wraps to <c>in + fmod(t - out, out - in)</c> (:701-712). A stopped loop
/// whose out point is before the clip's end runs on into its outro, and begins to fade only
/// <c>ease out</c> before the end (<c>LLKeyframeMotion::setStopTime</c>, :801-821).</para>
///
/// <para>A non-looping motion is clamped at its out point and holds its last pose; the viewer instead
/// stops it by itself there and fades it out. That is not ported -- <c>HasFinished</c> and the control
/// avatars lean on the hold.</para>
///
/// <para>Engine-neutral, one instance per playing animation, advanced on the main thread.</para>
/// </remarks>
public sealed class MotionState
{
    private float _residual;
    private bool _easeOutBegun;
    private float _stopAt = float.PositiveInfinity;

    public MotionState(MotionTiming timing)
    {
        Timing = timing;
        Reset(0f);
    }

    /// <summary>The clip's timing. Replaceable: playing the same id again with fresh data.</summary>
    public MotionTiming Timing { get; set; }

    /// <summary>Seconds since the motion was (re)started.</summary>
    public float Elapsed { get; private set; }

    /// <summary>The time inside the clip the keys are read at.</summary>
    public float PoseTime { get; private set; }

    /// <summary>How much this motion counts right now, 0..1.</summary>
    public float Weight { get; private set; }

    /// <summary>True from <see cref="Stop"/> until the motion is started again.</summary>
    public bool Stopped { get; private set; }

    /// <summary>True once a stopped motion has faded all the way out and may be dropped. One with no
    /// ease-out is finished the moment it is stopped (the viewer drops it on its next update).</summary>
    public bool IsFinished => Stopped && Elapsed >= _stopAt + SafeEase(Timing.EaseOut);

    /// <summary>The viewer's smoothstep (<c>cubic_step</c>, llmath.h:381).</summary>
    public static float CubicStep(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    /// <summary>Starts the clock again from 0. The weight it already has is kept as the floor of the
    /// new fade-in, so a motion brought back while it was fading out rises from there instead of
    /// dipping to 0 or jumping to 1.</summary>
    public void Activate() => Reset(Weight);

    /// <summary>Puts the clock at <paramref name="time"/> inside the clip without touching the weight --
    /// frame-stepping a frozen pose.</summary>
    public void Seek(float time)
    {
        Elapsed = time;
        PoseTime = time;
    }

    /// <summary>Begins fading the motion out. A looping clip with an outro keeps playing to its end
    /// first.</summary>
    public void Stop()
    {
        if (Stopped) return;

        Stopped = true;
        _stopAt = Elapsed;

        var t = Timing;
        if (t.Loop && t.LoopOut != t.Duration)
        {
            float loopFraction = t.LoopOut == t.LoopIn ? 0f : (Elapsed - t.LoopIn) % (t.LoopOut - t.LoopIn);
            _stopAt = Math.Max(Elapsed, Elapsed - loopFraction + (t.Duration - t.LoopIn) - SafeEase(t.EaseOut));
        }
    }

    /// <summary>Moves the clock on by <paramref name="dt"/> seconds and works out the weight and the
    /// pose time it ends on.</summary>
    public void Advance(float dt)
    {
        if (dt <= 0f) return;

        float previousPose = PoseTime;
        Elapsed += dt;

        // The weight the fade-out starts from is the one the motion had when it began, i.e. the last
        // frame's (llmotioncontroller.cpp:670-674).
        if (Stopped && !_easeOutBegun && Elapsed > _stopAt)
        {
            _easeOutBegun = true;
            _residual = Weight;
        }

        Weight = ComputeWeight();
        PoseTime = ComputePoseTime(previousPose, dt);
    }

    private void Reset(float residual)
    {
        Elapsed = 0f;
        Stopped = false;
        _stopAt = float.PositiveInfinity;
        _easeOutBegun = false;
        _residual = residual;
        Weight = ComputeWeight();
        PoseTime = ComputePoseTime(0f, 0f);
    }

    private float ComputeWeight()
    {
        float easeIn = SafeEase(Timing.EaseIn);
        float easeOut = SafeEase(Timing.EaseOut);

        if (Stopped && Elapsed > _stopAt)
        {
            if (easeOut <= 0f || Elapsed > _stopAt + easeOut) return 0f;
            return _residual * CubicStep(1f - (Elapsed - _stopAt) / easeOut);
        }

        if (easeIn <= 0f || Elapsed > easeIn) return 1f;
        return _residual + (1f - _residual) * CubicStep(Elapsed / easeIn);
    }

    private float ComputePoseTime(float previous, float dt)
    {
        var t = Timing;
        bool loops = t.Loop && t.LoopOut > t.LoopIn;

        if (!loops) return Math.Min(Elapsed, t.LoopOut);

        if (Stopped) return Math.Min(t.Duration, previous + dt);
        if (Elapsed <= t.LoopOut) return Elapsed;
        return t.LoopIn + (Elapsed - t.LoopOut) % (t.LoopOut - t.LoopIn);
    }

    private static float SafeEase(float ease) => float.IsFinite(ease) && ease > 0f ? ease : 0f;
}
