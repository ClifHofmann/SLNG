namespace SLNG.Core;

/// <summary>
/// Instant-message session ids. A 1:1 conversation has no session of its own on the wire: its id is the two
/// agents' ids XORed (the reference viewer's <c>LLIMMgr::computeSessionID</c>, llimview.cpp), so both sides
/// compute the same one. Everything else -- a group, an ad-hoc conference -- has an id of its own.
/// </summary>
public static class SessionIds
{
    /// <summary>The session id of the 1:1 conversation between two agents.</summary>
    public static Guid PeerToPeer(Guid a, Guid b)
    {
        byte[] x = a.ToByteArray();
        byte[] y = b.ToByteArray();
        for (int i = 0; i < x.Length; i++) x[i] ^= y[i];
        return new Guid(x);
    }

    /// <summary>True when <paramref name="session"/> is the 1:1 conversation between <paramref name="self"/> and
    /// <paramref name="other"/> -- what tells a person typing to us from somebody typing in a conference.</summary>
    public static bool IsPeerToPeer(Guid session, Guid self, Guid other) => session == PeerToPeer(self, other);
}
