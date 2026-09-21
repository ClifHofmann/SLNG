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
}
