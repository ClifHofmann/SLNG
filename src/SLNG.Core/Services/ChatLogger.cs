using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SLNG.Core.ChatLogs;

namespace SLNG.Core.Services;

/// <summary>Which conversation a log line belongs to -- picks the on-disk file name (see
/// <see cref="FirestormLogLayout.FileName"/>): <c>chat.txt</c> / <c>&lt;partner&gt;.txt</c> /
/// <c>&lt;group&gt; (group).txt</c>.</summary>
public enum ChatLogKind
{
    Local,
    Im,
    Group,
}

/// <summary>
/// Async, append-only chat log writer + paginated reader in FIRESTORM's format and layout
/// (FEAT-UI-41), so the two viewers can share one history: same folder, same file names, same line
/// format (<c>[2026/04/11 08:07]  Name: text</c>, Second Life time), same encoding (UTF-8, no BOM,
/// the platform's newline). Engine- and protocol-agnostic per AGENTS.md -- callers in <c>app/</c>
/// pass plain strings.
///
/// <para><b>Append only.</b> A file is opened, one record is appended, the file is closed; nothing
/// that is already there is ever rewritten. The one exception to "just append" is a safety: if a
/// file does not end in a newline (cut off by a crash), a newline is written first so two records
/// never share a line.</para>
///
/// <para><b>Reading goes through one parser</b> (<see cref="FirestormLogFormat.Parse"/>), for the
/// History viewer (<see cref="GetPage"/>) and the preload of an open conversation
/// (<see cref="GetTail"/>), and reads Firestorm's lines and SLNG's own older ones alike.</para>
///
/// <para>Running both viewers against one folder AT THE SAME TIME is not supported: each appends
/// without knowing about the other, and the viewer keeps part of its state (<c>conversation.log</c>)
/// in memory and rewrites it on exit.</para>
/// </summary>
public sealed class ChatLogger
{
    // Where the logs go RIGHT NOW. Changes at every login (BUG-GRID-01): a log belongs to one
    // account on one grid, and the account is only known once the user logs in. Null = no account
    // yet, so nothing is written and nothing is read. Written on the main thread, read from the
    // fire-and-forget appends, hence volatile.
    private volatile string? _rootDir;
    private volatile ChatLogNaming _naming = ChatLogNaming.Default;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    // The History viewer asks twice (how many pages, then one page); the parse of a big file is the
    // cost, so the last one is kept until the file changes.
    private readonly object _cacheLock = new();
    private (string Path, long Length, long WriteTicks, IReadOnlyList<ChatLogEntry> Entries)? _parsed;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The preload window the viewer reads from the end of a file: <c>LOG_RECALL_SIZE</c>
    /// is 20480 bytes. SLNG reads a little more per requested line, so a long message does not
    /// leave the tail short.</summary>
    private const int TailBytesPerEntry = 1024;
    private const int MinTailBytes = 20480;
    private const int MaxTailBytes = 1 << 20;

    /// <summary>Global on/off switch (Preferences "Enable Chat Logging", default on). When false,
    /// <see cref="AppendAsync"/> is a no-op -- reads still work against whatever was logged before.</summary>
    public bool Enabled { get; set; } = true;

    /// <param name="rootDir">The directory to log into, or null to start with none: the app attaches
    /// the account's own directory at login with <see cref="UseDirectory"/>.</param>
    public ChatLogger(string? rootDir = null, ChatLogNaming? naming = null)
    {
        _rootDir = rootDir;
        if (naming is not null) _naming = naming;
    }

    /// <summary>The directory logs are written to and read from, or null before a login attached one.</summary>
    public string? RootDirectory => _rootDir;

    /// <summary>How files are named and stamped for the current directory.</summary>
    public ChatLogNaming Naming => _naming;

    /// <summary>Points the logger at another directory -- the one for the account that just logged
    /// in -- or at none (null). Appends already in flight finish where they started; everything
    /// after this goes to, and reads come from, the new directory. <paramref name="naming"/>, when
    /// given, replaces the current naming (a null directory keeps it).</summary>
    public void UseDirectory(string? directory, ChatLogNaming? naming = null)
    {
        if (naming is not null) _naming = naming;
        _rootDir = string.IsNullOrWhiteSpace(directory) ? null : directory;
        lock (_cacheLock) _parsed = null; // a big file's parse is tens of megabytes; do not keep another account's
    }

