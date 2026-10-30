using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-ANIM-08. The per-joint weighted blend that replaced "highest priority takes the whole
/// joint" -- <c>LLJointStateBlender::blendJointStates</c>, llpose.cpp:238-400. Candidates are offered
/// NEWEST first, so a tie in priority is won by the one offered first.</summary>
public class JointBlendTableTests
{
    private const int Hip = 3;
    private const int Knee = 4;

    // value type is a plain float: blend(t, a, b) = a * (1 - t) + b * t, the argument order of nlerp / lerp.
    private static float Lerp(float t, float a, float b) => a * (1f - t) + b * t;

    private static float Resolve(JointBlendTable<float> table, int joint)
    {
        Assert.True(table.TryResolve(joint, Lerp, out float value));
        return value;
    }

    [Fact]
    public void AFullWeightWinnerTakesTheJointAndTheLowerOnesNoLongerCount()
    {
        var table = new JointBlendTable<float>();
        table.Offer(Hip, 4, 1f, 10f);
        table.Offer(Hip, 1, 1f, 99f);

        Assert.Equal(10f, Resolve(table, Hip));
    }

    [Fact]
    public void AFadingWinnerLetsTheLayerBelowShowThrough()
    {
        // The crossfade: a higher-priority motion at half weight over a stand at full weight.
        var table = new JointBlendTable<float>();
        table.Offer(Hip, 4, 0.25f, 10f);
        table.Offer(Hip, 1, 1f, 0f);

        // sum 0.25 then min(1, 1.25) = 1: the stand gets t = 0.25 / 1 on the accumulated pose.
        Assert.Equal(2.5f, Resolve(table, Hip), 4);
    }

    [Fact]
    public void ALoneMotionIsUsedWhole_AsTheViewerDoes()
    {
        // blendJointStates copies the first state's value whatever its weight (llpose.cpp:327-329, 366).
        var table = new JointBlendTable<float>();
        table.Offer(Hip, 2, 0.1f, 7f);

        Assert.Equal(7f, Resolve(table, Hip));
    }

    [Fact]
    public void ZeroWeightIsIgnored()
    {
        var table = new JointBlendTable<float>();
        table.Offer(Hip, 4, 0f, 10f);
        table.Offer(Hip, 1, 1f, 3f);

        Assert.Equal(3f, Resolve(table, Hip));
    }

    [Fact]
    public void EqualPriority_TheFirstOfferedIsTheNewestAndWins()
    {
        var table = new JointBlendTable<float>();
        table.Offer(Hip, 3, 1f, 1f); // newest
        table.Offer(Hip, 3, 1f, 2f); // older

        Assert.Equal(1f, Resolve(table, Hip));
    }

    [Fact]
    public void EqualPriority_ACrossfadeBlendsTheNewcomerOverTheOldOne()
    {
        // Loop A (older, fading out) replaced by loop B (newer, fading in), same priority.
        var table = new JointBlendTable<float>();
        table.Offer(Hip, 3, 0.25f, 10f); // B, newest
        table.Offer(Hip, 3, 0.75f, 0f);  // A

        Assert.Equal(2.5f, Resolve(table, Hip), 4);
    }

    [Fact]
    public void WeightsThatDoNotAddUpToOneBlendInProportion()
    {
        var table = new JointBlendTable<float>();
        table.Offer(Hip, 3, 0.25f, 10f);
        table.Offer(Hip, 3, 0.25f, 0f);

        // sum 0.5: the two share it equally.
        Assert.Equal(5f, Resolve(table, Hip), 4);
    }

    [Fact]
    public void EachJointIsDecidedOnItsOwn()
    {
        var table = new JointBlendTable<float>();
        table.Offer(Hip, 2, 1f, 1f);
        table.Offer(Knee, 5, 1f, 2f);
        table.Offer(Hip, 4, 1f, 3f);
        table.Offer(Knee, 3, 1f, 4f);

        Assert.Equal(3f, Resolve(table, Hip));
        Assert.Equal(2f, Resolve(table, Knee));
    }

    [Fact]
    public void Saturated_WhenWhatIsAheadAlreadyAddsUpToOne()
    {
        // Lets the caller skip evaluating a key that cannot show: steady state costs what it always did.
        var table = new JointBlendTable<float>();
        Assert.False(table.Saturated(Hip, 0));

        table.Offer(Hip, 4, 1f, 1f);
        Assert.True(table.Saturated(Hip, 4)); // a tie goes behind what is already there
        Assert.True(table.Saturated(Hip, 1));
        Assert.False(table.Saturated(Hip, 5)); // something above it is not shut out
    }

    [Fact]
    public void Saturated_NotWhileTheWinnerIsStillFadingIn()
    {
        var table = new JointBlendTable<float>();
        table.Offer(Hip, 4, 0.4f, 1f);

        Assert.False(table.Saturated(Hip, 1));

        table.Offer(Hip, 3, 0.6f, 2f);
        Assert.True(table.Saturated(Hip, 1));
    }

    [Fact]
    public void Clear_ForgetsEverythingAndKeepsWorking()
    {
        var table = new JointBlendTable<float>();
        table.Offer(Hip, 4, 1f, 10f);
        table.Clear();

        Assert.False(table.TryResolve(Hip, Lerp, out _));
        Assert.Equal(0, table.TouchedCount);
        Assert.False(table.Saturated(Hip, 0));

        table.Offer(Hip, 1, 1f, 5f);
        Assert.Equal(5f, Resolve(table, Hip));
        Assert.Equal(1, table.TouchedCount);
        Assert.Equal(Hip, table.TouchedJoint(0));
    }

    [Fact]
    public void MoreCandidatesThanLayers_KeepsTheHighestPriorityOnes()
    {
        var table = new JointBlendTable<float>();
        for (int i = 0; i < JointBlendTable<float>.MaxLayers + 3; i++)
            table.Offer(Hip, i, 0.1f, i); // ever higher, ever newer-and-above

        // The lowest ones dropped out; the top one is still the winner.
        Assert.True(table.TryResolve(Hip, Lerp, out float v));
        Assert.True(v > 5f);
    }
}
