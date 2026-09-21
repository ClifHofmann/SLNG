using System;
using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>
/// Decides when a region's event queue has stopped working, and — just as importantly — when to
/// say so.
/// </summary>
/// <remarks>
/// A simulator's EventQueueGet capability can disappear from under a running client: a region
/// restart is the usual way. What makes that hard to notice is the shape of the failure. The
/// server answers <b>HTTP 200</b> with a plain-text body ("cap not found: '…'"), and
/// LibreMetaverse reads any 200 as proof of health — it clears its backoff, logs the body and asks
/// again, for ever, without telling anyone. Neither <c>EventQueueRunning</c> (raised from
/// <c>ConnectedResponseHandler</c>, which only checks <c>IsSuccessStatusCode</c>) nor
/// <c>EventQueueClient.Running</c> ("the task is alive") ever flips, so there is no library signal
/// to subscribe to. The failures are visible in exactly one place: the log LibreMetaverse writes
/// into the factory we hand it.
///
/// <para>What it costs while nobody notices: everything delivered by EventQueue stops for that
/// region — group chat invitations, teleport progress, ObjectMedia, parcel and environment
/// pushes — and the log fills with one repeated line, drowning every other diagnostic. Both halves
/// are the bug (BUG-NET-20).</para>
///
/// <para>Engine- and protocol-agnostic on purpose: the rule is "how many failures, how close
/// together, and how often may we complain", which is worth testing without a grid. The caller
/// supplies the clock so a test does not have to sleep.</para>
/// </remarks>
public sealed class EventQueueHealth
{
    /// <summary>How many failures in a row before a region counts as stalled.</summary>
    /// <remarks>
    /// Not one. A single bad response is ordinary — a proxy hiccup, a 502 in the body, a region
    /// mid-handover — and the queue recovers by itself. What is not ordinary is the same answer
    /// arriving over and over, which is what "the capability is gone" looks like.
    /// </remarks>
    public const int FailuresBeforeStalled = 5;

    /// <summary>Failures further apart than this do not count towards each other.</summary>
    /// <remarks>
    /// Without a window, five unrelated hiccups spread over an afternoon would eventually add up
    /// to a false alarm. A dead queue produces its failures about a second apart, so a generous
    /// window still separates the two cases easily.
    /// </remarks>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(60);

    /// <summary>How long a stalled region stays quiet before it may be announced again.</summary>
    /// <remarks>
    /// The whole point is to replace 65 identical lines with one. But never repeating is wrong
    /// too: a queue that is still dead ten minutes later is worth a second mention, because by
    /// then the user has done several things that silently did not work.
    /// </remarks>
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromMinutes(10);

    private sealed class RegionState
    {
        public int Failures;
        public DateTime LastFailureUtc;
        public DateTime? AnnouncedUtc;
    }

    private readonly Dictionary<string, RegionState> _regions = new(StringComparer.Ordinal);

    /// <summary>Records one bad event-queue response for a region. True exactly on the responses
    /// where the caller should announce that this region's queue is stalled.</summary>
    /// <param name="regionKey">Whatever identifies the region to the caller. Compared verbatim, so
    /// the caller is responsible for using one spelling throughout.</param>
    /// <param name="utcNow">The clock, supplied so this is testable.</param>
    public bool ReportFailure(string regionKey, DateTime utcNow)
    {
        if (string.IsNullOrEmpty(regionKey)) return false;

        if (!_regions.TryGetValue(regionKey, out var state))
        {
            state = new RegionState();
            _regions[regionKey] = state;
        }

        // A long gap means the previous failures were something else; start counting again rather
        // than letting unrelated hiccups accumulate into a false alarm.
        if (state.Failures > 0 && utcNow - state.LastFailureUtc > FailureWindow)
        {
            state.Failures = 0;
            state.AnnouncedUtc = null;
        }

        state.Failures++;
        state.LastFailureUtc = utcNow;

        if (state.Failures < FailuresBeforeStalled) return false;

        if (state.AnnouncedUtc is { } announced && utcNow - announced < RepeatAfter) return false;

        state.AnnouncedUtc = utcNow;
        return true;
    }

    /// <summary>How many failures a region has accumulated in the current window. For the log line
    /// that announces the stall — "this has happened N times" is the part that tells the reader it
    /// is not a one-off.</summary>
    public int FailureCount(string regionKey)
        => _regions.TryGetValue(regionKey, out var s) ? s.Failures : 0;