    /// <summary>%APPDATA%\SLNG\logs\chat\ on Windows, ~/.config/slng/logs/chat/ on Linux/macOS --
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> already resolves to the right base
    /// on each platform (Windows roaming AppData vs. XDG ~/.config). The viewer-neutral folder
    /// -- the default base folder of an account that has none chosen; the account folders sit directly inside it. Logs written by
    /// builds before FEAT-UI-41 sit in it too, as loose files and (BUG-GRID-01) per grid.</summary>
    public static string DefaultLogDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appDirName = OperatingSystem.IsWindows() ? "SLNG" : "slng";
        return Path.Combine(appData, appDirName, "logs", "chat");
    }

    /// <summary>The file a message of this conversation would be appended to now, or null before a
    /// login attached a directory (or for an empty conversation name).</summary>
    public string? FilePathFor(ChatLogKind kind, string conversationName)
    {
        if (_rootDir is not { } root) return null;
        string? file = FirestormLogLayout.FileName(kind, conversationName, _naming, DateTime.Now);
        return file is null ? null : Path.Combine(root, file);
    }

    /// <summary>Appends one record in the viewer's format. Serialized per target file (via an
    /// internal lock) so a burst of messages can't interleave partial writes; safe to
    /// fire-and-forget from the caller.</summary>
    /// <param name="sender">Who said it; empty or whitespace is a system line and is written under
    /// <see cref="ChatLogNaming.SystemName"/>.</param>
    /// <param name="timestamp">When. A Utc time is taken as UTC, anything else as the machine's
    /// local time; the stamp written is Second Life time either way.</param>
    public async Task AppendAsync(ChatLogKind kind, string conversationName, string sender, string message, DateTime timestamp)
    {
        if (!Enabled) return;

        // Taken once: the directory can change between here and the write.
        string? root = _rootDir;
        ChatLogNaming naming = _naming;
        if (root is null) return;

        string? file = FirestormLogLayout.FileName(kind, conversationName, naming, timestamp.ToLocalTime());
        if (file is null) return;

        string from = string.IsNullOrWhiteSpace(sender) ? naming.SystemName : sender;
        string record = FirestormLogFormat.FormatRecord(SecondLifeTime.FromAny(timestamp), from, message);

        await AppendTextAsync(root, Path.Combine(root, file), FirestormLogFormat.ToFileText(record)).ConfigureAwait(false);
    }

    /// <summary>Appends text that already is whole records with their line endings -- what an
    /// import (<see cref="ChatLogImporter"/>) plans. Goes through the same per-file lock as
    /// <see cref="AppendAsync"/>, so an import and live chat never interleave.</summary>
    public async Task AppendBlockAsync(string directory, string path, string block)
        => await AppendTextAsync(directory, path, block).ConfigureAwait(false);

    private async Task AppendTextAsync(string directory, string path, string text)
    {
        var gate = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, useAsync: true);

            byte[] body = Utf8NoBom.GetBytes(text);
            byte[] prefix = Array.Empty<byte>();
            if (fs.Length > 0)
            {
                fs.Seek(-1, SeekOrigin.End);
                int last = fs.ReadByte();
                if (last != '\n') prefix = Utf8NoBom.GetBytes(Environment.NewLine);
            }

            fs.Seek(0, SeekOrigin.End);
            // One write, so a record is never half there.
            var all = new byte[prefix.Length + body.Length];
            Buffer.BlockCopy(prefix, 0, all, 0, prefix.Length);
            Buffer.BlockCopy(body, 0, all, prefix.Length, body.Length);
            await fs.WriteAsync(all).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Reads one page of previously-logged messages for the History viewer, most recent
    /// page last (i.e. page 0 is the oldest). A message is one entry however many lines it spans.
    /// Independent of whatever's in the caller's live in-memory buffer -- this is the on-disk
    /// record of record, and it reads Firestorm's files too.</summary>
    public IReadOnlyList<string> GetPage(ChatLogKind kind, string conversationName, int pageIndex, int pageSize, out int totalPages)
    {
        string? path = ExistingPathFor(kind, conversationName);
        if (path is null)
        {
            totalPages = 1;
            return Array.Empty<string>();
        }

        IReadOnlyList<ChatLogEntry> entries = ReadAll(path);
        totalPages = Math.Max(1, (int)Math.Ceiling(entries.Count / (double)pageSize));
        pageIndex = Math.Clamp(pageIndex, 0, totalPages - 1);
        return entries.Skip(pageIndex * pageSize).Take(pageSize).Select(e => e.Display()).ToList();
    }

    /// <summary>The last <paramref name="count"/> messages of a conversation, for the preload of an
    /// open tab. Reads only the end of the file -- real logs are tens of megabytes -- the way the
    /// viewer does (it reads the last 20 KB and drops the first, partial, line).</summary>
    public IReadOnlyList<string> GetTail(ChatLogKind kind, string conversationName, int count)
    {
        string? path = ExistingPathFor(kind, conversationName);
        if (path is null || count <= 0) return Array.Empty<string>();

        IReadOnlyList<ChatLogEntry> entries = ReadTail(path, count);
        return entries.Skip(Math.Max(0, entries.Count - count)).Select(e => e.Display()).ToList();
    }

    /// <summary>The existing log file of a conversation, or null.</summary>
    public string? ExistingPathFor(ChatLogKind kind, string conversationName)
        => _rootDir is { } root
            ? FirestormLogLayout.ResolveExisting(root, kind, conversationName, _naming, DateTime.Now)
            : null;

    private IReadOnlyList<ChatLogEntry> ReadAll(string path)
    {
        try
        {
            var info = new FileInfo(path);
            lock (_cacheLock)
            {
                if (_parsed is { } p && p.Path == path && p.Length == info.Length && p.WriteTicks == info.LastWriteTimeUtc.Ticks)
                    return p.Entries;
            }

            IReadOnlyList<ChatLogEntry> entries = FirestormLogFormat.Parse(ReadLines(path));
            lock (_cacheLock) _parsed = (path, info.Length, info.LastWriteTimeUtc.Ticks, entries);
            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<ChatLogEntry>();
        }
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        // Shared: the other viewer or a pending append may hold the file.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) is not null) yield return line;
    }

    private static IReadOnlyList<ChatLogEntry> ReadTail(string path, int count)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            int window = (int)Math.Clamp((long)count * TailBytesPerEntry, MinTailBytes, MaxTailBytes);
            bool partial = length > window;
            long start = partial ? length - window : 0;

            var buffer = new byte[length - start];
            fs.Seek(start, SeekOrigin.Begin);
            int read = 0;
            while (read < buffer.Length)
            {
                int n = fs.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }

            int from = 0;
            if (partial)
            {
                // The window starts mid-line (maybe mid-character): drop through the first newline.
                int nl = Array.IndexOf(buffer, (byte)'\n', 0, read);
                from = nl < 0 ? read : nl + 1;
            }

            string text = Utf8NoBom.GetString(buffer, from, read - from);
            return FirestormLogFormat.Parse(text.Split('\n'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<ChatLogEntry>();
        }
    }
}
