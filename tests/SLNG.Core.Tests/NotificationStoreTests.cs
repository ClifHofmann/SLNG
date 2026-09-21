using System;
using System.Linq;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// The counting and dismissal rules behind the notification window. Worth testing away from a
/// scene because a count that drifts from its list is the specific way this kind of window goes
/// wrong, and it does so quietly.
/// </summary>
public class NotificationStoreTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void CountsAreKeptPerTab()
    {
        var store = new NotificationStore();
        store.Add(NotificationKind.Transaction, Alice, "paid you");
        store.Add(NotificationKind.Transaction, Bob, "paid you");
        store.Add(NotificationKind.Group, Guid.Empty, "notice");

        Assert.Equal(2, store.CountOf(NotificationKind.Transaction));
        Assert.Equal(1, store.CountOf(NotificationKind.Group));
        Assert.Equal(0, store.CountOf(NotificationKind.System));
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public void NewestIsFirst()
    {
        var store = new NotificationStore();
        store.Add(NotificationKind.System, Guid.Empty, "older");
        store.Add(NotificationKind.System, Guid.Empty, "newer");

        Assert.Equal("newer", store.Entries[0].Text);
        Assert.Equal("older", store.Entries[1].Text);
    }

    [Fact]
    public void DismissingTwiceCannotMoveACountTwice()
    {
        // The failure this guards: dismiss one, then "close all", and a naive implementation
        // subtracts twice and renders "Transaktionen (-1)".
        var store = new NotificationStore();
        var entry = store.Add(NotificationKind.Transaction, Alice, "paid you");

        Assert.True(store.Dismiss(entry.Id));
        Assert.False(store.Dismiss(entry.Id));
        Assert.Equal(0, store.CountOf(NotificationKind.Transaction));
    }

    [Fact]
    public void ANoOpDismissalRaisesNothing()
    {
        var store = new NotificationStore();
        int raised = 0;
        store.Changed += (_, _) => raised++;

        Assert.False(store.Dismiss(Guid.NewGuid()));
        Assert.Equal(0, raised);
    }

    [Fact]
    public void DismissAllClearsOneTabOnly()
    {
        var store = new NotificationStore();
        store.Add(NotificationKind.Transaction, Alice, "paid you");
        store.Add(NotificationKind.Group, Guid.Empty, "notice");

        Assert.Equal(1, store.DismissAll(NotificationKind.Transaction));
        Assert.Equal(0, store.CountOf(NotificationKind.Transaction));
        Assert.Equal(1, store.CountOf(NotificationKind.Group));
    }

    [Fact]
    public void DismissAllWithoutAKindClearsEverything()
    {
        var store = new NotificationStore();
        store.Add(NotificationKind.Transaction, Alice, "paid you");
        store.Add(NotificationKind.Group, Guid.Empty, "notice");

        Assert.Equal(2, store.DismissAll());
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void TheOldestFallOffPastTheCap()
    {
        var store = new NotificationStore();
        for (int i = 0; i < NotificationStore.MaxEntries + 50; i++)
        {
            store.Add(NotificationKind.System, Guid.Empty, $"line {i}");
        }

        Assert.Equal(NotificationStore.MaxEntries, store.Count);
        // The newest survived; the very first did not.
        Assert.Equal($"line {NotificationStore.MaxEntries + 49}", store.Entries[0].Text);
        Assert.DoesNotContain(store.Entries, e => e.Text == "line 0");
    }

    [Fact]
    public void ALateNameReplacesThePlaceholder()
    {
        // A payment is announced the moment it lands, which is usually before the payer's name has
        // resolved. Without this the window keeps saying "Someone paid you L$ 2200" forever --
        // the one thing the entry exists to answer.
        var store = new NotificationStore();
        store.Add(NotificationKind.Transaction, Alice, "Someone paid you L$ 2200.");
        store.Add(NotificationKind.Transaction, Bob, "Someone paid you L$ 5.");

        Assert.Equal(1, store.ResolveSender(Alice, "Someone", "De Nise"));

        Assert.Equal("Someone paid you L$ 5.", store.Entries[0].Text);       // Bob's, untouched
        Assert.Equal("De Nise paid you L$ 2200.", store.Entries[1].Text);
    }

    [Fact]
    public void ALateNameAlsoFixesWhatTheWindowLinks()
    {
        // The window makes the sender's name a link by finding SenderName inside Text. If only
        // the text were rewritten, it would then look for "Someone" and find the real name there
        // instead -- and the link would quietly disappear at the moment it became useful.
        var store = new NotificationStore();
        store.Add(NotificationKind.Transaction, Alice, "Someone paid you L$ 10.", senderName: "Someone");

        store.ResolveSender(Alice, "Someone", "De Nise");

        Assert.Equal("De Nise", store.Entries[0].SenderName);
        Assert.Contains(store.Entries[0].SenderName, store.Entries[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvingAnUnknownSenderChangesNothing()
    {
        var store = new NotificationStore();
        store.Add(NotificationKind.Transaction, Alice, "Someone paid you L$ 1.");

        Assert.Equal(0, store.ResolveSender(Bob, "Someone", "Other Person"));
        Assert.Equal(0, store.ResolveSender(Guid.Empty, "Someone", "Other Person"));
        Assert.Equal(0, store.ResolveSender(Alice, "Someone", ""));
        Assert.Equal("Someone paid you L$ 1.", store.Entries[0].Text);
    }

    [Fact]
    public void NewEntriesStartUnread()
    {
        var store = new NotificationStore();
        store.Add(NotificationKind.Transaction, Alice, "paid you");
        store.Add(NotificationKind.Group, Guid.Empty, "notice");

        Assert.Equal(2, store.UnreadCount);
        Assert.Equal(1, store.UnreadOf(NotificationKind.Transaction));
    }

    [Fact]
    public void ReadingOneTabLeavesTheOthersAlone()
    {
        // Opening the window on Transactions must not silently clear the badge for group notices
        // the user has not looked at -- the badge would then be lying about what is waiting.
        var store = new NotificationStore();
        store.Add(NotificationKind.Transaction, Alice, "paid you");
        store.Add(NotificationKind.Group, Guid.Empty, "notice");

        Assert.Equal(1, store.MarkRead(NotificationKind.Transaction));

        Assert.Equal(0, store.UnreadOf(NotificationKind.Transaction));
        Assert.Equal(1, store.UnreadOf(NotificationKind.Group));
        Assert.Equal(1, store.UnreadCount);
    }

    [Fact]
    public void ReadingIsIdempotent()
    {
        var store = new NotificationStore();
        store.Add(NotificationKind.System, Guid.Empty, "hello");

        Assert.Equal(1, store.MarkRead());
        Assert.Equal(0, store.MarkRead());
        Assert.Equal(0, store.UnreadCount);
    }

    [Fact]
    public void DismissingAnUnreadEntryTakesItsBadgeWithIt()
    {
        var store = new NotificationStore();
        var entry = store.Add(NotificationKind.Transaction, Alice, "paid you");

        Assert.Equal(1, store.UnreadCount);
        store.Dismiss(entry.Id);
        Assert.Equal(0, store.UnreadCount);
    }

    [Fact]
    public void AddedFiresOnlyForNewEntries()
    {
        // A toast must appear when something ARRIVES and must not reappear when something is
        // dismissed -- and dismissal changes the list too, so Changed alone cannot drive it.
        var store = new NotificationStore();
        int added = 0;
        store.Added += (_, _) => added++;

        var entry = store.Add(NotificationKind.Transaction, Alice, "paid you");
        Assert.Equal(1, added);

        store.Dismiss(entry.Id);
        store.MarkRead();
        Assert.Equal(1, added);
    }

    [Fact]
    public void AddingRaisesChanged()
    {
        var store = new NotificationStore();
        int raised = 0;
        store.Changed += (_, _) => raised++;

        store.Add(NotificationKind.System, Guid.Empty, "hello");

        Assert.Equal(1, raised);
    }

    // ---- BUG-UI-12: entries that still have a decision behind them ---------------------------

    [Fact]
    public void AnEntryCarriesItsActionKey()
    {
        var store = new NotificationStore();
        var key = Guid.NewGuid();

        var entry = store.Add(NotificationKind.Group, Alice, "invited you", actionKey: key);

        Assert.Equal(key, entry.ActionKey);
        Assert.Equal(key, store.Entries[0].ActionKey);
    }

    [Fact]
    public void AnEntryWithoutAnActionHasAnEmptyKey()
    {
        // The default, and the one that matters: most notifications are records, not questions,
        // and the window keys the "open" button off exactly this.
        var store = new NotificationStore();

        var entry = store.Add(NotificationKind.Transaction, Alice, "paid you");

        Assert.Equal(Guid.Empty, entry.ActionKey);
    }

    [Fact]
    public void CompletingAnActionClearsTheWayBackButKeepsTheEntry()
    {
        // The whole point: the record of "you were invited and joined" is worth keeping, but the
        // button must go -- the simulator will not take a second answer.
        var store = new NotificationStore();
        var key = Guid.NewGuid();
        store.Add(NotificationKind.Group, Alice, "invited you", actionKey: key);

        int changed = store.CompleteAction(key, "invited you -- joined.");

        Assert.Equal(1, changed);
        Assert.Single(store.Entries);
        Assert.Equal(Guid.Empty, store.Entries[0].ActionKey);
        Assert.Equal("invited you -- joined.", store.Entries[0].Text);
    }

    [Fact]
    public void CompletingWithoutNewTextLeavesTheTextAlone()
    {
        // Used at session teardown, where the answer was never given and there is nothing new to
        // say -- only the way back has to go.
        var store = new NotificationStore();
        var key = Guid.NewGuid();
        store.Add(NotificationKind.Group, Alice, "invited you", actionKey: key);

        store.CompleteAction(key);

        Assert.Equal("invited you", store.Entries[0].Text);
        Assert.Equal(Guid.Empty, store.Entries[0].ActionKey);
    }

    [Fact]
    public void CompletingAnEmptyKeyTouchesNothing()
    {
        // A caller that lost track of its key must not be able to strip the action off every
        // entry that never had one -- which is what matching Guid.Empty would do.
        var store = new NotificationStore();
        var live = Guid.NewGuid();
        store.Add(NotificationKind.Transaction, Alice, "paid you");
        store.Add(NotificationKind.Group, Alice, "invited you", actionKey: live);

        int changed = store.CompleteAction(Guid.Empty);

        Assert.Equal(0, changed);
        Assert.Equal(live, store.Entries[0].ActionKey);
    }

    [Fact]
    public void CompletingTwiceChangesNothingTheSecondTime()
    {
        var store = new NotificationStore();
        var key = Guid.NewGuid();
        store.Add(NotificationKind.Group, Alice, "invited you", actionKey: key);

        Assert.Equal(1, store.CompleteAction(key, "joined"));
        Assert.Equal(0, store.CompleteAction(key, "joined again"));
        Assert.Equal("joined", store.Entries[0].Text);
    }

    [Fact]
    public void CompletingAnActionRaisesChangedOnlyWhenSomethingChanged()
    {
        var store = new NotificationStore();
        var key = Guid.NewGuid();
        store.Add(NotificationKind.Group, Alice, "invited you", actionKey: key);

        int raised = 0;
        store.Changed += (_, _) => raised++;

        store.CompleteAction(key);
        Assert.Equal(1, raised);

        store.CompleteAction(key);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void CompletingAnActionDoesNotRaiseAdded()
    {
        // Otherwise answering an invitation would pop a toast announcing it all over again.
        var store = new NotificationStore();
        var key = Guid.NewGuid();
        store.Add(NotificationKind.Group, Alice, "invited you", actionKey: key);

        int added = 0;
        store.Added += (_, _) => added++;

        store.CompleteAction(key, "joined");

        Assert.Equal(0, added);
    }

    [Fact]
    public void CompletingAnActionDoesNotMarkTheEntryRead()
    {
        // Answering from the window is not the same as having seen the tab it sits in, and the
        // badge counts the latter.
        var store = new NotificationStore();
        var key = Guid.NewGuid();
        store.Add(NotificationKind.Group, Alice, "invited you", actionKey: key);

        store.CompleteAction(key, "joined");

        Assert.False(store.Entries[0].Read);
        Assert.Equal(1, store.UnreadOf(NotificationKind.Group));
    }
}
