using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-ANIM-07. The per-joint, highest-priority-wins rule both animation channels
/// (rotation and position) share (LLJointStateBlender::addJointState, llpose.cpp:200-230).</summary>
public class JointPriorityTableTests
{
    private const int Hip = 3;
    private const int Knee = 4;

    [Fact]
    public void EmptyTable_AnyCandidateWins()
    {
        var table = new JointPriorityTable<string>();

        Assert.True(table.Wins(Hip, priority: 0));
        Assert.True(table.Offer(Hip, 0, "stand"));
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void HigherPriorityReplacesLower_LowerNeverReplacesHigher()
    {
        var table = new JointPriorityTable<string>();

        table.Offer(Hip, 2, "stand");
        Assert.True(table.Offer(Hip, 4, "pose"));
        Assert.False(table.Offer(Hip, 3, "gesture"));

        Assert.True(table.TryGet(Hip, out int priority, out string value));
        Assert.Equal(4, priority);
        Assert.Equal("pose", value);
    }

    [Fact]
    public void EqualPriority_TheLaterCandidateWins()
    {
        var table = new JointPriorityTable<string>();

        table.Offer(Hip, 3, "first");
        Assert.True(table.Wins(Hip, 3));
        table.Offer(Hip, 3, "second");

        Assert.True(table.TryGet(Hip, out string value));
        Assert.Equal("second", value);
    }

    [Fact]
    public void EachJointIsDecidedOnItsOwn()
    {
        // The pose wins the knee, the gesture wins the hip: priority is per joint, not per animation.
        var table = new JointPriorityTable<string>();

        table.Offer(Hip, 2, "pose");
        table.Offer(Knee, 5, "pose");
        table.Offer(Hip, 4, "gesture");
        table.Offer(Knee, 3, "gesture");

        Assert.True(table.TryGet(Hip, out string hip));
        Assert.True(table.TryGet(Knee, out string knee));
        Assert.Equal("gesture", hip);
        Assert.Equal("pose", knee);
    }

    [Fact]
    public void WinsDoesNotModifyTheTable()
    {
        var table = new JointPriorityTable<string>();
        table.Offer(Hip, 3, "held");

        Assert.True(table.Wins(Hip, 9));
        Assert.True(table.TryGet(Hip, out string value));
        Assert.Equal("held", value);
    }

    [Fact]
    public void SetOverridesWhateverHoldsTheJoint()
    {
        // The head-gaze overlay writes at priority 99 whatever is there.
        var table = new JointPriorityTable<string>();
        table.Offer(Hip, 5, "pose");

        table.Set(Hip, 99, "gaze");

        Assert.True(table.TryGet(Hip, out int priority, out string value));
        Assert.Equal(99, priority);
        Assert.Equal("gaze", value);
    }

    [Fact]
    public void TryGet_OnAJointNobodyKeyed_IsFalse()
    {
        var table = new JointPriorityTable<string>();

        Assert.False(table.TryGet(Hip, out string value));
        Assert.Null(value);
    }

    [Fact]
    public void Clear_EmptiesTheTable()
    {
        var table = new JointPriorityTable<string>();
        table.Offer(Hip, 3, "x");
        table.Offer(Knee, 3, "y");

        table.Clear();

        Assert.Equal(0, table.Count);
        Assert.True(table.Wins(Hip, 0));
    }

    [Fact]
    public void Enumerating_YieldsEveryJointWithItsWinner()
    {
        var table = new JointPriorityTable<string>();
        table.Offer(Hip, 3, "a");
        table.Offer(Knee, 1, "b");
        table.Offer(Knee, 2, "c");

        var seen = new System.Collections.Generic.Dictionary<int, string>();
        foreach (var (joint, entry) in table) seen[joint] = entry.Value;

        Assert.Equal(2, seen.Count);
        Assert.Equal("a", seen[Hip]);
        Assert.Equal("c", seen[Knee]);
    }
}
