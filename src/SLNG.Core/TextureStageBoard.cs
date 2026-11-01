using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace SLNG.Core;

/// <summary>
/// FEAT-PERF-30: the stages a texture passes between a face asking for it and the texture being bound, in the order a warm one takes
/// them. A texture is in exactly one stage at a time; <see cref="Done"/> is the end and is not reported.
/// </summary>
public enum TexStage
{
    /// <summary>Asked for, not yet picked up: a pool thread has not started it, or the cache is deciding where to look.</summary>
    Pool = 0,
    /// <summary>Looking for the block-compressed warm copy in the decoded disk cache (FEAT-PERF-17 part B).</summary>
    Copy,
    /// <summary>Raw .j2c: existence check and read from the asset cache.</summary>
    Disk,
    /// <summary>Waiting for one of the decode slots (<c>AssetService</c>'s texture decode gate).</summary>
    DecodeWait,
    /// <summary>Holding a decode slot, reading the decoded copy from the decoded disk cache (FEAT-PERF-11).</summary>
    DecodedRead,
    /// <summary>Holding a decode slot, decoding the J2K (Magick, or CoreJ2K as the fallback).</summary>
    Decode,
    /// <summary>Waiting for an HTTP slot, then fetching. Cold textures only.</summary>
    Net,
    /// <summary>Waiting for the image-prep gate and a pool thread.</summary>
    PrepWait,
    /// <summary>Building the Godot <c>Image</c> (resize, mipmaps, optional block compression) on a worker.</summary>
    Prepare,
    /// <summary>In <c>MainThreadWorkQueue</c> (label <c>texture.upload</c>) until its item starts.</summary>
    QueueWait,
    /// <summary>The main-thread item itself: <c>CommitPreparedTexture</c>.</summary>
    Upload,
    /// <summary>Uploaded; the awaiting code has not resumed yet. Not a reported stage.</summary>
    Done,
}

/// <summary>How a texture came to be decoded pixels, for the <c>[TexStage]</c> counters.</summary>
public enum TexPath
{
    /// <summary>The block-compressed copy from the decoded disk cache.</summary>
    Copy = 0,
    /// <summary>The decoded RGBA copy from the decoded disk cache.</summary>
    DecodedCache,
    /// <summary>A J2K decode of the cached .j2c.</summary>
    Decode,
    /// <summary>Fetched over HTTP (or UDP), then decoded.</summary>
    Http,
}

/// <summary>
/// FEAT-PERF-30: where the time goes between a face asking for a texture and the texture being bound. It changes no behaviour; it
/// only watches.
///
/// <para>The unit is one texture fetch (<c>GpuCache</c> single-flights per texture id), keyed by the texture id: the owner calls
/// <see cref="Open"/>, each stage change calls <see cref="Enter"/>, and <see cref="Close"/> ends it. Every face that asks while the
/// fetch is open is added with <see cref="AddFace"/>, so a gauge can say both "textures in this stage" and "faces waiting in it" -- the
/// second is what <c>[ArrivalCount]</c> reports. Leaving a stage records its duration; a stage a texture has not left yet has none,
/// only an age (<see cref="StageInterval.OldestOpenMs"/>), which is how a stuck stage shows before anything has come out of it.</para>
///
/// <para>A call for an id nobody opened is ignored, so the other callers of the asset service (terrain, sculpts, the shrink path) pay
/// one failed dictionary lookup and are not measured.</para>
///
/// <para>Thread-safe. Per texture one lock moves it between stages; the gauges are <see cref="Interlocked"/> counters. Nothing here
/// allocates per frame: one small object per texture fetch.</para>
/// </summary>
public sealed class TextureStageBoard
{
    /// <summary>The stages that are reported, <see cref="TexStage.Pool"/> up to and including <see cref="TexStage.Upload"/>.</summary>
    public const int ReportedStages = (int)TexStage.Done;

    /// <param name="Textures">Fetches in the stage right now.</param>
    /// <param name="Faces">Faces waiting on those fetches.</param>
    public readonly record struct StageGauge(long Textures, long Faces);

