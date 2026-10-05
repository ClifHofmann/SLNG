using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// BUG-UI-28: a friend's "is online" toast waits for the name instead of saying "Someone", and for the Display Name
/// instead of showing the login name -- but only as long as it must.
/// </summary>
public class PresenceNameWaitTests
{
    [Fact]
    public void WithEverythingKnown_TheToastIsShownAtOnce()
    {
        Assert.Equal(PresenceNameWaitDecision.Show, PresenceNameWait.Decide(true, true, true, pollsDone: 0));
    }

    [Fact]
    public void WithoutALoginName_ItWaits()
    {
        Assert.Equal(PresenceNameWaitDecision.Wait, PresenceNameWait.Decide(false, true, false, pollsDone: 0));
        Assert.Equal(PresenceNameWaitDecision.Wait, PresenceNameWait.Decide(false, true, false, pollsDone: PresenceNameWait.MaxPolls - 1));
    }

    [Fact]
    public void WithoutALoginName_ItGivesUpAfterTheLimit_AndShowsThePlaceholder()
    {
        Assert.Equal(PresenceNameWaitDecision.ShowWithoutName, PresenceNameWait.Decide(false, true, false, PresenceNameWait.MaxPolls));
        Assert.Equal(PresenceNameWaitDecision.ShowWithoutName, PresenceNameWait.Decide(false, false, true, PresenceNameWait.MaxPolls + 5));
    }

    [Fact]
    public void ADisplayNameAnswerDoesNotHelpWithoutALoginName()
    {
        // The toast has nothing to say yet however much is known about the Display Name.
        Assert.Equal(PresenceNameWaitDecision.Wait, PresenceNameWait.Decide(false, true, true, pollsDone: 3));
    }

    [Fact]
    public void WithALoginName_ItWaitsForTheDisplayNameAnswer_ForALimitedTime()
    {
        Assert.Equal(PresenceNameWaitDecision.Wait, PresenceNameWait.Decide(true, true, false, pollsDone: 0));
        Assert.Equal(PresenceNameWaitDecision.Wait, PresenceNameWait.Decide(true, true, false, PresenceNameWait.DisplayNamePolls - 1));
        // A grid that serves no Display Names never answers: after the limit the login name is shown.
        Assert.Equal(PresenceNameWaitDecision.Show, PresenceNameWait.Decide(true, true, false, PresenceNameWait.DisplayNamePolls));
    }

    [Fact]
    public void WhenDisplayNamesAreSwitchedOff_NothingIsWaitedFor()
    {
        Assert.Equal(PresenceNameWaitDecision.Show, PresenceNameWait.Decide(true, false, false, pollsDone: 0));
    }

    [Fact]
    public void AnAnswerOfNone_CountsAsAnAnswer()
    {
        // "answered" means the grid replied, including "no Display Name of their own": no reason to wait on.
        Assert.Equal(PresenceNameWaitDecision.Show, PresenceNameWait.Decide(true, true, displayNameAnswered: true, pollsDone: 1));
    }

    [Fact]
    public void TheLimitsAreSixAndTwoSeconds()
    {
        Assert.Equal(6.0, PresenceNameWait.MaxPolls * PresenceNameWait.PollSeconds, precision: 6);
        Assert.Equal(2.0, PresenceNameWait.DisplayNamePolls * PresenceNameWait.PollSeconds, precision: 6);
        Assert.True(PresenceNameWait.DisplayNamePolls < PresenceNameWait.MaxPolls);
    }
}
