using System.Collections.Generic;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-ANIM-09. A joint that an animation drove and nothing drives any more keeps its last pose,
/// as in the viewer: <c>LLJointStateBlender::blendJointStates</c> returns early when no state drives the
/// joint, "instead of resetting joint state to default, just leave it unchanged from last frame"
/// (llpose.cpp:242-246), and nothing puts it back when the motion ends (<c>LLMotion::deactivate</c>,
/// llmotion.cpp:157-170; only <c>LLVOAvatar::resetSkeleton</c>, llvoavatar.cpp:2256 does).</summary>
public class JointHoldTests
{
    private const int Pec = 2;
    private const int Head = 5;
    private const int Knee = 7;

    private static JointHold<float> NewHold(List<(int Joint, JointHoldEvent Event)>? log = null)
        => new(16, joint => joint != Head, log == null ? null : (j, e) => log.Add((j, e)));

    /// <summary>One frame in which exactly <paramref name="driven"/> are given a value.</summary>
    private static void Frame(JointHold<float> hold, params (int Joint, float Value)[] driven)
    {
        hold.BeginFrame();
        foreach (var (joint, value) in driven) hold.Driven(joint, value);
        hold.EndFrame();
    }

    private static bool TryHeld(JointHold<float> hold, int joint, out float value)
    {
        for (int i = 0; i < hold.HeldCount; i++)
        {
            if (hold.HeldJoint(i) != joint) continue;
            value = hold.HeldValue(i);
            return true;
        }
        value = 0f;
        return false;
    }

    [Fact]
    public void ADrivenJoint_IsNotHeldWhileItIsStillDriven()
    {
        var hold = NewHold();

        Frame(hold, (Pec, 1f));
        Frame(hold, (Pec, 2f));

        Assert.Equal(0, hold.HeldCount);
    }

    [Fact]
    public void AJointAnAnimationStoppedDriving_KeepsItsLastValue()
    {
        // The deformer pulse: the animation keys the pec for a quarter second, then it is gone. The pec
        // used to snap back to rest the next frame.
        var hold = NewHold();
        Frame(hold, (Pec, 1f));
        Frame(hold, (Pec, 3f));

        Frame(hold); // the animation has faded out and left the set

        Assert.Equal(1, hold.HeldCount);
        Assert.True(TryHeld(hold, Pec, out float held));
        Assert.Equal(3f, held);
    }

    [Fact]
    public void TheHoldLasts_NotJustOneFrame()
    {
        var hold = NewHold();
        Frame(hold, (Pec, 3f));
        Frame(hold);

        for (int i = 0; i < 100; i++) Frame(hold, (Knee, 0f)); // other joints keep being animated

        Assert.True(TryHeld(hold, Pec, out float held));
        Assert.Equal(3f, held);
    }

    [Fact]
    public void ANewKeyOnAHeldJoint_ReplacesTheHoldAndIsHeldInItsTurn()
    {
        var hold = NewHold();
        Frame(hold, (Pec, 3f));
        Frame(hold);
        Assert.True(hold.IsHeld(Pec));

        Frame(hold, (Pec, 5f)); // the next pulse
        Assert.False(hold.IsHeld(Pec));
        Assert.Equal(0, hold.HeldCount);

        Frame(hold);
        Assert.True(TryHeld(hold, Pec, out float held));
        Assert.Equal(5f, held);
    }

    [Fact]
    public void Clear_LetsGoOfEverythingAndForgetsWhatWasDrivenLastFrame()
    {
        // A skeleton reset / a stop: the held pose goes, and a joint that was still being driven when
        // it happened must not turn into a hold on the frame after.
        var hold = NewHold();
        Frame(hold, (Pec, 3f), (Knee, 1f));
        Frame(hold, (Knee, 1f)); // the pec is held now
        Assert.Equal(1, hold.HeldCount);

        hold.Clear();
        Assert.Equal(0, hold.HeldCount);
        Assert.False(hold.IsHeld(Pec));

        Frame(hold); // the knee was driven last frame; Clear made that history moot
        Assert.Equal(0, hold.HeldCount);
    }

    [Fact]
    public void AJointThatIsNotHoldable_IsNeverHeld()
    {
        // A joint a default motion keeps driving in the viewer (eyes, head, ...): SLNG has no such motion,
        // so holding it would leave it stuck after an animation ends.
        var hold = NewHold();
        Frame(hold, (Head, 9f), (Pec, 1f));

        Frame(hold);

        Assert.False(hold.IsHeld(Head));
        Assert.True(hold.IsHeld(Pec));
    }

    [Fact]
    public void EachJointIsHeldAtItsOwnLastValue()
    {
        var hold = NewHold();
        Frame(hold, (Pec, 1f), (Knee, 10f));
        Frame(hold, (Knee, 11f));        // the pec is released here, at 1
        Frame(hold);                     // the knee is released here, at 11

        Assert.True(TryHeld(hold, Pec, out float pec));
        Assert.True(TryHeld(hold, Knee, out float knee));
        Assert.Equal(1f, pec);
        Assert.Equal(11f, knee);
    }

    [Fact]
    public void TheDiagLogSaysWhenAJointIsHeldReplacedAndCleared()
    {
        var log = new List<(int Joint, JointHoldEvent Event)>();
        var hold = NewHold(log);

        Frame(hold, (Pec, 1f));
        Frame(hold);
        Frame(hold, (Pec, 2f));
        Frame(hold);
        hold.Clear();

        Assert.Equal(new[]
        {
            (Pec, JointHoldEvent.Held),
            (Pec, JointHoldEvent.Replaced),
            (Pec, JointHoldEvent.Held),
            (Pec, JointHoldEvent.Cleared),
        }, log);
    }

    [Fact]
    public void AJointOutsideTheTable_IsIgnored()
    {
        var hold = NewHold();

        Frame(hold, (-1, 1f), (999, 1f));
        Frame(hold);

        Assert.Equal(0, hold.HeldCount);
    }
}
