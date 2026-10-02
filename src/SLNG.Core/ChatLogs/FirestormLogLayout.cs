using System.Globalization;
using System.Text;
using SLNG.Core.Services;

namespace SLNG.Core.ChatLogs;

/// <summary>
/// FEAT-UI-41: the folder and file naming rules of Firestorm's chat logs, as pure functions. Every
/// rule cites the source it was read from (<c>indra/llfilesystem/lldir.cpp</c>,
/// <c>indra/newview/lllogchat.cpp</c>, <c>llimview.cpp</c>, <c>llstartup.cpp</c> in
/// <c>FirestormViewer/phoenix-firestorm</c>) and was checked against real files. The full table is
/// in docs/specs/FEAT-UI-41-firestorm-compatible-chat-logs.md.
/// </summary>
public static class FirestormLogLayout
{
    public const string GroupSuffix = " (group)";          // lllogchat.cpp GROUP_CHAT_SUFFIX
    public const string NearbyStem = "chat";               // makeLogFileName("chat")
    public const string Extension = ".txt";                // LL_TRANSCRIPT_FILE_EXTENSION

    /// <summary>The grid label whose account folders carry no suffix (<c>lldir.cpp</c>
    /// <c>setPerAccountChatLogsDir</c>: <c>gridlower != "second_life"</c>).</summary>
    public const string SecondLifeLabel = "Second Life";

    /// <summary>The label of the Linden beta grid, as Firestorm's own grid list names it
    /// (<c>grids.remote.xml</c>: <c>gridname</c> "Second Life Beta") -- so its folder is
    /// <c>first_last.second_life_beta</c>.</summary>
    public const string SecondLifeBetaLabel = "Second Life Beta";

    private static readonly string[] DeviceNames =
    {
        "con", "prn", "aux", "nul",
        "com0", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt0", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    // Characters Windows refuses in a file or folder name, used only where the original rule would
    // otherwise produce a name the file system rejects.
    private static readonly char[] WindowsInvalid = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>
    /// The per-account folder: <c>first_last</c> lower-cased with spaces as <c>_</c>, plus
    /// <c>.&lt;label&gt;</c> (label lower-cased, spaces as <c>_</c>) unless the label is empty or
    /// "Second Life". Real examples: <c>clifton_howlett</c> (Second Life),
    /// <c>clifton_howlett.osgrid</c>, <c>cilian_dupont.alife_virtual</c> (label "Alife Virtual"),
    /// <c>test_user.localhost</c>, <c>denise1976_resident</c> (a single-name account is
    /// <c>name Resident</c>).
    ///
    /// <para>Source: <c>llstartup.cpp</c> builds <c>userid = first_last</c> lower-cased
    /// (<c>LLSecAPIBasicCredential::userID</c>) and calls
    /// <c>setPerAccountChatLogsDir(userid, LLGridManager::getGridLabel())</c>; <c>lldir.cpp</c> adds
    /// the suffix. The suffix is the grid's LABEL (its <c>gridname</c>), not its nick:
    /// "Alife Virtual" has nick "AV" and the real folder is <c>.alife_virtual</c>.</para>
    ///
    /// <para>One deliberate deviation: characters Windows cannot have in a folder name become
    /// <c>_</c> (the viewer would fail to create the folder). It matters only for a label that fell
    /// back to a <c>host:port</c> text.</para>
    /// </summary>
    public static string AccountFolderName(string? firstName, string? lastName, string? gridLabel)
    {
        string first = (firstName ?? "").Trim();
        string last = (lastName ?? "").Trim();
        if (last.Length == 0) last = "Resident"; // a single-name account is "<name> Resident"

        string folder = Lower(first + "_" + last);

        string label = Lower(gridLabel ?? "");
        if (label.Length > 0 && label != "second_life") folder += "." + label;

        return ReplaceChars(folder, WindowsInvalid);
    }

    /// <summary>
    /// <c>LLLogChat::cleanFileName</c>: every one of <c>"'\/?*:.&lt;&gt;|[]{}~</c> becomes <c>_</c>.
    /// Note the dot -- that is why <c>pink.ice</c> is the file <c>pink_ice.txt</c>. Extra, for names
    /// Windows would refuse where the viewer simply fails: control characters and a trailing space
    /// become <c>_</c>, and a device name (<c>con</c>, <c>nul</c>, ...) is prefixed with <c>_</c>.
    /// </summary>
    public static string CleanFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if ("\"'\\/?*:.<>|[]{}~".IndexOf(c) >= 0 || char.IsControl(c)) sb.Append('_');
            else sb.Append(c);
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb[^1] = '_';

        string cleaned = sb.ToString();
        return Array.IndexOf(DeviceNames, cleaned.ToLowerInvariant()) >= 0 ? "_" + cleaned : cleaned;
    }

