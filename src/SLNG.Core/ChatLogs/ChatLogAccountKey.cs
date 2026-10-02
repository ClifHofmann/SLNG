namespace SLNG.Core.ChatLogs;

/// <summary>
/// FEAT-UI-41: the key under which one saved account's chat-log folder is stored. A function of the
/// grid and the account name only -- the same two things that make up a login profile
/// (<c>First Last @ &lt;login URI&gt;</c> in logins.cfg) -- built from the BUG-GRID-01 identities, so
/// <c>http://hg.osgrid.org/</c> and <c>HG.OSGrid.org:80</c> are one account and the same name on
/// Second Life and on OSGrid are two. Always a short, plain string (<c>[a-z0-9.~_-]</c>), safe as a
/// config-file key; a profile that was never saved (a one-off login) has a key too.
/// </summary>
public static class ChatLogAccountKey
{
    public static string Of(string? loginUri, string? firstName, string? lastName)
        => GridIdentity.Slug(loginUri) + "__" + GridIdentity.AccountSlug(firstName, lastName);
}