    /// <summary>Whether a region is currently counted as stalled.</summary>
    public bool IsStalled(string regionKey)
        => _regions.TryGetValue(regionKey, out var s) && s.Failures >= FailuresBeforeStalled;

    // There is deliberately no ReportSuccess: a poll that works writes nothing to the log, so
    // there is no signal to hang one on. Recovery is handled by FailureWindow instead -- once the
    // failures stop for a minute the count and the announcement both reset, so a queue that dies
    // again later is announced again. An API with no caller would only look like coverage.

    /// <summary>Drops everything. For a logout, where the next session's regions have nothing to do
    /// with this one's.</summary>
    public void Clear() => _regions.Clear();

    /// <summary>
    /// Recognises the log lines LibreMetaverse writes when an event-queue poll comes back with
    /// something that is not LLSD, and hands back the simulator it names.
    /// </summary>
    /// <remarks>
    /// The two shapes, both from <c>EventQueueClient</c>:
    /// <code>
    /// Skipping LLSD parsing; server returned non-LLSD response from {sim}: "cap not found: '…'"
    /// Could not parse response (1) from {sim} event queue: "…"
    /// </code>
    /// Matching a log message is a poor way to learn something, and it is here for one reason:
    /// this is the only place the failure is visible. The library reports it nowhere else —
    /// <c>EventQueueRunning</c> is raised from <c>ConnectedResponseHandler</c>, which checks only
    /// <c>IsSuccessStatusCode</c>, and the dead queue answers with a perfectly good HTTP 200.
    ///
    /// <para>Deliberately tolerant: it matches on the stable phrases rather than the whole
    /// sentence, and a caller that gets <c>false</c> simply keeps the line. If a future
    /// LibreMetaverse reworded these, the failure mode is the old one — a noisy log — not a
    /// swallowed message or a false alarm.</para>
    /// </remarks>
    public static bool TryReadEventQueueFailure(string? message, out string simulatorText)
    {
        simulatorText = string.Empty;
        if (string.IsNullOrEmpty(message)) return false;

        const string NonLlsd = "server returned non-LLSD response from ";
        const string Unparsed = "Could not parse response";

        int from;
        if (message.Contains(NonLlsd, StringComparison.Ordinal))
        {
            from = message.IndexOf(NonLlsd, StringComparison.Ordinal) + NonLlsd.Length;
        }
        else if (message.StartsWith(Unparsed, StringComparison.Ordinal)
                 && message.Contains(" event queue:", StringComparison.Ordinal))
        {
            const string Marker = " from ";
            int i = message.IndexOf(Marker, StringComparison.Ordinal);
            if (i < 0) return false;
            from = i + Marker.Length;
        }
        else
        {
            return false;
        }

        // Where the simulator ends. " event queue:" has to be tried FIRST: the second shape ends
        // "… from {sim} event queue: \"…\"", so looking for the quoted body would swallow those
        // two words into the region's name.
        int end = message.IndexOf(" event queue:", from, StringComparison.Ordinal);
        if (end < 0) end = message.IndexOf(": \"", from, StringComparison.Ordinal);
        simulatorText = (end < 0 ? message.Substring(from) : message.Substring(from, end - from)).Trim();
        return true;
    }

    /// <summary>
    /// Pulls the region's name out of the way LibreMetaverse renders a simulator into a log line:
    /// <c>Millenium (54.218.44.155:13037)</c>. Empty when it does not look like that.
    /// </summary>
    /// <remarks>
    /// Parsing a log line is not how one would choose to learn this, and it is written down here
    /// rather than buried in the caller so the fragility is visible: the format belongs to
    /// LibreMetaverse and could change. The caller must therefore keep working with an empty
    /// result — an unnamed stalled region is still worth reporting.
    /// </remarks>
    public static string RegionNameFromSimulatorText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        int open = text.LastIndexOf(" (", StringComparison.Ordinal);
        if (open <= 0) return text.Trim();

        // Only treat it as the address suffix if it really looks like one.
        int close = text.IndexOf(')', open);
        if (close < 0) return text.Trim();
        string inside = text.Substring(open + 2, close - open - 2);
        if (inside.IndexOf(':') < 0) return text.Trim();

        return text.Substring(0, open).Trim();
    }
}
