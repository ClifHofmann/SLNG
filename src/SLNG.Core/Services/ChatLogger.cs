using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SLNG.Core.Services;

/// <summary>Which conversation a log line belongs to — picks the on-disk file name per §5 of
/// docs/specs/M5-3-tabbed-chat-window.md (chat.txt / &lt;Avatar&gt;.txt / &lt;Group&gt;.txt).</summary>
public enum ChatLogKind
{
    Local,
    Im,
    Group,
}

/// <summary>
/// Async, append-only chat log writer + paginated reader, in original-viewer format
/// (<c>[YYYY/MM/DD HH:MM:SS] Sender: Message</c>). Engine- and protocol-agnostic per AGENTS.md's
/// layering rule — callers in <c>app/</c> pass plain strings, never Godot or LibreMetaverse types.
/// Backs both the live chat window (append) and the paginated History viewer (GetPage), which
/// reads straight from disk rather than the caller's in-memory buffer.
/// </summary>
public sealed class ChatLogger
{
    private readonly string _rootDir;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    /// <summary>Global on/off switch (Preferences "Enable Chat Logging", default on). When false,
    /// <see cref="AppendAsync"/> is a no-op — reads still work against whatever was logged before.</summary>
    public bool Enabled { get; set; } = true;

    public ChatLogger(string? rootDir = null)
    {
        _rootDir = rootDir ?? DefaultLogDirectory();
    }

    /// <summary>%APPDATA%\SLNG\logs\chat\ on Windows, ~/.config/slng/logs/chat/ on Linux/macOS —
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> already resolves to the right base
    /// on each platform (Windows roaming AppData vs. XDG ~/.config).</summary>
    public static string DefaultLogDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appDirName = OperatingSystem.IsWindows() ? "SLNG" : "slng";
        return Path.Combine(appData, appDirName, "logs", "chat");
    }

    public string FilePathFor(ChatLogKind kind, string conversationName) =>
        Path.Combine(_rootDir, FileNameFor(kind, conversationName));

    private static string FileNameFor(ChatLogKind kind, string conversationName) => kind switch
    {
        ChatLogKind.Local => "chat.txt",
        ChatLogKind.Im => SanitizeFileName(conversationName) + ".txt",
        ChatLogKind.Group => SanitizeFileName(conversationName) + ".txt",
        _ => "chat.txt",
    };

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    /// <summary>Appends one line, formatted as <c>[YYYY/MM/DD HH:MM:SS] Sender: Message</c>.
    /// Serialized per target file (via an internal lock) so a burst of messages can't interleave
    /// partial writes; safe to fire-and-forget from the caller.</summary>
    public async Task AppendAsync(ChatLogKind kind, string conversationName, string sender, string message, DateTime timestamp)
    {
        if (!Enabled) return;

        string path = FilePathFor(kind, conversationName);
        string line = $"[{timestamp:yyyy/MM/dd HH:mm:ss}] {sender}: {message}";
        var gate = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_rootDir);
            await File.AppendAllTextAsync(path, line + Environment.NewLine).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Reads one page of previously-logged lines for the History viewer, most recent
    /// page last (i.e. page 0 is the oldest). Independent of whatever's in the caller's live
    /// in-memory buffer — this is the on-disk record of record.</summary>
    public IReadOnlyList<string> GetPage(ChatLogKind kind, string conversationName, int pageIndex, int pageSize, out int totalPages)
    {
        string path = FilePathFor(kind, conversationName);
        if (!File.Exists(path))
        {
            totalPages = 1;
            return Array.Empty<string>();
        }

        string[] allLines = File.ReadAllLines(path);
        totalPages = Math.Max(1, (int)Math.Ceiling(allLines.Length / (double)pageSize));
        pageIndex = Math.Clamp(pageIndex, 0, totalPages - 1);
        return allLines.Skip(pageIndex * pageSize).Take(pageSize).ToList();
    }
}
