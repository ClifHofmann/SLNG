using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-ANIM-08. A motion fades in and out over its ease durations instead of appearing and
/// disappearing at full weight -- <c>LLMotionController::updateMotionsByType</c>,
/// llmotioncontroller.cpp:645-734 (the weight a started motion rises with, the weight a stopped one
/// falls with), through <c>cubic_step</c> (llmath.h:381).</summary>
public class MotionStateTests
{
    private const float Dt = 1f / 30f;

    private static MotionTiming Loop(float length = 2f, float easeIn = 0.4f, float easeOut = 0.4f, float loopIn = 0f, float loopOut = -1f)
        => new(length, Loop: true, loopIn, loopOut < 0f ? length : loopOut, easeIn, easeOut);

    private static MotionTiming OneShot(float length = 2f, float easeIn = 0.4f, float easeOut = 0.4f)
        => new(length, Loop: false, 0f, length, easeIn, easeOut);

    [Fact]
    public void CubicStep_IsTheViewersSmoothstep()
    {
        Assert.Equal(0f, MotionState.CubicStep(-1f));
        Assert.Equal(0f, MotionState.CubicStep(0f));
        Assert.Equal(0.5f, MotionState.CubicStep(0.5f), 5);
        Assert.Equal(0.15625f, MotionState.CubicStep(0.25f), 5); // 0.0625 * (3 - 0.5)
        Assert.Equal(1f, MotionState.CubicStep(1f));
        Assert.Equal(1f, MotionState.CubicStep(7f));
    }

    [Fact]
    public void ANewMotionStartsAtZeroWeight_NotFullWeight()
    {
        // This is the jolt: SLNG gave a motion its whole weight the frame it appeared.
        var m = new MotionState(Loop(easeIn: 0.4f));

        Assert.Equal(0f, m.Weight);
    }

    [Fact]
    public void EaseIn_RisesMonotonicallyAndReachesFullWeightAtTheEaseDuration()
    {
        var m = new MotionState(Loop(easeIn: 0.4f));

        float previous = 0f;
        for (int i = 0; i < 10; i++)
        {
            m.Advance(Dt);
            Assert.InRange(m.Weight, previous, 1f);
            previous = m.Weight;
        }

        // 10 frames = 0.333 s: past the middle, short of the end.
        Assert.InRange(m.Weight, 0.5f, 0.99f);

        for (int i = 0; i < 4; i++) m.Advance(Dt);
        Assert.Equal(1f, m.Weight);
    }

    [Fact]
    public void ZeroEaseIn_IsFullWeightAtOnce()
    {
        var m = new MotionState(Loop(easeIn: 0f));

        m.Advance(Dt);

        Assert.Equal(1f, m.Weight);
    }

    [Fact]
    public void Stop_FadesTheWeightOutInsteadOfDroppingTheMotion()
    {
        var m = new MotionState(Loop(easeIn: 0f, easeOut: 0.5f));
        m.Advance(1f);
        Assert.Equal(1f, m.Weight);

        m.Stop();
        Assert.False(m.IsFinished);

        float previous = 1f;
        for (int i = 0; i < 8; i++)
        {
            m.Advance(Dt);
            Assert.InRange(m.Weight, 0f, previous);
            previous = m.Weight;
        }

        Assert.InRange(m.Weight, 0.01f, 0.99f); // 0.267 s into a 0.5 s fade: mid-way, not gone
        Assert.False(m.IsFinished);

        m.Advance(0.5f);
        Assert.True(m.IsFinished);
    }

    [Fact]
    public void ZeroEaseOut_EndsAtOnce()
    {
        var m = new MotionState(Loop(easeIn: 0f, easeOut: 0f));
        m.Advance(1f);

        m.Stop();
        m.Advance(Dt);

        Assert.True(m.IsFinished);
    }

    [Fact]
    public void StoppedDuringEaseIn_FadesOutFromTheWeightItHadReached()
    {
        // mResidualWeight is the weight when the ease-out starts (llmotioncontroller.cpp:670-674): the
        // fade starts from where the fade-in stopped, it does not jump up to 1 first.
        var m = new MotionState(Loop(easeIn: 1f, easeOut: 1f));
        for (int i = 0; i < 9; i++) m.Advance(Dt); // 0.3 s into a 1 s fade-in
        float reached = m.Weight;
        Assert.InRange(reached, 0.1f, 0.5f);

        m.Stop();
        m.Advance(Dt);

        Assert.InRange(m.Weight, 0f, reached);
    }

