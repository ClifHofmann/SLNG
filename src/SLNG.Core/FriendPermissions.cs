namespace SLNG.Core;

/// <summary>
/// What one friend lets the other do (FEAT-UI-35). Each side of a friendship holds its own set, in the
/// <see cref="FriendEntry"/> as <c>GrantedByMe</c> (what the friend may do with me) and <c>GrantedToMe</c> (what I may
/// do with them).
/// <para>The values are the grid's own bits (<c>GrantUserRights</c> / <c>ChangeUserRights</c>: 1 online status, 2 map,
/// 4 modify objects), so <c>SLNG.Net</c> converts with a cast; they are redeclared here because no LibreMetaverse type
/// may cross into <c>SLNG.Core</c>.</para>
/// </summary>
[Flags]
public enum FriendPermissions
{
    None = 0,

    /// <summary>See whether the other is online.</summary>
    SeeOnline = 1,

    /// <summary>See where the other is on the world map.</summary>
    SeeOnMap = 2,

    /// <summary>Edit, delete or take the other's objects.</summary>
    ModifyObjects = 4,
}

public static class FriendPermissionsExtensions
{
    /// <summary>The set with <paramref name="permission"/> switched on or off, the rest untouched. The grid takes a
    /// friend's whole set in one message, never a single bit, so every change is "the old set, one bit different".</summary>
    public static FriendPermissions With(this FriendPermissions set, FriendPermissions permission, bool on) =>
        on ? set | permission : set & ~permission;
}
