using System;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class PrioritySubjectTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");

    [Fact]
    public void Focus_IsEmptyUntilSet_AndSetReplacesIt()
    {
        PrioritySubject.Set(Guid.Empty);
        Assert.Equal(Guid.Empty, PrioritySubject.Focus);

        PrioritySubject.Set(A);
        Assert.Equal(A, PrioritySubject.Focus);
        Assert.True(PrioritySubject.IsFocus(A));
        Assert.False(PrioritySubject.IsFocus(Guid.Empty));

        PrioritySubject.Set(Guid.Empty);
    }
}

public class FocusCompletionTimerTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    [Fact]
    public void Complete_AfterItStaysCompleteForTheStableTime_ReportsSecondsFromTheFocus()
    {
        var t = new FocusCompletionTimer();
        Assert.Null(t.Track(A, 10.0));

        Assert.Null(t.Update(12.0, complete: false));
        Assert.Null(t.Update(13.0, complete: true));   // first complete moment
        Assert.Null(t.Update(13.5, complete: true));   // not stable yet
        var report = t.Update(14.2, complete: true);

        Assert.NotNull(report);
        Assert.Equal(A, report!.Value.Subject);
        Assert.True(report.Value.Completed);
        Assert.Equal(3.0, report.Value.Seconds, 3);     // 13.0 - 10.0
    }

    [Fact]
    public void ABlipOfCompleteness_DoesNotCount()
    {
        var t = new FocusCompletionTimer();
        t.Track(A, 0.0);
        Assert.Null(t.Update(1.0, complete: true));
        Assert.Null(t.Update(1.4, complete: false));    // more work arrived
        Assert.Null(t.Update(3.0, complete: true));
        var report = t.Update(4.1, complete: true);

        Assert.Equal(3.0, report!.Value.Seconds, 3);    // counted from the second, stable completeness
    }

    [Fact]
    public void ReportsOnlyOncePerFocus()
    {
        var t = new FocusCompletionTimer();
        t.Track(A, 0.0);
        t.Update(1.0, true);
        Assert.NotNull(t.Update(2.5, true));
        Assert.Null(t.Update(5.0, true));
    }

    [Fact]
    public void FocusMovingOnBeforeCompletion_ReportsTheFirstAsNotComplete()
    {
        var t = new FocusCompletionTimer();
        t.Track(A, 0.0);
        t.Update(2.0, complete: false);

        var report = t.Track(B, 6.0);

        Assert.NotNull(report);
        Assert.Equal(A, report!.Value.Subject);
        Assert.False(report.Value.Completed);
        Assert.Equal(6.0, report.Value.Seconds, 3);
    }

    [Fact]
    public void TrackingTheSameSubjectAgain_DoesNotRestart()
    {
        var t = new FocusCompletionTimer();
        t.Track(A, 0.0);
        Assert.Null(t.Track(A, 5.0));
        t.Update(6.0, true);
        Assert.Equal(6.0, t.Update(7.1, true)!.Value.Seconds, 3);
    }

    [Fact]
    public void NoSubject_ReportsNothing()
    {
        var t = new FocusCompletionTimer();
        Assert.Null(t.Track(Guid.Empty, 0.0));
        Assert.Null(t.Update(5.0, true));
    }
}