    /// <summary>One stage over one interval, and what is in it now.</summary>
    public readonly record struct StageInterval(
        int Count, double P50, double P95, double Max, long OpenTextures, long OpenFaces, double OldestOpenMs);

    /// <summary>The counters of one interval (see <see cref="TakeCounters"/>).</summary>
    public readonly record struct Counters(
        long Lookups, long LookupHits, long Opened, long Faces, long CopyAttempts, long CopyFound,
        long Copy, long DecodedCache, long Decode, long Http);

    private sealed class Tracker
    {
        public readonly long OpenedMicros;
        public long EnteredMicros;
        public int Stage;
        public long Faces;
        public bool Closed;

        public Tracker(long now) { OpenedMicros = now; EnteredMicros = now; Stage = (int)TexStage.Pool; }
    }

    private static readonly string[] StageNames =
    {
        "Pool", "Copy", "Disk", "DecodeWait", "DecodedRead", "Decode", "Net", "PrepWait", "Prepare", "QueueWait", "Upload", "Done",
    };

    private readonly Func<long> _nowMicros;
    private readonly ConcurrentDictionary<Guid, Tracker> _open = new();
    private readonly long[] _textures = new long[ReportedStages + 1];
    private readonly long[] _faces = new long[ReportedStages + 1];
    private readonly SampleWindow[] _windows = new SampleWindow[ReportedStages + 1];
    private readonly SampleWindow _total = new();

    private long _lookups, _lookupHits, _opened, _facesAdded, _copyAttempts, _copyFound;
    private readonly long[] _paths = new long[4];

    /// <param name="nowMicros">A monotonic clock in microseconds. Null (the default) is <see cref="Stopwatch"/>. A test sets it.</param>
    public TextureStageBoard(Func<long>? nowMicros = null)
    {
        _nowMicros = nowMicros ?? (() => (long)(Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency));
        for (int i = 0; i < _windows.Length; i++) _windows[i] = new SampleWindow();
    }

    /// <summary>The name used in the log lines (<c>[TexStage.DecodeWait]</c>).</summary>
    public static string Name(TexStage stage) => StageNames[(int)stage];

    /// <summary>A texture fetch starts, in <see cref="TexStage.Pool"/>. A second open for an id that is still open changes nothing.</summary>
    public void Open(Guid id)
    {
        var t = new Tracker(_nowMicros());
        if (!_open.TryAdd(id, t)) return;
        Interlocked.Increment(ref _textures[t.Stage]);
        Interlocked.Increment(ref _opened);
    }

    /// <summary>A face is now waiting on this fetch, in whatever stage it is in.</summary>
    public void AddFace(Guid id)
    {
        if (!_open.TryGetValue(id, out var t)) return;
        lock (t)
        {
            if (t.Closed) return;
            t.Faces++;
            Interlocked.Increment(ref _faces[t.Stage]);
        }
        Interlocked.Increment(ref _facesAdded);
    }

    /// <summary>The fetch leaves its stage (recording how long it was there) and enters <paramref name="stage"/>.</summary>
    public void Enter(Guid id, TexStage stage)
    {
        if (!_open.TryGetValue(id, out var t)) return;
        long now = _nowMicros();
        lock (t)
        {
            if (t.Closed || t.Stage == (int)stage) return;
            Leave(t, now);
            Volatile.Write(ref t.Stage, (int)stage);
            Volatile.Write(ref t.EnteredMicros, now);
            Interlocked.Increment(ref _textures[(int)stage]);
            Interlocked.Add(ref _faces[(int)stage], t.Faces);
        }
    }

    /// <summary>The fetch is over, with a texture or without: records the stage it was in and its total, and forgets it.</summary>
    public void Close(Guid id)
    {
        if (!_open.TryRemove(id, out var t)) return;
        long now = _nowMicros();
        lock (t)
        {
            if (t.Closed) return;
            t.Closed = true;
            Leave(t, now);
            _total.Add((now - t.OpenedMicros) / 1000.0);
        }
    }

