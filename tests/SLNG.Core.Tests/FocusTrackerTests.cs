using System;
using System.Collections.Generic;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class FocusTrackerTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static FocusTracker.Candidate Near(Guid id, float distToCamera, float cos = 1f) =>
        new(id, distToCamera, float.NaN, cos);

    private static FocusTracker.Candidate OnFocusPoint(Guid id, float distToFocus, float distToCamera = 30f) =>
        new(id, distToCamera, distToFocus, 1f);

    [Fact]
    public void Nobody_CloseOrInView_MeansNoSubject()
    {
        var t = new FocusTracker();

        Assert.Equal(Guid.Empty, t.Update(0, new[] { Near(A, 40f), Near(B, 3f, cos: 0.1f) }));
    }

    [Fact]
    public void AvatarCloseToTheCameraAndInFront_BecomesTheSubject()
    {
        var t = new FocusTracker();

        Assert.Equal(A, t.Update(0, new[] { Near(A, 3.5f), Near(B, 20f) }));
    }

    [Fact]
    public void ExplicitFocusPoint_BeatsProximity_EvenFromFarAway()
    {
        var t = new FocusTracker();

        var picked = t.Update(0, new[] { Near(B, 2f), OnFocusPoint(A, 0.4f, distToCamera: 40f) });

        Assert.Equal(A, picked);
    }

    [Fact]
    public void ExplicitFocusPoint_TooFarFromTheAvatar_IsNotAFocus()
    {
        var t = new FocusTracker();

        Assert.Equal(Guid.Empty, t.Update(0, new[] { OnFocusPoint(A, 9f, distToCamera: 40f) }));
    }

    [Fact]
    public void CurrentSubject_IsKeptAgainstASlightlyBetterCandidate()
    {
        var t = new FocusTracker();
        Assert.Equal(A, t.Update(0, new[] { Near(A, 3.0f), Near(B, 6f) }));

        // B is now marginally closer, but not by enough to take the subject.
        Assert.Equal(A, t.Update(1, new[] { Near(A, 3.2f), Near(B, 3.0f) }));
    }

    [Fact]
    public void ClearlyBetterCandidate_TakesOverAtOnce()
    {
        var t = new FocusTracker();
        Assert.Equal(A, t.Update(0, new[] { Near(A, 5.0f), Near(B, 20f) }));

        Assert.Equal(B, t.Update(1, new[] { Near(A, 5.5f), Near(B, 1.5f) }));
    }

    [Fact]
    public void SubjectThatLeavesView_IsHeldForTheGraceTime_ThenDropped()
    {
        var t = new FocusTracker();
        Assert.Equal(A, t.Update(0, new[] { Near(A, 3f) }));

        Assert.Equal(A, t.Update(0.5, new[] { Near(A, 30f) }));
        Assert.Equal(Guid.Empty, t.Update(0.5 + FocusTracker.LossGraceSeconds + 0.1, new[] { Near(A, 30f) }));
    }

    [Fact]
    public void SubjectThatLeavesView_IsReplacedImmediatelyByAnotherEligibleAvatar()
    {
        var t = new FocusTracker();
        Assert.Equal(A, t.Update(0, new[] { Near(A, 3f) }));

        Assert.Equal(B, t.Update(0.25, new[] { Near(A, 30f), Near(B, 4f) }));
    }

    [Fact]
    public void Changed_ReportsOnlyARealChangeOfSubject()
    {
        var t = new FocusTracker();

        t.Update(0, new[] { Near(A, 3f) });
        Assert.True(t.Changed);
        t.Update(1, new[] { Near(A, 3.1f) });
        Assert.False(t.Changed);
        t.Update(2, new[] { Near(A, 3.1f), Near(B, 0.5f) });
        Assert.True(t.Changed);
    }

    [Fact]
    public void NoCandidatesAtAll_DropsAfterTheGrace()
    {
        var t = new FocusTracker();
        t.Update(0, new[] { Near(A, 3f) });

        Assert.Equal(A, t.Update(0.25, Array.Empty<FocusTracker.Candidate>()));
        Assert.Equal(Guid.Empty, t.Update(10, Array.Empty<FocusTracker.Candidate>()));
    }
}
