using System.Collections.Generic;
using Godot;
using SLNG.Core;
using SLNG.Core.Services;

namespace SLNG.App;

/// <summary>
/// BUG-GRID-01: the app's single door to per-grid data on disk. Anything that belongs to ONE grid
/// -- a cache, a log, a picture of the last session -- gets its path from here and nowhere else, so
/// the same account name or the same region handle on two grids can never meet in one file.
///
/// <para>Two roots, one rule (<see cref="GridDataPaths"/>): <c>user://grids/&lt;grid&gt;/...</c> for
/// what the client keeps in its own data directory, and
/// <c>%APPDATA%\SLNG\logs\chat\&lt;grid&gt;\&lt;account&gt;\</c> for the chat logs of v0.26.13 (FEAT-UI-41 then moved
/// chat logs to Firestorm's layout, see <see cref="ChatLogPaths"/>; the root stays for the import). The grid is always the one the user logs into
/// (<see cref="SLNG.Net.LoginCredentials.GridLoginUri"/>, i.e. what is in the grid box), so a path is
/// chosen per login, never at startup.</para>
///
/// <para>Everything here except <see cref="LogFirstUse"/> only <b>builds</b> a path. Nothing creates, moves or deletes a file, which is
/// what keeps it safe to call on the login screen -- <c>--selftest</c> boots the real client against
/// the developer's real <c>user://</c>. The writer creates the directory when it first writes.</para>
///
/// <para>What is deliberately NOT per grid: UI preferences and window layouts (a property of the
/// screen, not of a grid), saved logins (already keyed by account and grid URI), and caches keyed by
/// an asset UUID -- see docs/specs/BUG-GRID-01-per-grid-data-separation.md for the reasoning and the
/// assumption behind the last one.</para>
/// </summary>
public static class GridData
{
    public const string ObjectCacheKind = "objects";
    public const string MapTileCacheKind = "maptiles";
    public const string InventoryCacheKind = "inventory";
    public const string DisplayNameCacheKind = "displaynames";
    private const string LoginBackgroundName = "last_session_bg";

    private static GridDataPaths? _user;
    private static GridDataPaths? _chat;

    /// <summary><c>user://grids</c> as a real directory. Resolved on first use, not at type load, so
    /// merely touching this class before the engine has its project settings does nothing.</summary>
    public static GridDataPaths User => _user ??= new GridDataPaths(ProjectSettings.GlobalizePath("user://grids"));

    /// <summary>The chat-log root (<see cref="ChatLogger.DefaultLogDirectory"/>), under which v0.26.13 kept
    /// per-grid folders. Kept only for <see cref="LegacyChatLogDirectory"/>.</summary>
    public static GridDataPaths Chat => _chat ??= new GridDataPaths(ChatLogger.DefaultLogDirectory());

    /// <summary>Where this grid's object cache (<c>.slobj</c>, one file per region) lives.</summary>
    public static string ObjectCacheDirectory(string? gridUri) => User.CacheDirectory(gridUri, ObjectCacheKind);

    /// <summary>Where this grid's world-map tiles are cached.</summary>
    public static string MapTileDirectory(string? gridUri) => User.CacheDirectory(gridUri, MapTileCacheKind);

    /// <summary>Where this grid's inventory cache (<c>.inv.cache</c>) lives.</summary>
    public static string InventoryCacheDirectory(string? gridUri) => User.CacheDirectory(gridUri, InventoryCacheKind);

    /// <summary>Where this grid's display name cache (<c>.names.json</c>) lives.</summary>
    public static string DisplayNameCacheDirectory(string? gridUri) => User.CacheDirectory(gridUri, DisplayNameCacheKind);

    /// <summary>The picture of the last session of this account on this grid -- the login and
    /// loading-screen background. A real file path (not <c>user://</c>), which Godot's image and
    /// file calls accept as they do a virtual one.</summary>
    public static string LoginBackgroundPath(string? gridUri, string firstName, string lastName)
        => User.AccountFile(gridUri, firstName, lastName, LoginBackgroundName, ".png");

    /// <summary>Where v0.26.13 kept this account's chat logs on this grid
    /// (<c>&lt;chat root&gt;\&lt;grid&gt;\&lt;account&gt;\</c>). Since FEAT-UI-41 logs live in Firestorm's
    /// layout instead (<see cref="ChatLogPaths"/>); this is only where the import looks for the old ones.</summary>
    public static string LegacyChatLogDirectory(string? gridUri, string firstName, string lastName)
        => Chat.AccountDirectory(gridUri, firstName, lastName);

    /// <summary>The object caches of every grid, plus the shared one older builds wrote -- for
    /// "Clear cache" and for showing its size, which are about the cache as a whole, not the grid
    /// you happen to be on.</summary>
    public static IReadOnlyList<string> AllObjectCacheDirectories()
    {
        var dirs = new List<string>(User.ExistingCacheDirectories(ObjectCacheKind))
        {
            ProjectSettings.GlobalizePath("user://cache/objects"),
        };
        return dirs;
    }

    /// <summary>Says once, the first time a grid is logged into, that its data now has a directory of
    /// its own and that what older builds kept in shared places is left where it is. The one thing it
    /// does is create the grid's (empty) directory, which is what makes "first" true only once; it
    /// runs on the login path, never on the boot path the smoke test exercises.</summary>
    public static void LogFirstUse(string? gridUri)
    {
        string dir = User.GridDirectory(gridUri);
        if (System.IO.Directory.Exists(dir)) return;

        try { System.IO.Directory.CreateDirectory(dir); }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException)
        {
            GD.PrintErr($"[GridData] could not create {dir}: {ex.Message}");
            return;
        }

        GD.Print($"[GridData] first login on '{GridIdentity.Slug(gridUri)}': its data is kept under {dir}. " +
                 "Caches and the last-session picture older builds shared between grids " +
                 "(user://cache/objects, user://last_session_bg_*.png, the chat logs in the log root) " +
                 "are left untouched and no longer used.");
    }
}
