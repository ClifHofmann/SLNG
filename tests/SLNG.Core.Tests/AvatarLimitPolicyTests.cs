using System;
using System.Collections.Generic;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class AvatarLimitPolicyTests
{
    [Fact]
    public void Presets_MatchReferenceViewerFeaturetable()
    {
        // featuretable.txt lines 98, 185, 269, 351
        Assert.Equal(3, AvatarLimitPolicy.PresetLowCap);
        Assert.Equal(7, AvatarLimitPolicy.PresetMediumCap);
        Assert.Equal(11, AvatarLimitPolicy.PresetHighCap);
        Assert.Equal(16, AvatarLimitPolicy.PresetUltraCap);
    }

    [Fact]
    public void Evaluate_EmptyList_ReturnsEmpty()
    {
        var result = AvatarLimitPolicy.Evaluate(Array.Empty<AvatarLimitPolicy.Candidate>(), 10);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10)]
    public void Evaluate_CapZeroOrNegative_RendersAllFully(int cap)
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var candidates = new[]
        {
            new AvatarLimitPolicy.Candidate(id1, 10f, IsCurrentlyFull: false, IsExempt: false),
            new AvatarLimitPolicy.Candidate(id2, 50f, IsCurrentlyFull: false, IsExempt: false),
        };

        var result = AvatarLimitPolicy.Evaluate(candidates, cap);

        Assert.True(result[id1]);
        Assert.True(result[id2]);
    }

    [Fact]
    public void Evaluate_FewerCandidatesThanCap_AllRenderFully()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var candidates = new[]
        {
            new AvatarLimitPolicy.Candidate(id1, 10f, IsCurrentlyFull: false, IsExempt: false),
            new AvatarLimitPolicy.Candidate(id2, 30f, IsCurrentlyFull: false, IsExempt: false),
        };

        var result = AvatarLimitPolicy.Evaluate(candidates, cap: 5);

        Assert.True(result[id1]);
        Assert.True(result[id2]);
    }

    [Fact]
    public void Evaluate_MoreCandidatesThanCap_PicksNearestFirst()
    {
        var id1 = Guid.NewGuid(); // 5m
        var id2 = Guid.NewGuid(); // 15m
        var id3 = Guid.NewGuid(); // 25m
        var id4 = Guid.NewGuid(); // 35m

        var candidates = new[]
        {
            new AvatarLimitPolicy.Candidate(id3, 25f, IsCurrentlyFull: false, IsExempt: false),
            new AvatarLimitPolicy.Candidate(id1, 5f, IsCurrentlyFull: false, IsExempt: false),
            new AvatarLimitPolicy.Candidate(id4, 35f, IsCurrentlyFull: false, IsExempt: false),
            new AvatarLimitPolicy.Candidate(id2, 15f, IsCurrentlyFull: false, IsExempt: false),
        };

        // Cap of 2: id1 (5m) and id2 (15m) must be full; id3 and id4 reduced
        var result = AvatarLimitPolicy.Evaluate(candidates, cap: 2);

        Assert.True(result[id1]);
        Assert.True(result[id2]);
        Assert.False(result[id3]);
        Assert.False(result[id4]);
    }

    [Fact]
    public void Evaluate_ExemptCandidates_AreAlwaysFullAndDoNotConsumeSlots()
    {
        var selfId = Guid.NewGuid();
        var pinnedId = Guid.NewGuid();
        var other1 = Guid.NewGuid(); // 10m
        var other2 = Guid.NewGuid(); // 20m
        var other3 = Guid.NewGuid(); // 30m

        var candidates = new[]
        {
            new AvatarLimitPolicy.Candidate(selfId, 0f, IsCurrentlyFull: true, IsExempt: true),
            new AvatarLimitPolicy.Candidate(pinnedId, 100f, IsCurrentlyFull: true, IsExempt: true),
            new AvatarLimitPolicy.Candidate(other1, 10f, IsCurrentlyFull: false, IsExempt: false),
            new AvatarLimitPolicy.Candidate(other2, 20f, IsCurrentlyFull: false, IsExempt: false),
            new AvatarLimitPolicy.Candidate(other3, 30f, IsCurrentlyFull: false, IsExempt: false),
        };

        // Cap of 2 for non-exempt: self and pinned are exempt (Full).
        // other1 (10m) and other2 (20m) fill the 2 slots (Full).
        // other3 (30m) is reduced.
        var result = AvatarLimitPolicy.Evaluate(candidates, cap: 2);

        Assert.True(result[selfId]);
        Assert.True(result[pinnedId]);
        Assert.True(result[other1]);
        Assert.True(result[other2]);
        Assert.False(result[other3]);
    }

    [Fact]
    public void Evaluate_Hysteresis_PreventsFlickeringAtBoundary()
    {
        var incumbentId = Guid.NewGuid();
        var challengerId = Guid.NewGuid();

        // Incumbent was already Full at 20.0m.
        // Challenger is Reduced at 19.0m (5% closer, but within 15% hysteresis margin).
        // Incumbent effective distance: 20 * (1 - 0.15) = 17.0m < 19.0m.
        // With cap = 1, incumbent stays Full!
        var candidates = new[]
        {
            new AvatarLimitPolicy.Candidate(incumbentId, 20.0f, IsCurrentlyFull: true, IsExempt: false),
            new AvatarLimitPolicy.Candidate(challengerId, 19.0f, IsCurrentlyFull: false, IsExempt: false),
        };

        var result = AvatarLimitPolicy.Evaluate(candidates, cap: 1, hysteresisFraction: 0.15f);

        Assert.True(result[incumbentId]);
        Assert.False(result[challengerId]);
    }

    [Fact]
    public void Evaluate_Hysteresis_AllowsDisplacementWhenChallengerIsSignificantlyCloser()
    {
        var incumbentId = Guid.NewGuid();
        var challengerId = Guid.NewGuid();

        // Incumbent was Full at 20.0m (effective distance 17.0m).
        // Challenger moves to 16.0m (clearly closer than 17.0m).
        // Challenger should now displace incumbent.
        var candidates = new[]
        {
            new AvatarLimitPolicy.Candidate(incumbentId, 20.0f, IsCurrentlyFull: true, IsExempt: false),
            new AvatarLimitPolicy.Candidate(challengerId, 16.0f, IsCurrentlyFull: false, IsExempt: false),
        };

        var result = AvatarLimitPolicy.Evaluate(candidates, cap: 1, hysteresisFraction: 0.15f);

        Assert.False(result[incumbentId]);
        Assert.True(result[challengerId]);
    }

    [Fact]
    public void Evaluate_Hysteresis_SymmetricallyProtectsNewIncumbent()
    {
        var avatarA = Guid.NewGuid();
        var avatarB = Guid.NewGuid();

        // Avatar B has taken the slot and is now Full at 16.0m.
        // Effective distance of B: 16 * 0.85 = 13.6m.
        // Avatar A moves from 20m to 15m.
        // Although A (15m) is closer than B's actual distance (16m),
        // B's effective distance (13.6m) is lower than A's (15m).
        // B stays Full!
        var candidates = new[]
        {
            new AvatarLimitPolicy.Candidate(avatarB, 16.0f, IsCurrentlyFull: true, IsExempt: false),
            new AvatarLimitPolicy.Candidate(avatarA, 15.0f, IsCurrentlyFull: false, IsExempt: false),
        };

        var result = AvatarLimitPolicy.Evaluate(candidates, cap: 1, hysteresisFraction: 0.15f);

        Assert.True(result[avatarB]);
        Assert.False(result[avatarA]);
    }

    [Fact]
    public void Evaluate_AbsoluteMargin_IncumbentKeepsSlotAgainstNearbyChallenger()
    {
        var incumbentId = Guid.NewGuid();
        var challengerId = Guid.NewGuid();

        // Incumbent is full at 5.5 m, challenger is 0.5 m closer at 5.0 m. The relative discount
        // alone (15 % of 5.5 = 0.83 m) would swap them; the 2 m absolute margin keeps the incumbent.
        var candidates = new[]
        {
            new AvatarLimitPolicy.Candidate(incumbentId, 5.5f, IsCurrentlyFull: true, IsExempt: false),
            new AvatarLimitPolicy.Candidate(challengerId, 5.0f, IsCurrentlyFull: false, IsExempt: false),
        };

        var result = AvatarLimitPolicy.Evaluate(candidates, cap: 1);

        Assert.True(result[incumbentId]);
        Assert.False(result[challengerId]);
    }

    [Fact]
    public void Evaluate_AbsoluteMargin_StillYieldsToAClearlyCloserChallenger()
    {
        var incumbentId = Guid.NewGuid();
        var challengerId = Guid.NewGuid();

        // Incumbent at 6 m has an effective distance of 4 m; a challenger at 3 m is clearly closer.
        var candidates = new[]
        {
            new AvatarLimitPolicy.Candidate(incumbentId, 6.0f, IsCurrentlyFull: true, IsExempt: false),
            new AvatarLimitPolicy.Candidate(challengerId, 3.0f, IsCurrentlyFull: false, IsExempt: false),
        };

        var result = AvatarLimitPolicy.Evaluate(candidates, cap: 1);

        Assert.False(result[incumbentId]);
        Assert.True(result[challengerId]);
    }

    [Fact]
    public void Evaluate_AbsoluteMargin_ZeroDisablesItAndKeepsTheRelativeBehaviour()
    {
        var incumbentId = Guid.NewGuid();
        var challengerId = Guid.NewGuid();

        var candidates = new[]
        {
            new AvatarLimitPolicy.Candidate(incumbentId, 5.5f, IsCurrentlyFull: true, IsExempt: false),
            new AvatarLimitPolicy.Candidate(challengerId, 5.0f, IsCurrentlyFull: false, IsExempt: false),
        };

        // Relative only: incumbent effective = 5.5 * 0.85 = 4.675 m < 5.0 m, so it still wins here...
        var keeps = AvatarLimitPolicy.Evaluate(candidates, cap: 1, absoluteMarginMetres: 0f);
        Assert.True(keeps[incumbentId]);

        // ...but a challenger at 4.5 m beats it, which the default 2 m margin would have prevented.
        candidates[1] = new AvatarLimitPolicy.Candidate(challengerId, 4.5f, IsCurrentlyFull: false, IsExempt: false);
        var swaps = AvatarLimitPolicy.Evaluate(candidates, cap: 1, absoluteMarginMetres: 0f);
        Assert.False(swaps[incumbentId]);
        Assert.True(swaps[challengerId]);

        var heldByMargin = AvatarLimitPolicy.Evaluate(candidates, cap: 1);
        Assert.True(heldByMargin[incumbentId]);
        Assert.False(heldByMargin[challengerId]);
    }
}
