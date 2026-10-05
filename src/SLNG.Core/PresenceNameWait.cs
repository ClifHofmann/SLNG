namespace SLNG.Core;

/// <summary>What a friend's "is online" toast does next while it waits for the friend's name.</summary>
public enum PresenceNameWaitDecision
{
    /// <summary>Show it now with the best name known.</summary>
    Show,

    /// <summary>Not yet: check again after <see cref="PresenceNameWait.PollSeconds"/>.</summary>
    Wait,

    /// <summary>Give up waiting for the login name: show it with a placeholder.</summary>
    ShowWithoutName,
}

/// <summary>
/// BUG-UI-28: right after login every online friend announces themselves at once, before the grid has delivered
/// their names. A toast that says "Someone is online." (no login name yet) or shows the login name where the person
/// has a Display Name (not asked for or answered yet) is wrong, so the toast waits -- briefly, because it is also a
/// notification and a late one is a poor one.
/// <para>The two names wait differently. The <b>login name</b> is needed: without it the toast has nothing to say,
/// so it waits up to <see cref="MaxPolls"/> polls. The <b>Display Name</b> is a refinement: it is waited for only
/// while the grid has not answered at all (an answer of "none" counts), and only for <see cref="DisplayNamePolls"/>
/// polls -- a grid that serves no Display Names never answers, and must not hold every toast for the full time.</para>
/// </summary>
public static class PresenceNameWait
{
    public const double PollSeconds = 0.25;

    /// <summary>Longest wait for the login name: 6 s.</summary>
    public const int MaxPolls = 24;

    /// <summary>Longest wait for a Display Name answer once the login name is known: 2 s.</summary>
    public const int DisplayNamePolls = 8;

    /// <param name="haveLoginName">The friend's login name is known.</param>
    /// <param name="wantDisplayName">The person uses Display Names (the preference).</param>
    /// <param name="displayNameAnswered">The grid has answered for this friend, even if only "no Display Name of their own".</param>
    /// <param name="pollsDone">How many times the toast has already waited.</param>
    public static PresenceNameWaitDecision Decide(bool haveLoginName, bool wantDisplayName, bool displayNameAnswered, int pollsDone)
    {
        if (!haveLoginName)
            return pollsDone >= MaxPolls ? PresenceNameWaitDecision.ShowWithoutName : PresenceNameWaitDecision.Wait;

        if (wantDisplayName && !displayNameAnswered && pollsDone < DisplayNamePolls)
            return PresenceNameWaitDecision.Wait;

        return PresenceNameWaitDecision.Show;
    }
}
