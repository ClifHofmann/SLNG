using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public sealed class SessionIdsTests
{
    [Fact]
    public void Both_sides_of_a_conversation_compute_the_same_session_id()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Equal(SessionIds.PeerToPeer(a, b), SessionIds.PeerToPeer(b, a));
    }

    [Fact]
    public void A_one_to_one_session_is_recognised_and_another_is_not()
    {
        var self = Guid.NewGuid();
        var other = Guid.NewGuid();
        Assert.True(SessionIds.IsPeerToPeer(SessionIds.PeerToPeer(self, other), self, other));
        Assert.False(SessionIds.IsPeerToPeer(Guid.NewGuid(), self, other));
        Assert.False(SessionIds.IsPeerToPeer(SessionIds.PeerToPeer(self, Guid.NewGuid()), self, other));
    }

    [Fact]
    public void The_session_id_is_the_bytewise_xor_of_the_two_ids()
    {
        var a = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var b = Guid.Parse("ffffffff-0000-ffff-0000-ffffffffffff");
        // Bytewise whatever the byte order Guid uses inside: XOR with the same bytes twice is the identity.
        Assert.Equal(a, SessionIds.PeerToPeer(SessionIds.PeerToPeer(a, b), b));
    }
}
