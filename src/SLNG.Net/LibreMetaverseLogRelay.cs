using System;
using Microsoft.Extensions.Logging;

namespace SLNG.Net;

/// <summary>
/// The console sink SLNG hands to LibreMetaverse, with one line of its own between the library and
/// the terminal: every message passes a caller-supplied filter first, which may read it and may
/// swallow it.
/// </summary>
/// <remarks>
/// It exists because of BUG-NET-20. When a region's EventQueueGet capability disappears, the
/// library keeps polling for ever and writes the same warning about once a second, and it reports
/// the condition <b>nowhere else</b> — the server's error arrives as HTTP 200, so every liveness
/// flag stays green. The log is the only place the truth appears, and this factory is ours, so
/// this is where it can be read.
///
/// <para>The format is deliberately the same one <c>AddSimpleConsole</c> produced before —
/// <c>warn: SLNG[0] message</c> — so old and new logs still read alike and existing greps keep
/// working.</para>
///
/// <para>Filtering rather than an extra listening provider, because half of this bug is the flood:
/// 65 identical lines with nothing between them hid every other diagnostic in the session. A
/// watcher that only observed would leave that in place.</para>
/// </remarks>
internal sealed class LibreMetaverseLogRelay : ILoggerProvider
{
    private readonly string _category;
    private readonly Func<LogLevel, string, bool> _shouldWrite;

    /// <param name="category">The category name the lines carry, as the previous console logger
    /// showed it.</param>
    /// <param name="shouldWrite">Sees every message before it is printed; false swallows the line.
    /// Called on whichever thread LibreMetaverse logged from — a network thread as a rule — so it
    /// must be safe there and must not block.</param>
    internal LibreMetaverseLogRelay(string category, Func<LogLevel, string, bool> shouldWrite)
    {
        _category = category;
        _shouldWrite = shouldWrite;
    }

    public ILogger CreateLogger(string categoryName) => new Relay(_category, _shouldWrite);

    public void Dispose() { }

    private sealed class Relay : ILogger
    {
        private readonly string _category;
        private readonly Func<LogLevel, string, bool> _shouldWrite;

        internal Relay(string category, Func<LogLevel, string, bool> shouldWrite)
        {
            _category = category;
            _shouldWrite = shouldWrite;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // The factory's own minimum level already filters; anything that reaches here is wanted.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            string message;
            try { message = formatter(state, exception) ?? string.Empty; }
            catch { return; }

            bool write;
            // A filter that throws must not cost the line, let alone take down the network thread
            // it was called on.
            try { write = _shouldWrite(logLevel, message); }
            catch { write = true; }
            if (!write) return;

            var text = $"{ShortName(logLevel)}: {_category}[{eventId.Id}] {message}";
            if (exception != null) text += Environment.NewLine + exception;
            Console.Error.WriteLine(text);
        }

        /// <summary>The four-letter level names the console logger used, kept so a log from before
        /// this change and one from after are the same text.</summary>
        private static string ShortName(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none",
        };
    }
}
