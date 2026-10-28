using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Godot;
using SLNG.Core;
using SLNG.Core.Logging;

namespace SLNG.App;

/// <summary>
/// FEAT-UI-76: the opt-in debug log. While it is on, every line the client prints -- its own, the libraries' (they log
/// through <c>Console</c>, which <see cref="ConsoleToGodotLog"/> turns into engine output), and the engine's warnings
/// and errors -- is run through <see cref="LogSanitizer"/> and appended to <c>logs/debug/slng-debug.log</c>, and the
/// verbose per-object diagnostics (<see cref="Diag.Verbose"/>, normally <c>--diag</c> only) are switched on too. Those
/// verbose lines go to this file ALONE (<see cref="Diag.Sink"/>), so the console and <c>godot.log</c> stay as readable as with the
/// log off; only <c>--diag</c> sends them to the console as well.
///
/// <para>The engine's own <c>godot.log</c> is not changed and is not sanitized; this file is the one meant to be
/// handed to somebody. It is rotated like the other logs (ten sessions are kept) and capped at
/// <see cref="MaxBytes"/> so a long session cannot fill a disk.</para>
///
/// <para>Writing never blocks the caller: lines go into a bounded queue that one background task drains, and a line that
/// does not fit is counted and reported rather than stalling a render or network thread.</para>
/// </summary>
public static class DebugLog
{
    public const long MaxBytes = 64L * 1024 * 1024;
    private const int MaxFiles = 10;
    private const int QueueLines = 100_000;
    private const string BaseName = "slng-debug";
    private const string Extension = ".log";

    private static readonly object Gate = new();
    private static Channel<string>? _channel;
    private static Task? _writer;
    private static StreamWriter? _file;
    private static bool _diagBefore;
    private static long _dropped;
    private static volatile bool _running;
    private static long _written;

    /// <summary>Raised on the thread that called <see cref="Start"/> or <see cref="Stop"/>.</summary>
    public static event Action? StateChanged;

    public static bool IsRunning => _running;

    /// <summary>The folder the logs live in, as a path the operating system understands.</summary>
    public static string DirectoryPath => ProjectSettings.GlobalizePath(SelfTest.UserLogsDir + "/debug");

    /// <summary>The file being written, or null while the log is off.</summary>
    public static string? CurrentFile { get; private set; }

    /// <summary>Bytes written to <see cref="CurrentFile"/> so far.</summary>
    public static long BytesWritten => Interlocked.Read(ref _written);

    public static void Start()
    {
        lock (Gate)
        {
            if (_running) return;
            try
            {
                string dir = DirectoryPath;
                Directory.CreateDirectory(dir);
                string path = LogRotation.Rotate(dir, BaseName, Extension, DateTime.Now, MaxFiles);
                // Shared for reading: the person opens the file in an editor while it is still being written.
                var stream = new FileStream(path, FileMode.Create, System.IO.FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _file = new StreamWriter(stream, new UTF8Encoding(false));
                CurrentFile = path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                GD.PrintErr($"[DebugLog] cannot open the log file: {e.Message}");
                return;
            }

            _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueLines)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropWrite,
            });
            Interlocked.Exchange(ref _written, 0);
            Interlocked.Exchange(ref _dropped, 0);
            _diagBefore = Diag.Verbose;
            Diag.Verbose = true;
            _running = true;

            var reader = _channel.Reader;
            var file = _file;
            _writer = Task.Run(async () => await DrainAsync(reader, file).ConfigureAwait(false));
        }

        WriteHeader();
        StateChanged?.Invoke();
    }

    public static void Stop()
    {
        Task? writer;
        StreamWriter? file;
        lock (Gate)
        {
            if (!_running) return;
            _running = false;
            Diag.Verbose = _diagBefore;
            long dropped = Interlocked.Read(ref _dropped);
            if (dropped > 0) _channel!.Writer.TryWrite($"[DebugLog] {dropped} line(s) did not fit the queue and were dropped");
            _channel!.Writer.TryComplete();
            writer = _writer;
            file = _file;
            _channel = null;
            _writer = null;
            _file = null;
        }

        try { writer?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { /* a write that failed has nothing left to report to */ }
        try { file?.Dispose(); }
        catch (IOException) { }

        CurrentFile = null;
        StateChanged?.Invoke();
    }

    /// <summary>Appends one log entry. Safe from any thread, a no-op while the log is off. A multi-line entry becomes
    /// one line each, so a line is always sanitized as a whole.</summary>
    public static void Write(string level, string text)
    {
        if (!_running || string.IsNullOrEmpty(text)) return;
        var channel = _channel;
        if (channel == null) return;

        string stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        int thread = System.Environment.CurrentManagedThreadId;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            string entry = $"{stamp} {level,-5} T{thread,-3} {LogSanitizer.Shared.Sanitize(line)}";
            if (!channel.Writer.TryWrite(entry)) Interlocked.Increment(ref _dropped);
        }
    }

    private static async Task DrainAsync(ChannelReader<string> reader, StreamWriter file)
    {
        bool full = false;
        try
        {
            await foreach (string entry in reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (full) continue; // keep draining so writers never back up, but write nothing more
                if (Interlocked.Read(ref _written) + entry.Length > MaxBytes)
                {
                    full = true;
                    file.WriteLine($"[DebugLog] size limit of {MaxBytes / (1024 * 1024)} MB reached; the rest of this session is not written");
                    file.Flush();
                    continue;
                }
                file.WriteLine(entry);
                Interlocked.Add(ref _written, entry.Length + 2);
                if (reader.Count == 0) file.Flush(); // idle: the file is complete up to the last line even after a crash
            }
            file.Flush();
        }
        catch (IOException) { /* disk full or the file went away: nothing sensible left to do */ }
        catch (ObjectDisposedException) { }
    }

    private static void WriteHeader()
    {
        try
        {
            Write("INFO", $"[DebugLog] started {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            Write("INFO", $"[DebugLog] Puris Viewer {Boot.AppVersion}, Godot {Engine.GetVersionInfo()["string"]}");
            Write("INFO", $"[DebugLog] {OS.GetName()} {OS.GetVersion()}, {OS.GetProcessorCount()} logical processors");
            Write("INFO", $"[DebugLog] GPU {RenderingServer.GetVideoAdapterName()}");
            Write("INFO", "[DebugLog] Sanitized: identifiers are replaced by short tags that stay the same inside this file only; " +
                          "passwords, tokens, addresses, URL paths and home-folder names are removed. Free text from the world " +
                          "(object and parcel names) cannot be recognised -- read the file before sharing it.");
        }
        catch (Exception e)
        {
            Write("WARN", $"[DebugLog] could not write the system header: {e.GetType().Name}");
        }
    }
}
