using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// BUG-UI-28: a friend's "is online" toast waits for the name instead of saying "Someone", and for the Display Name
/// instead of showing the login name -- but only as long as it must. The Display Name wait counts from when the login
/// name appeared, not from when the toast started.
/// </summary>
public class PresenceNameWaitTests
{
    private const int Max = PresenceNameWait.MaxPolls;
    private const int Disp = PresenceNameWait.DisplayNamePolls;

    [Fact]
    public void WithEverythingKnown_TheToastIsShownAtOnce()
    {
        Assert.Equal(PresenceNameWaitDecision.Show,
            PresenceNameWait.Decide(wantDisplayName: true, displayNameAnswered: true, pollsDone: 0, loginNameKnownAtPoll: 0));
    }

    [Fact]
    public void WithoutALoginName_ItWaits()
    {
        Assert.Equal(PresenceNameWaitDecision.Wait, PresenceNameWait.Decide(true, false, 0, null));
        Assert.Equal(PresenceNameWaitDecision.Wait, PresenceNameWait.Decide(true, false, Max - 1, null));
    }

    [Fact]
    public void WithoutALoginName_ItGivesUpAfterTheLimit_AndShowsThePlaceholder()
    {
        Assert.Equal(PresenceNameWaitDecision.ShowWithoutName, PresenceNameWait.Decide(true, false, Max, null));
        Assert.Equal(PresenceNameWaitDecision.ShowWithoutName, PresenceNameWait.Decide(false, true, Max + 5, null));
    }

    [Fact]
    public void ADisplayNameAnswerDoesNotHelpWithoutALoginName()
    {
        // The toast has nothing to say yet however much is known about the Display Name.
        Assert.Equal(PresenceNameWaitDecision.Wait, PresenceNameWait.Decide(true, true, 3, null));
    }

    [Fact]
    public void WithALoginName_ItWaitsForTheDisplayNameAnswer_ForALimitedTime()
    {
        Assert.Equal(PresenceNameWaitDecision.Wait, PresenceNameWait.Decide(true, false, 0, 0));
        Assert.Equal(PresenceNameWaitDecision.Wait, PresenceNameWait.Decide(true, false, Disp - 1, 0));
        // A grid that serves no Display Names never answers: after the limit the login name is shown.
        Assert.Equal(PresenceNameWaitDecision.Show, PresenceNameWait.Decide(true, false, Disp, 0));
    }

    [Fact]
    public void TheDisplayNameWaitCountsFromWhenTheLoginNameAppeared_NotFromTheStart()
    {
        // The login name arrived late (poll 10, 2.5 s in); the Display Name was only asked for then. A wait counted from
        // the start would be over already and show the login name -- the bug this guards against.
        Assert.Equal(PresenceNameWaitDecision.Wait, PresenceNameWait.Decide(true, false, pollsDone: 10, loginNameKnownAtPoll: 10));
        Assert.Equal(PresenceNameWaitDecision.Wait, PresenceNameWait.Decide(true, false, 10 + Disp - 1, 10));
        Assert.Equal(PresenceNameWaitDecision.Show, PresenceNameWait.Decide(true, false, 10 + Disp, 10));
    }

    [Fact]
    public void WhenDisplayNamesAreNotWanted_NothingIsWaitedFor()
    {
        Assert.Equal(PresenceNameWaitDecision.Show, PresenceNameWait.Decide(false, false, 0, 0));
    }

    [Fact]
    public void AnAnswerOfNone_CountsAsAnAnswer()
    {
        // "answered" means the grid replied, including "no Display Name of their own": no reason to wait on.
        Assert.Equal(PresenceNameWaitDecision.Show, PresenceNameWait.Decide(true, displayNameAnswered: true, pollsDone: 1, loginNameKnownAtPoll: 0));
    }

    [Fact]
    public void TheLimitsAreSixAndTwoSeconds()
    {
        Assert.Equal(6.0, Max * PresenceNameWait.PollSeconds, precision: 6);
        Assert.Equal(2.0, Disp * PresenceNameWait.PollSeconds, precision: 6);
        Assert.True(Disp < Max);
    }
}
