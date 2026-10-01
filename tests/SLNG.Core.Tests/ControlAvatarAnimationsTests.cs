using System;
using System.Linq;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-ANIMESH-02: which animations an animesh's control avatar plays, and what has to change to
/// get there. Ports <c>LLControlAvatar::updateAnimations</c> (llcontrolavatar.cpp:559-607) and the
/// start/stop loop of <c>LLVOAvatar::processAnimationStateChanges</c> (llvoavatar.cpp:6071-6104).
/// </summary>
public class ControlAvatarAnimationsTests
{
    private static readonly Guid A = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = new("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = new("00000000-0000-0000-0000-00000000000c");

    private static SignaledAnimation[] L(params (Guid id, int seq)[] items) =>
        items.Select(i => new SignaledAnimation(i.id, i.seq)).ToArray();

    private static SignaledAnimation[] Union(params SignaledAnimation[][] lists) =>
        ControlAvatarAnimations.Union(lists);

    // ---- union -----------------------------------------------------------------------------------

    [Fact]
    public void NoListsAndEmptyListsGiveNothing()
    {
        Assert.Empty(Union());
        Assert.Empty(Union(L(), L(), L()));
    }

    [Fact]
    public void ASingleListComesBackIntact()
    {
        Assert.Equal(L((A, 1), (B, 7)), Union(L((A, 1), (B, 7))));
    }

    [Fact]
    public void TheOrderIsByAnimationIdWhateverTheInputOrder()
    {
        var expected = L((A, 1), (B, 2), (C, 3));

        Assert.Equal(expected, Union(L((C, 3), (A, 1), (B, 2))));
        Assert.Equal(expected, Union(L((B, 2)), L((C, 3)), L((A, 1))));
        Assert.Equal(expected, Union(L((A, 1)), L((B, 2), (C, 3))));
    }

    [Fact]
    public void TheSameAnimationInTwoPrimsKeepsTheLargerSequenceId()
    {
        Assert.Equal(L((A, 9)), Union(L((A, 4)), L((A, 9))));
        // and it does not matter which prim is which
        Assert.Equal(L((A, 9)), Union(L((A, 9)), L((A, 4))));
    }

    [Fact]
    public void SequenceIdsAreComparedAsSignedIntegers()
    {
        // llmax(S32, S32): -3 is less than 7. Read as unsigned, -3 would be 4294967293 and win.
        Assert.Equal(L((A, 7)), Union(L((A, -3)), L((A, 7))));
        Assert.Equal(L((A, -3)), Union(L((A, -3)), L((A, -5))));
        Assert.Equal(L((A, -1)), Union(L((A, int.MinValue)), L((A, -1))));
        Assert.Equal(L((A, int.MaxValue)), Union(L((A, -1)), L((A, int.MaxValue))));
    }

    [Fact]
    public void WithinOneListTheLastEntryWinsAcrossListsTheLargerDoes()
    {
        // process_object_animation fills a std::map with `map[id] = seq`, so a repeat inside ONE
        // message overwrites (even with a smaller number); only updateAnimations takes the max, and
        // only between prims.
        Assert.Equal(L((A, 3)), Union(L((A, 5), (A, 3))));
        Assert.Equal(L((A, 4)), Union(L((A, 5), (A, 3)), L((A, 4))));
    }

    [Fact]
    public void AnAnimationThatLeavesOnePrimButIsStillInAnotherStays()
    {
        var root = L((A, 5), (B, 1));
        var child = L((A, 3));

        Assert.Equal(L((A, 5), (B, 1)), Union(root, child));

        // the root drops A: it is still signalled by the child, with the CHILD's number
        Assert.Equal(L((A, 3), (B, 1)), Union(L((B, 1)), child));

        // and gone only when no prim has it
        Assert.Equal(L((B, 1)), Union(L((B, 1)), L()));
    }

    [Fact]
    public void ADistinctAnimationInEveryPrimAllPlay()
    {
        Assert.Equal(L((A, 1), (B, 2), (C, 3)), Union(L((A, 1)), L((B, 2)), L((C, 3))));
    }

    // ---- diff against what is playing ------------------------------------------------------------

    [Fact]
    public void NothingChangedIsAnEmptyChange()
    {
        var set = L((A, 1), (B, 2));

        var change = ControlAvatarAnimations.Diff(set, L((A, 1), (B, 2)));

        Assert.True(change.IsEmpty);
        Assert.Empty(change.Start);
        Assert.Empty(change.Stop);
        Assert.Empty(change.Restart);
    }

    [Fact]
    public void WhatIsNewStartsAndWhatIsGoneStops()
    {
        var change = ControlAvatarAnimations.Diff(L((A, 1), (B, 2)), L((B, 2), (C, 5)));

        Assert.Equal(L((C, 5)), change.Start);
        Assert.Equal(new[] { A }, change.Stop);
        Assert.Empty(change.Restart);
        Assert.False(change.IsEmpty);
    }

    [Fact]
    public void AChangedSequenceIdIsARestartNotAStopAndStart()
    {
        var change = ControlAvatarAnimations.Diff(L((A, 1), (B, 2)), L((A, 2), (B, 2)));

        Assert.Equal(L((A, 2)), change.Restart);
        Assert.Empty(change.Start);
        Assert.Empty(change.Stop);
    }

    [Fact]
    public void AnyDifferenceInSequenceIdRestartsEvenADecrease()
    {
        // `found_anim->second != anim_it->second` (llvoavatar.cpp:6094) -- not "greater than". A
        // prim dropping the larger of two numbers for one animation moves the union DOWN.
        Assert.Equal(L((A, 3)), ControlAvatarAnimations.Diff(L((A, 5)), L((A, 3))).Restart);
        Assert.Equal(L((A, -5)), ControlAvatarAnimations.Diff(L((A, -3)), L((A, -5))).Restart);
    }

    [Fact]
    public void EverythingStartsFromNothingAndEverythingStopsToNothing()
    {
        var all = L((A, 1), (B, 2), (C, 3));

        var up = ControlAvatarAnimations.Diff(L(), all);
        Assert.Equal(all, up.Start);
        Assert.Empty(up.Stop);

        var down = ControlAvatarAnimations.Diff(all, L());
        Assert.Equal(new[] { A, B, C }, down.Stop);
        Assert.Empty(down.Start);
    }

    [Fact]
    public void AllThreeKindsAtOnceInADeterministicOrder()
    {
        var change = ControlAvatarAnimations.Diff(
            L((C, 1), (A, 1), (B, 1)),
            L((B, 9), (A, 1)));

        Assert.Equal(new[] { C }, change.Stop);
        Assert.Equal(L((B, 9)), change.Restart);
        Assert.Empty(change.Start);
    }

    [Fact]
    public void TheSameSetInADifferentOrderIsTheSameSet()
    {
        Assert.True(ControlAvatarAnimations.SameSet(L((A, 1), (B, 2)), L((B, 2), (A, 1))));
        Assert.False(ControlAvatarAnimations.SameSet(L((A, 1), (B, 2)), L((A, 1), (B, 3))));
        Assert.False(ControlAvatarAnimations.SameSet(L((A, 1)), L((A, 1), (B, 2))));
        Assert.True(ControlAvatarAnimations.SameSet(L(), L()));
    }
}