    [Fact]
    public void RestartedWhileFadingOut_FadesBackInFromTheCurrentWeight()
    {
        // LLMotionController::updateMotionsByType: ease-in weight = residual + (1 - residual) * step.
        var m = new MotionState(Loop(easeIn: 1f, easeOut: 1f));
        m.Advance(2f);
        m.Stop();
        for (int i = 0; i < 15; i++) m.Advance(Dt); // half way down
        float falling = m.Weight;
        Assert.InRange(falling, 0.2f, 0.8f);

        m.Activate();

        Assert.False(m.IsFinished);
        m.Advance(Dt);
        Assert.InRange(m.Weight, falling - 0.05f, 1f); // no dip below where it was, no jump to 1
        Assert.True(m.Weight < 0.99f);
    }

    [Fact]
    public void ALoopWrapsAtItsOutPointBackToTheInPoint()
    {
        // llkeyframemotion.cpp:701-712: past the out point, loop_in + fmod(t - loop_out, loop_out - loop_in).
        var m = new MotionState(Loop(length: 2f, easeIn: 0f, loopIn: 0.5f, loopOut: 1.5f));

        m.Advance(1.4f);
        Assert.Equal(1.4f, m.PoseTime, 4);

        m.Advance(0.3f); // elapsed 1.7 -> 0.2 past the out point
        Assert.Equal(0.5f + 0.2f, m.PoseTime, 4);

        m.Advance(1.0f); // elapsed 2.7 -> 1.2 past: 1.2 % 1.0
        Assert.Equal(0.5f + 0.2f, m.PoseTime, 3);
    }

    [Fact]
    public void ALoopStartsAtTimeZero_NotAtItsInPoint()
    {
        // The viewer plays the intro before the loop in point once: time starts at 0
        // (LLKeyframeMotion::onActivate, llkeyframemotion.cpp:676; onUpdate is handed time since activation).
        var m = new MotionState(Loop(length: 2f, easeIn: 0f, loopIn: 0.5f, loopOut: 1.5f));

        Assert.Equal(0f, m.PoseTime);
    }

    [Fact]
    public void AStoppedLoopPlaysOnToItsEndInsteadOfCuttingOff()
    {
        // LLKeyframeMotion::setStopTime (llkeyframemotion.cpp:801-821): a loop whose out point is before
        // the end keeps going into its outro, and only then eases out.
        var m = new MotionState(Loop(length: 3f, easeIn: 0f, easeOut: 0.5f, loopIn: 0f, loopOut: 2f));
        m.Advance(0.5f);

        m.Stop();

        // Ease-out begins at (now - loop fraction) + (duration - loop in) - ease out = 0 + 3 - 0.5 = 2.5 s.
        m.Advance(1.0f); // elapsed 1.5
        Assert.Equal(1f, m.Weight);
        Assert.False(m.IsFinished);
        Assert.Equal(1.5f, m.PoseTime, 4); // advancing, not wrapped

        m.Advance(1.2f); // elapsed 2.7: 0.2 s into the fade
        Assert.InRange(m.Weight, 0.01f, 0.99f);

        m.Advance(1.0f);
        Assert.True(m.IsFinished);
        Assert.True(m.PoseTime <= 3f);
    }

    [Fact]
    public void Seek_MovesTheClockAndLeavesTheWeightAlone()
    {
        var m = new MotionState(Loop(easeIn: 0f));
        m.Advance(0.2f);

        m.Seek(1.25f);

        Assert.Equal(1.25f, m.PoseTime);
        Assert.Equal(1f, m.Weight);

        m.Advance(0.25f);
        Assert.Equal(1.5f, m.PoseTime, 4);
    }

    [Fact]
    public void ANonLoopingMotionHoldsItsLastPoseAtTheEnd()
    {
        var m = new MotionState(OneShot(length: 1f, easeIn: 0f));

        m.Advance(5f);

        Assert.Equal(1f, m.PoseTime);
        Assert.Equal(1f, m.Weight);
        Assert.False(m.IsFinished);
    }
}
