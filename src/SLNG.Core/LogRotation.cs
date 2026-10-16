using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SLNG.Core;

/// <summary>Start-of-session rotation for a log the client writes itself (the perf sidecar
/// <c>slng-perf.log</c>), the way Godot rotates <c>godot.log</c>: the previous session's file is
/// renamed to <c>&lt;base&gt;&lt;yyyy-MM-ddTHH.mm.ss&gt;&lt;ext&gt;</c> and the new session starts
/// a fresh file. The file used to be truncated at every start, so the evidence of a session was
/// gone the moment the client was started again.</summary>
public static class LogRotation
{
    /// <summary>Moves <c>dir/base+ext</c> aside (when it has content) and deletes the oldest
    /// rotated copies so that, with the file about to be written, at most
    /// <paramref name="maxFiles"/> remain -- the meaning of Godot's <c>max_log_files</c>.</summary>
    /// <returns>The path of the file to write this session. Normally <c>dir/base+ext</c>. If
    /// the previous file cannot be moved (an older client holds it open without delete sharing),
    /// a per-process name next to it, so that client's file is never truncated.</returns>
    public static string Rotate(string dir, string baseName, string extension, DateTime now, int maxFiles)
    {
        string current = Path.Combine(dir, baseName + extension);
        try
        {
            var info = new FileInfo(current);
            if (info.Exists)
            {
                if (info.Length == 0)
                {
                    info.Delete();
                }
                else
                {
                    string stamp = now.ToString("yyyy-MM-dd'T'HH.mm.ss", CultureInfo.InvariantCulture);
                    string target = Path.Combine(dir, baseName + stamp + extension);
                    for (int n = 2; File.Exists(target); n++)
                        target = Path.Combine(dir, $"{baseName}{stamp}-{n}{extension}");
                    File.Move(current, target);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(dir, $"{baseName}-{Environment.ProcessId}{extension}");
        }

        Prune(dir, baseName, extension, Math.Max(0, maxFiles - 1));
        return current;
    }

    /// <summary>Keeps the <paramref name="keep"/> newest rotated copies. A rotated copy is
    /// <c>base</c> followed by a digit (the timestamp) or by <c>-</c> and a process id, and
    /// <c>ext</c>; nothing else in the folder is touched.</summary>
    private static void Prune(string dir, string baseName, string extension, int keep)
    {
        var rotated = Directory.EnumerateFiles(dir, baseName + "*" + extension)
            .Select(path => new FileInfo(path))
            .Where(f => IsRotatedCopy(f.Name, baseName, extension))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(keep);
        foreach (var old in rotated)
        {
            try { old.Delete(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* still open elsewhere */ }
        }
    }

    private static bool IsRotatedCopy(string name, string baseName, string extension)
    {
        if (!name.StartsWith(baseName, StringComparison.Ordinal) || !name.EndsWith(extension, StringComparison.Ordinal)) return false;
        string middle = name.Substring(baseName.Length, name.Length - baseName.Length - extension.Length);
        if (middle.Length == 0) return false;
        if (char.IsAsciiDigit(middle[0])) return true;
        return middle[0] == '-' && middle.Length > 1 && middle.Skip(1).All(char.IsAsciiDigit);
    }
}