    /// <summary>The name a P2P IM history is filed under, from the other person's LEGACY name
    /// ("First Last", "First Resident" for a single-name account), before the date suffix and
    /// <see cref="CleanFileName"/>.
    /// <list type="bullet">
    /// <item><see cref="ImLogNameStyle.Legacy"/>: <c>name.substr(0, name.find(" Resident"))</c>
    /// (<c>llimview.cpp</c> under <c>UseLegacyIMLogNames</c>) -- "Pink Ice" stays "Pink Ice".</item>
    /// <item><see cref="ImLogNameStyle.Account"/>: <c>LLCacheName::buildUsername</c> --
    /// "Pink Ice" becomes "pink.ice", "Clifton Resident" becomes "clifton", lower-cased.</item>
    /// </list></summary>
    public static string ImStem(string legacyName, ImLogNameStyle style)
    {
        if (style == ImLogNameStyle.Legacy)
        {
            int resident = legacyName.IndexOf(" Resident", StringComparison.Ordinal);
            return resident >= 0 ? legacyName[..resident] : legacyName;
        }

        int space = legacyName.IndexOf(' ');
        if (space >= 0)
        {
            string username = legacyName[..space];
            string lastname = legacyName[(space + 1)..];
            if (lastname != "Resident") username += "." + lastname;
            return Lower(username);
        }

        int res = legacyName.IndexOf(" Resident", StringComparison.Ordinal);
        return Lower(res >= 0 ? legacyName[..res] : legacyName);
    }

    /// <summary>
    /// The file name (with <c>.txt</c>) of one conversation, or null when the viewer would refuse to
    /// write one (an empty name -- <c>saveHistory</c> warns and returns).
    /// <list type="bullet">
    /// <item>Nearby chat: <c>chat.txt</c>.</item>
    /// <item>Group chat: <c>&lt;group name&gt; (group).txt</c>.</item>
    /// <item>IM: <see cref="ImStem"/>.</item>
    /// </list>
    /// With <see cref="ChatLogNaming.DateSuffix"/> the stem gets <c>-YYYY-MM</c> (<c>-YYYY-MM-DD</c>
    /// for nearby chat) in LOCAL time (<c>strftime</c> on <c>localtime</c>) before cleaning --
    /// <c>makeLogFileName</c>.
    /// </summary>
    public static string? FileName(ChatLogKind kind, string conversationName, ChatLogNaming naming, DateTime localTime)
    {
        string stem = kind switch
        {
            ChatLogKind.Local => NearbyStem,
            ChatLogKind.Group => conversationName + GroupSuffix,
            _ => ImStem(conversationName, naming.ImStyle),
        };

        if (kind != ChatLogKind.Local && conversationName.Trim().Length == 0) return null;

        if (naming.DateSuffix)
        {
            stem += kind == ChatLogKind.Local
                ? localTime.ToString("-yyyy-MM-dd", CultureInfo.InvariantCulture)
                : localTime.ToString("-yyyy-MM", CultureInfo.InvariantCulture);
        }

        return CleanFileName(stem) + Extension;
    }

    /// <summary>
    /// Which existing file holds a conversation's history. The viewer tries the exact name first
    /// (<c>loadChatHistory</c>); when it is missing it falls back to <c>oldLogFileName</c>: the
    /// newest dated file (<c>name-????-??.txt</c>, <c>chat-????-??-??.txt</c>), else the plain name.
    /// This returns the existing one, or null when there is none.
    /// </summary>
    public static string? ResolveExisting(string directory, ChatLogKind kind, string conversationName,
        ChatLogNaming naming, DateTime localTime)
    {
        string? exact = FileName(kind, conversationName, naming, localTime);
        if (exact is null || !Directory.Exists(directory)) return null;

        string exactPath = Path.Combine(directory, exact);
        if (File.Exists(exactPath)) return exactPath;

        string plain = FileName(kind, conversationName, naming with { DateSuffix = false }, localTime)!;
        string stem = plain[..^Extension.Length];
        string pattern = kind == ChatLogKind.Local ? stem + "-????-??-??.txt" : stem + "-????-??.txt";

        try
        {
            string? newest = Directory.EnumerateFiles(directory, pattern)
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
                .LastOrDefault();
            if (newest is not null) return newest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder has no history to show.
        }

        string plainPath = Path.Combine(directory, plain);
        return File.Exists(plainPath) ? plainPath : null;
    }

    private static string Lower(string s) => s.ToLowerInvariant().Replace(' ', '_');

    private static string ReplaceChars(string s, char[] chars)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(Array.IndexOf(chars, c) >= 0 || char.IsControl(c) ? '_' : c);
        return sb.ToString();
    }
}
