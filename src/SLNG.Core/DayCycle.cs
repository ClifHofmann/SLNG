using System.Numerics;

namespace SLNG.Core;

/// <summary>One keyframe on a day-cycle track: a position in 0..1 along the cycle, and the
/// settings that apply exactly there.</summary>
public readonly record struct DayCycleFrame<T>(float Position, T Settings);

/// <summary>A region's day cycle — the sky and water keyframes plus the cycle's length and offset
/// (FEAT-ENV-01).
///
/// Evaluating "what does the sky look like right now" is a pure function of time, which is why
/// this lives in <c>SLNG.Core</c> and is testable without a grid, a renderer or a clock.
///
/// The two tracks are separate because SL's day cycle stores them that way: track 0 is water,
/// tracks 1-4 are sky altitude bands (llenvironment.h's <c>ETrackType</c>). Only the ground-level
/// sky track (1) is modelled here; altitude bands need the camera's height and belong with the
/// parcel work that is deliberately deferred.</summary>
/// <param name="SkyFrames">Ground-level sky keyframes, ascending by position. Empty means "no
/// cycle" — <see cref="EvaluateSky"/> then returns the viewer default.</param>
/// <param name="WaterFrames">Water keyframes, ascending by position.</param>
/// <param name="DayLengthSeconds">Length of one full cycle. SL's default is 14400 (4 hours).</param>
/// <param name="DayOffsetSeconds">Offset added to wall-clock time before taking the cycle
/// position, which is how a region shifts its noon away from the grid's.</param>
public sealed record DayCycle(
    IReadOnlyList<DayCycleFrame<SkySettings>> SkyFrames,
    IReadOnlyList<DayCycleFrame<WaterSettings>> WaterFrames,
    int DayLengthSeconds = 14400,
    int DayOffsetSeconds = 57600)
{
    /// <summary>A region with no day cycle at all: the viewer's default sky and water, fixed.</summary>
    public static DayCycle Default { get; } = new(
        new[] { new DayCycleFrame<SkySettings>(0f, SkySettings.Default) },
        new[] { new DayCycleFrame<WaterSettings>(0f, WaterSettings.Default) });

    /// <summary>Cycle position in 0..1 for a given wall-clock time.
    ///
    /// Matches <c>LLEnvironment::DayInstance::animate</c>, which takes
    /// <c>LLDate::now().secondsSinceEpoch() + mDayOffset</c> and loops it over the day length.
    /// A non-positive day length means a fixed sky, which is a legitimate region setting and not
    /// an error — it maps to position 0.</summary>
    public float PositionAt(DateTimeOffset utcNow)
    {
        if (DayLengthSeconds <= 0) return 0f;

        double seconds = utcNow.ToUnixTimeMilliseconds() / 1000.0 + DayOffsetSeconds;
        double position = seconds % DayLengthSeconds / DayLengthSeconds;
        // C#'s % keeps the sign of the dividend, and a large enough negative offset would push
        // this below zero; the viewer's own offset handling normalises the same way
        // (llenvironment.cpp:1822-1823).
        if (position < 0) position += 1.0;
        return (float)position;
    }

    /// <summary>The sky at a given wall-clock time.</summary>
    public SkySettings EvaluateSky(DateTimeOffset utcNow) => EvaluateSkyAt(PositionAt(utcNow));

    /// <summary>The water at a given wall-clock time.</summary>
    public WaterSettings EvaluateWater(DateTimeOffset utcNow) => EvaluateWaterAt(PositionAt(utcNow));

    /// <summary>The sky at an explicit cycle position in 0..1. Separate from
    /// <see cref="EvaluateSky"/> so tests can pin a position without faking a clock.</summary>
    public SkySettings EvaluateSkyAt(float position)
        => Evaluate(SkyFrames, position, SkySettings.Default, SkySettings.Lerp);

    /// <summary>The water at an explicit cycle position in 0..1.</summary>
    public WaterSettings EvaluateWaterAt(float position)
        => Evaluate(WaterFrames, position, WaterSettings.Default, WaterSettings.Lerp);

    /// <summary>Finds the two keyframes bracketing <paramref name="position"/> and blends them.
    ///
    /// The track is a LOOP: past the last keyframe we blend back into the first across the
    /// wrap, which is what stops a visible jump at midnight on any cycle whose last frame is not
    /// an exact copy of its first.</summary>
    private static T Evaluate<T>(
        IReadOnlyList<DayCycleFrame<T>> frames,
        float position,
        T fallback,
        Func<T, T, float, T> lerp)
    {
        if (frames.Count == 0) return fallback;
        if (frames.Count == 1) return frames[0].Settings;

        position = Math.Clamp(position, 0f, 1f);

        // Frames arrive sorted (the parser sorts them), so a linear scan for the last frame at or
        // before `position` is enough -- a day cycle has a handful of frames, not thousands.
        int previous = -1;
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].Position <= position) previous = i;
            else break;
        }

        int next;
        float span, offset;
        if (previous < 0)
        {
            // Before the first keyframe: we are inside the wrap segment, coming from the last.
            previous = frames.Count - 1;
            next = 0;
            span = 1f - frames[previous].Position + frames[next].Position;
            offset = position + (1f - frames[previous].Position);
        }
        else if (previous == frames.Count - 1)
        {
            // After the last keyframe: same wrap segment, seen from the other side.
            next = 0;
            span = 1f - frames[previous].Position + frames[next].Position;
            offset = position - frames[previous].Position;
        }
        else
        {
            next = previous + 1;
            span = frames[next].Position - frames[previous].Position;
            offset = position - frames[previous].Position;
        }

        // Keyframes at exactly 0.0 and 1.0 leave the wrap segment zero-length. Holding the last
        // keyframe beats producing a NaN sky, and it is what the cycle visually does there
        // anyway: the two ends coincide.
        if (span <= 0f) return frames[previous].Settings;

        return lerp(frames[previous].Settings, frames[next].Settings, offset / span);
    }
}
