using LibreMetaverse;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>A resident changing their Display Name: the nametag must follow, and the user is told
/// the way the reference viewer tells them ("[OLD] ([SLID]) is now known as [NEW].").</summary>
public class DisplayNameUpdateTests
{
    private static DisplayNameUpdateEventArgs Update(Guid id, string? oldName, string? newName, string? userName) =>
        new(oldName!, new AgentDisplayName { ID = new UUID(id), DisplayName = newName, UserName = userName });

    [Fact]
    public void ChangedName_RepaintsTheNametag_AndTellsTheUser()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();
        NameResolvedEvent? resolved = null;
        DisplayNameChangedEvent? changed = null;
        session.DisplayNameResolved += (_, e) => resolved = e;
        session.DisplayNameChanged += (_, e) => changed = e;

        session.OnDisplayNameUpdate(null, Update(id, "Sid", "CrazyShiva", "sidney.trezuguet"));

        Assert.Equal(new NameResolvedEvent(id, "CrazyShiva"), resolved);
        Assert.Equal(new DisplayNameChangedEvent(id, "Sid", "CrazyShiva", "sidney.trezuguet"), changed);
    }

    [Fact]
    public void MissingOldName_FallsBackToTheUsername()
    {
        // The username is what does not change, so it is the honest thing to call them before.
        using var session = new GridSession();
        DisplayNameChangedEvent? changed = null;
        session.DisplayNameChanged += (_, e) => changed = e;

        session.OnDisplayNameUpdate(null, Update(Guid.NewGuid(), "", "New Name", "sidney.trezuguet"));

        Assert.Equal("sidney.trezuguet", changed!.OldName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyNewName_IsIgnored(string? newName)
    {
        // Nothing to show and nothing to announce -- keep whatever the nametag has.
        using var session = new GridSession();
        bool anything = false;
        session.DisplayNameResolved += (_, _) => anything = true;
        session.DisplayNameChanged += (_, _) => anything = true;

        session.OnDisplayNameUpdate(null, Update(Guid.NewGuid(), "Sid", newName, "sidney.trezuguet"));

        Assert.False(anything);
    }
}