    private void Leave(Tracker t, long now)
    {
        int stage = t.Stage;
        Interlocked.Decrement(ref _textures[stage]);
        Interlocked.Add(ref _faces[stage], -t.Faces);
        if (stage != (int)TexStage.Done) _windows[stage].Add((now - Volatile.Read(ref t.EnteredMicros)) / 1000.0);
    }

    /// <summary>A GPU-cache lookup for a face's texture, hit or miss.</summary>
    public void NoteLookup(bool hit)
    {
        Interlocked.Increment(ref _lookups);
        if (hit) Interlocked.Increment(ref _lookupHits);
    }

    /// <summary>A look for the block-compressed warm copy, found or not.</summary>
    public void NoteCopyAttempt(bool found)
    {
        Interlocked.Increment(ref _copyAttempts);
        if (found) Interlocked.Increment(ref _copyFound);
    }

    /// <summary>How one texture came to be pixels.</summary>
    public void NotePath(TexPath path) => Interlocked.Increment(ref _paths[(int)path]);

    /// <summary>What is in <paramref name="stage"/> right now.</summary>
    public StageGauge Gauge(TexStage stage) =>
        new(Interlocked.Read(ref _textures[(int)stage]), Interlocked.Read(ref _faces[(int)stage]));

    /// <summary>The durations of the fetches that left <paramref name="stage"/> since the previous call (a new interval starts), with
    /// the gauge and the age of the oldest fetch still in it.</summary>
    public StageInterval TakeStage(TexStage stage)
    {
        var s = _windows[(int)stage].Take();
        var g = Gauge(stage);
        return new StageInterval(s.Count, s.P50, s.P95, s.Max, g.Textures, g.Faces, g.Textures > 0 ? OldestOpenMs(stage) : 0);
    }

    /// <summary>Request-to-bound for the fetches that closed since the previous call.</summary>
    public SampleWindow.Summary TakeTotal() => _total.Take();

    /// <summary>The counters since the previous call (a new interval starts).</summary>
    public Counters TakeCounters() => new(
        Interlocked.Exchange(ref _lookups, 0), Interlocked.Exchange(ref _lookupHits, 0), Interlocked.Exchange(ref _opened, 0),
        Interlocked.Exchange(ref _facesAdded, 0), Interlocked.Exchange(ref _copyAttempts, 0), Interlocked.Exchange(ref _copyFound, 0),
        Interlocked.Exchange(ref _paths[(int)TexPath.Copy], 0), Interlocked.Exchange(ref _paths[(int)TexPath.DecodedCache], 0),
        Interlocked.Exchange(ref _paths[(int)TexPath.Decode], 0), Interlocked.Exchange(ref _paths[(int)TexPath.Http], 0));

    private double OldestOpenMs(TexStage stage)
    {
        long now = _nowMicros();
        long oldest = long.MaxValue;
        foreach (var t in _open.Values)
        {
            if (Volatile.Read(ref t.Stage) != (int)stage) continue;
            long entered = Volatile.Read(ref t.EnteredMicros);
            if (entered < oldest) oldest = entered;
        }
        return oldest == long.MaxValue ? 0 : Math.Max(0, now - oldest) / 1000.0;
    }

