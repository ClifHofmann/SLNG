namespace SLNG.Core;

/// <summary>SL's per-face "Media Controls" style (LLMediaEntry::CONTROLS enum, llmediaentry.h:39-42).
/// Standard shows the full media control bar; Mini shows a compact one.</summary>
public enum MediaControlStyle : byte
{
    Standard = 0,
    Mini = 1,
}

/// <summary>Who may interact with / control a MOAP face (LLMediaEntry's PERM_* bits,
/// llmediaentry.h:176-181). A real bitmask on the wire, but <see cref="MediaPermissionEvaluator"/>
/// evaluates it as a short-circuiting chain, not a plain OR -- see that type's doc comment.</summary>
[Flags]
public enum MediaPermission : byte
{
    None = 0,
    Owner = 1,
    Group = 2,
    Anyone = 4,
    All = Owner | Group | Anyone,
}

/// <summary>Evaluates <see cref="MediaPermission"/> the way the reference viewer does
/// (LLVOVolume::hasMediaPermission, llvovolume.cpp:2782-2822) -- an if/else-if CHAIN over the
/// bits, not a bitwise OR of independent checks:
/// <code>
/// if      (perms &amp; ANYONE) return true;
/// else if (perms &amp; GROUP)  return agent-in-group;
/// else if (perms &amp; OWNER)  return agent-is-owner;
/// else return false;
/// </code>
/// This has a real, deliberately-preserved quirk: with <c>perms = Owner|Group</c>, an agent who
/// OWNS the object but is NOT in its group is DENIED, because the Group branch is taken (its bit
/// is set) and short-circuits before the Owner branch is ever reached. Do not "fix" this into a
/// clean OR -- that would diverge from what the real viewer (and, by the same chain, OpenSim's
/// MoapModule) actually grants.</summary>
public static class MediaPermissionEvaluator
{
    public static bool HasPermission(MediaPermission perms, bool isOwner, bool isInObjectGroup)
    {
        if ((perms & MediaPermission.Anyone) != 0) return true;
        if ((perms & MediaPermission.Group) != 0) return isInObjectGroup;
        if ((perms & MediaPermission.Owner) != 0) return isOwner;
        return false;
    }
}

/// <summary>One prim face's MOAP media (LLMediaEntry / LibreMetaverse's <c>MediaEntry</c>),
/// converted at the <c>SLNG.Net</c> boundary -- no LibreMetaverse type reaches here. Field set and
/// wire key names verified against <c>llmediaentry.cpp:35-75</c> and the ACTUAL pinned
/// LibreMetaverse 3.1.3 assembly (reflection), not a newer vendored checkout.
///
/// Defaults mirror the real viewer's <c>LLMediaEntry()</c> constructor
/// (<c>llmediaentry.cpp:96-97</c>), which defaults BOTH permission fields to <see
/// cref="MediaPermission.All"/> -- LibreMetaverse's own default-constructed <c>MediaEntry</c>
/// defaults them to <see cref="MediaPermission.None"/> instead, which would lock every agent
/// (including the owner) out of a face if ever sent verbatim in a future authoring/Update path.
/// This type is populated only from a real GET response in Phase 1; the trap is documented here
/// for whenever an Update path is added.</summary>
public readonly record struct MediaFace(
    string HomeUrl = "",
    string CurrentUrl = "",
    bool AutoPlay = false,
    bool AutoLoop = false,
    bool AutoScale = false,
    bool AutoZoom = false,
    bool InteractOnFirstClick = false,
    MediaControlStyle Controls = MediaControlStyle.Standard,
    int WidthPixels = 0,
    int HeightPixels = 0,
    MediaPermission ControlPermissions = MediaPermission.All,
    MediaPermission InteractPermissions = MediaPermission.All,
    bool EnableWhiteList = false,
    string[]? WhiteList = null,
    bool EnableAlternativeImage = false);

/// <summary>SL's MOAP "media version" doorbell string carried in <c>ObjectUpdate</c>'s
/// <c>MediaURL</c> field: <c>x-mv:&lt;10-digit sequence&gt;/&lt;agent-uuid&gt;</c>
/// (lltextureentry.cpp:53, :745-797). It is only ever a change NOTIFICATION -- the actual
/// <see cref="MediaFace"/> data never rides the wire and must be fetched separately via the
/// <c>ObjectMedia</c> capability (see <c>GridSession.RequestObjectMediaAsync</c>).</summary>
public static class MediaVersionString
{
    private const string Prefix = "x-mv:";

    public static bool IsMediaVersion(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Splits a doorbell string into its sequence number and the agent id that caused
    /// the change (the latter is how the real viewer suppresses echo of its own edits,
    /// llvovolume.cpp:2706-2707 -- SLNG does not author media yet, so it is exposed but unused).</summary>
    public static bool TryParse(string? value, out int sequence, out Guid agentId)
    {
        sequence = 0;
        agentId = Guid.Empty;
        if (!IsMediaVersion(value)) return false;

        var rest = value!.AsSpan(Prefix.Length);
        int slash = rest.IndexOf('/');
        if (slash < 0) return false;

        return int.TryParse(rest[..slash], out sequence) && Guid.TryParse(rest[(slash + 1)..], out agentId);
    }
}
