using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SLNG.Core.Tests;

public class ParkedOutfitPolicyTests
{
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)] // Cannot have mesh without anchor
    public void IsWornItemFinished_EvaluatesCorrectly(
        bool isRigged, bool hasAnchor, bool anchorHasMesh, bool expectedFinished)
    {
        bool actual = ParkedOutfitPolicy.IsWornItemFinished(isRigged, hasAnchor, anchorHasMesh);
        Assert.Equal(expectedFinished, actual);
        Assert.Equal(!expectedFinished, ParkedOutfitPolicy.ShouldReRequestOnUnpark(isRigged, hasAnchor, anchorHasMesh));
    }

    [Fact]
    public void FindItemsNeedingUnparkReRequest_FiltersUnfinishedItemsOnly()
    {
        var finishedRigged = Guid.NewGuid();
        var finishedStatic = Guid.NewGuid();
        var midBuildRigged = Guid.NewGuid();
        var arrivedWhileReduced = Guid.NewGuid();
        var discardedRig = Guid.NewGuid();

        var states = new[]
        {
            new ParkedOutfitPolicy.WornItemState(finishedRigged, IsRigged: true, HasAnchor: false, AnchorHasMesh: false),
            new ParkedOutfitPolicy.WornItemState(finishedStatic, IsRigged: false, HasAnchor: true, AnchorHasMesh: true),
            new ParkedOutfitPolicy.WornItemState(midBuildRigged, IsRigged: false, HasAnchor: true, AnchorHasMesh: false),
            new ParkedOutfitPolicy.WornItemState(arrivedWhileReduced, IsRigged: false, HasAnchor: false, AnchorHasMesh: false),
            new ParkedOutfitPolicy.WornItemState(discardedRig, IsRigged: false, HasAnchor: true, AnchorHasMesh: false),
        };

        var reRequested = ParkedOutfitPolicy.FindItemsNeedingUnparkReRequest(states);

        Assert.Equal(3, reRequested.Count);
        Assert.Contains(midBuildRigged, reRequested);
        Assert.Contains(arrivedWhileReduced, reRequested);
        Assert.Contains(discardedRig, reRequested);
        Assert.DoesNotContain(finishedRigged, reRequested);
        Assert.DoesNotContain(finishedStatic, reRequested);
    }

    [Fact]
    public void FindItemsNeedingUnparkReRequest_Empty_ReturnsEmpty()
    {
        var result = ParkedOutfitPolicy.FindItemsNeedingUnparkReRequest(Array.Empty<ParkedOutfitPolicy.WornItemState>());
        Assert.Empty(result);
    }
}