    /// <summary>
    /// Where the faces that are waiting are, for the <c>[ArrivalCount]</c> lines: <c>decodeWait=2100f/900t</c> is 2100 faces on 900
    /// textures in the decode-slot queue. Only stages that hold something; "none" when nothing waits.
    /// </summary>
    public string DescribeWaiting()
    {
        var sb = new StringBuilder();
        for (var s = TexStage.Pool; s < TexStage.Done; s++)
        {
            var g = Gauge(s);
            if (g.Textures <= 0 && g.Faces <= 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            string name = Name(s);
            sb.Append(char.ToLowerInvariant(name[0])).Append(name, 1, name.Length - 1)
              .Append('=').Append(g.Faces.ToString(CultureInfo.InvariantCulture)).Append("f/")
              .Append(g.Textures.ToString(CultureInfo.InvariantCulture)).Append('t');
        }
        return sb.Length == 0 ? "none" : sb.ToString();
    }

    /// <summary>One <c>[TexStage.&lt;name&gt;]</c> line for the interval since the previous call, or null when nothing left the stage in it
    /// and nothing is in it now (so an idle stage is silent).</summary>
    public string? TakeStageLine(TexStage stage)
    {
        var s = TakeStage(stage);
        if (s.Count == 0 && s.OpenTextures == 0) return null;
        var c = CultureInfo.InvariantCulture;
        string line = string.Create(c,
            $"[TexStage.{Name(stage)}] n={s.Count} p50={s.P50:0}ms p95={s.P95:0}ms max={s.Max:0}ms open={s.OpenTextures}");
        return s.OpenTextures > 0 ? string.Create(c, $"{line} faces={s.OpenFaces} oldest={s.OldestOpenMs:0}ms") : line;
    }

    /// <summary>The <c>[TexStage.Total]</c> line: request to bound texture, for the fetches that closed in the interval.</summary>
    public string? TakeTotalLine()
    {
        var s = TakeTotal();
        long open = 0, faces = 0;
        for (var st = TexStage.Pool; st < TexStage.Done; st++) { var g = Gauge(st); open += g.Textures; faces += g.Faces; }
        if (s.Count == 0 && open == 0) return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"[TexStage.Total] n={s.Count} p50={s.P50:0}ms p95={s.P95:0}ms max={s.Max:0}ms open={open} faces={faces}");
    }

    /// <summary>The <c>[TexStage]</c> counters line for the interval, or null when nothing was counted.</summary>
    public string? TakeCountersLine()
    {
        var c = TakeCounters();
        if (c.Lookups == 0 && c.Opened == 0 && c.CopyAttempts == 0 && c.Copy + c.DecodedCache + c.Decode + c.Http == 0) return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"[TexStage] gpuLookups=+{c.Lookups} gpuHits=+{c.LookupHits} fetches=+{c.Opened} faces=+{c.Faces} | " +
            $"warmCopy tried=+{c.CopyAttempts} found=+{c.CopyFound} | made: copy=+{c.Copy} decodedCache=+{c.DecodedCache} " +
            $"decode=+{c.Decode} http=+{c.Http}");
    }
}

/// <summary>
/// FEAT-PERF-30: the one board the client uses, behind the verbose switch. Call sites do not guard: with <see cref="Diag.Verbose"/> off
/// each call is a read of one static and a return. <c>Core</c> reaches neither the cache (<c>app</c>) nor the asset pipeline
/// (<c>SLNG.Assets</c>), so this is the side channel between them, as <see cref="TexClassRegistry"/> is for the class of a request.
/// </summary>
public static class TexStages
{
    /// <summary>The board behind the calls below. A test may read it; the client does not need to.</summary>
    public static TextureStageBoard Board { get; } = new();

    private static int _registered;

    public static void Open(Guid id) { if (Diag.Verbose) Board.Open(id); }
    public static void AddFace(Guid id) { if (Diag.Verbose) Board.AddFace(id); }
    public static void Enter(Guid id, TexStage stage) { if (Diag.Verbose) Board.Enter(id, stage); }
    public static void Close(Guid id) { if (Diag.Verbose) Board.Close(id); }
    public static void NoteLookup(bool hit) { if (Diag.Verbose) Board.NoteLookup(hit); }
    public static void NoteCopyAttempt(bool found) { if (Diag.Verbose) Board.NoteCopyAttempt(found); }
    public static void NotePath(TexPath path) { if (Diag.Verbose) Board.NotePath(path); }

    /// <summary>Puts the <c>[TexStage*]</c> lines on the 5-second reporter. Once; does nothing without <c>--diag</c>.</summary>
    public static void RegisterReporter()
    {
        if (!Diag.Verbose || Interlocked.Exchange(ref _registered, 1) == 1) return;
        FetchPipeReporter.Register(Board.TakeCountersLine);
        for (var s = TexStage.Pool; s < TexStage.Done; s++)
        {
            var stage = s;
            FetchPipeReporter.Register(() => Board.TakeStageLine(stage));
        }
        FetchPipeReporter.Register(Board.TakeTotalLine);
    }
}
