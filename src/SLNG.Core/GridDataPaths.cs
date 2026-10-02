namespace SLNG.Core;

/// <summary>
/// BUG-GRID-01: the one place a per-grid path is built. A root directory the client owns (the app
/// supplies it -- <c>src/</c> does not know Godot's <c>user://</c>) plus a login URI gives
/// <c>&lt;root&gt;/&lt;grid slug&gt;/...</c>; nothing that belongs to one grid is written anywhere else.
///
/// <para>Layout under <c>&lt;root&gt;/&lt;slug&gt;/</c>:</para>
/// <list type="bullet">
/// <item><c>cache/&lt;kind&gt;/</c> -- regenerable per-grid caches (<see cref="CacheDirectory"/>);</item>
/// <item><c>&lt;baseName&gt;_&lt;account&gt;.&lt;ext&gt;</c> -- one file per account
/// (<see cref="AccountFile"/>);</item>
/// <item><c>&lt;account&gt;/</c> -- a directory per account (<see cref="AccountDirectory"/>); an account
/// slug always contains an underscore, so it can never be <c>cache</c>.</item>
/// </list>
///
/// <para>Paths are joined with <c>/</c>, which Windows accepts and which also keeps a
/// <c>user://</c> style root intact. Building a path touches nothing on disk, so it is safe on a
/// boot path; the writer creates the directory when it first writes.</para>
/// </summary>
public sealed class GridDataPaths
{
    private readonly string _root;

    /// <param name="rootDirectory">The directory every grid's folder lives in, with or without a
    /// trailing separator.</param>
    public GridDataPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = rootDirectory.TrimEnd('/', '\\');
        if (_root.Length == 0) _root = rootDirectory; // a filesystem root: keep it
    }

    public string Root => _root;

    /// <summary><c>&lt;root&gt;/&lt;slug&gt;</c>.</summary>
    public string GridDirectory(string? loginUri) => _root + "/" + GridIdentity.Slug(loginUri);

    /// <summary><c>&lt;root&gt;/&lt;slug&gt;/cache/&lt;kind&gt;</c>. <paramref name="kind"/> is a fixed
    /// name chosen by the caller (<c>objects</c>, <c>maptiles</c>), never user input.</summary>
    public string CacheDirectory(string? loginUri, string kind)
        => GridDirectory(loginUri) + "/cache/" + RequireKind(kind);

    /// <summary><c>&lt;root&gt;/&lt;slug&gt;/&lt;account&gt;</c>.</summary>
    public string AccountDirectory(string? loginUri, string? firstName, string? lastName)
        => GridDirectory(loginUri) + "/" + GridIdentity.AccountSlug(firstName, lastName);

    /// <summary><c>&lt;root&gt;/&lt;slug&gt;/&lt;baseName&gt;_&lt;account&gt;&lt;extension&gt;</c>, e.g.
    /// <c>.../agni/last_session_bg_clifton_howlett.png</c>.</summary>
    public string AccountFile(string? loginUri, string? firstName, string? lastName, string baseName, string extension)
        => GridDirectory(loginUri) + "/" + RequireKind(baseName, allowUnderscore: true)
           + "_" + GridIdentity.AccountSlug(firstName, lastName) + extension;

    /// <summary>Every <c>cache/&lt;kind&gt;</c> directory that exists, one per grid that has ever
    /// stored one. For "clear the cache", which is not about the grid you happen to be on.</summary>
    public IReadOnlyList<string> ExistingCacheDirectories(string kind)
    {
        RequireKind(kind);
        var found = new List<string>();
        try
        {
            if (!Directory.Exists(_root)) return found;
            foreach (string gridDir in Directory.EnumerateDirectories(_root))
            {
                string candidate = Path.Combine(gridDir, "cache", kind);
                if (Directory.Exists(candidate)) found.Add(candidate);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable root is an empty list: the caller only wants to size or clear caches.
        }
        return found;
    }

    private static string RequireKind(string name, bool allowUnderscore = false)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("A name is required.", nameof(name));
        foreach (char c in name)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' || (allowUnderscore && c == '_');
            if (!ok) throw new ArgumentException($"'{name}' is not a plain lower-case name.", nameof(name));
        }
        return name;
    }
}
