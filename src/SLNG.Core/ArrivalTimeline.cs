using System;
using System.Collections.Generic;
using System.Globalization;

namespace SLNG.Core;

/// <summary>What the renderer sees at one moment of an arrival (FEAT-PERF-28). All counts, no ids.</summary>
/// <param name="FullAvatars">Shown avatars drawn in full (the local one included).</param>
/// <param name="FullWithAllBakes">Of those, the ones whose every wanted bake is applied.</param>
/// <param name="FullWithoutBakes">Of those, the ones with no bake applied at all.</param>
/// <param name="ReducedAvatars">Shown avatars over the cap, drawn as stand-ins.</param>
/// <param name="ReducedWithoutBakes">Of those, the ones with no bake applied.</param>
/// <param name="FacesWaiting">Texture requests of faces that are still waiting for their texture.</param>
/// <param name="TexInflight">Textures in flight in the asset pipeline (the <c>inflight</c> of <c>[TexPipe]</c>).</param>
/// <param name="TextureBytes">Texture bytes received over HTTP since the process started.</param>
public readonly record struct ArrivalSample(
    int FullAvatars, int FullWithAllBakes, int FullWithoutBakes,
    int ReducedAvatars, int ReducedWithoutBakes,
    int FacesWaiting, int TexInflight, long TextureBytes);

/// <summary>
/// FEAT-PERF-28: the time base for "how long does it take to arrive in a region". It changes no behaviour; it only watches.
///
/// <para>The clock starts at the region handshake of the region that became current. From there it notes when
/// <list type="bullet">
/// <item>every full avatar has every bake it wants (<c>bakes</c>),</item>
/// <item>no face is waiting for a texture any more (<c>noFaceWithoutTexture</c>),</item>
/// <item>the texture pipeline has nothing in flight (<c>texInflight0</c>),</item>
/// </list>
/// each with the seconds and the megabytes transferred until then, and writes ONE <c>[Arrival]</c> line when all three are reached,
/// when <see cref="GiveUpSeconds"/> have passed, or when the next region is entered. At +30 s and +60 s it writes a
/// <c>[ArrivalCount]</c> line with the reduced avatars and the full avatars still without bakes -- the two that tell cause 1 (over-cap
/// avatars draw no bakes) from cause 2 (their bakes queue behind the scenery) in ADR 0005.</para>
///
/// <para>A milestone counts only after it was seen to be open (something waiting) and then held for <see cref="SettleSeconds"/>:
/// an idle pipe at the instant of the handshake is not "everything arrived", and a queue that drains between two waves is not done.
/// Its time is the moment it first held, not the moment it was confirmed.</para>
///
/// <para>Pure and clock-free: every call carries its own <c>now</c>. Thread-safe -- the handshake comes from a network thread, the
/// samples from the main thread; lines are written outside the lock.</para>
/// </summary>
public sealed class ArrivalTimeline
{
    public const double SettleSeconds = 3.0;
    public const double GiveUpSeconds = 300.0;
    /// <summary>A handshake older than this when its region becomes current is a neighbour's, not the arrival's.</summary>
    public const double MaxHandshakeAgeSeconds = 30.0;

    private static readonly double[] CountAt = { 30.0, 60.0 };

    /// <summary>The one the client uses; it writes to <see cref="Diag"/> when the verbose switch is on.</summary>
    public static ArrivalTimeline Shared { get; } = new(line =>
    {
        if (Diag.Verbose) Diag.Write(line);
    });

    private sealed class Milestone
    {
        public bool Armed;
        public bool Reached;
        public double At;
        public long Bytes;
        private double? _runStart;
        private long _runBytes;

        public void Reset()
        {
            Armed = Reached = false;
            At = 0;
            Bytes = 0;
            _runStart = null;
        }

        public void Observe(bool arm, bool holds, double elapsed, long bytes)
        {
            if (Reached) return;
            if (arm) Armed = true;
            if (!Armed || !holds) { _runStart = null; return; }
            if (_runStart == null) { _runStart = elapsed; _runBytes = bytes; }
            if (elapsed - _runStart.Value >= SettleSeconds)
            {
                Reached = true;
                At = _runStart.Value;
                Bytes = _runBytes;
            }
        }
    }

    private readonly Action<string> _emit;
    private readonly object _lock = new();
    private readonly Dictionary<long, double> _handshakes = new();

    private long? _awaitingHandshakeFor;
    private string _awaitingName = "";

    private bool _active;
    private long _handle;
    private string _name = "";
    private double _startedAt;
    private bool _haveBaseline;
    private long _baseBytes;
    private bool _lineEmitted;
    private int _countsDone;
    private ArrivalSample _last;
    private readonly Milestone _bakes = new();
    private readonly Milestone _faces = new();
    private readonly Milestone _inflight = new();

    public ArrivalTimeline(Action<string> emit) => _emit = emit;

    /// <summary>True while an arrival is being watched, so a caller can skip building a sample when nothing wants it.</summary>
    public bool Active { get { lock (_lock) return _active; } }

    /// <summary>When the running arrival's clock started (its handshake), on the caller's clock.</summary>
    public double StartedAt { get { lock (_lock) return _startedAt; } }

    /// <summary>A <c>RegionHandshake</c> arrived from the simulator with this handle (any simulator, not only the current one).</summary>
    public void NoteHandshake(long handle, double now)
    {
        lock (_lock)
        {
            _handshakes[handle] = now;
            if (_awaitingHandshakeFor == handle)
            {
                _awaitingHandshakeFor = null;
                Start(handle, _awaitingName, now);
            }
        }
    }

    /// <summary>The region with this handle became the current one: an arrival (login, teleport, crossing). Ends the running one.</summary>
    public void NoteRegionCurrent(long handle, string name, double now)
    {
        var lines = new List<string>(2);
        lock (_lock)
        {
            if (_active) Finish("left for another region", lines);
            if (_handshakes.TryGetValue(handle, out double shook) && now - shook <= MaxHandshakeAgeSeconds)
            {
                _awaitingHandshakeFor = null;
                Start(handle, name, shook);
            }
            else
            {
                _awaitingHandshakeFor = handle;
                _awaitingName = name;
            }
        }
        Emit(lines);
    }

    /// <summary>One look at the world. Cheap; the caller decides how often (once a second is plenty).</summary>
    public void Update(in ArrivalSample sample, double now)
    {
        var lines = new List<string>(3);
        lock (_lock)
        {
            if (!_active) return;
            double elapsed = now - _startedAt;
            if (!_haveBaseline) { _haveBaseline = true; _baseBytes = sample.TextureBytes; }
            long bytes = sample.TextureBytes - _baseBytes;
            _last = sample;

            _bakes.Observe(true, sample.FullAvatars > 0 && sample.FullWithAllBakes == sample.FullAvatars, elapsed, bytes);
            _faces.Observe(sample.FacesWaiting > 0, sample.FacesWaiting == 0, elapsed, bytes);
            _inflight.Observe(sample.TexInflight > 0, sample.TexInflight == 0, elapsed, bytes);

            while (_countsDone < CountAt.Length && elapsed >= CountAt[_countsDone])
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"[ArrivalCount] +{CountAt[_countsDone]:0}s (sampled at +{elapsed:0.0}s) full={sample.FullAvatars} " +
                    $"fullNoBakes={sample.FullWithoutBakes} reduced={sample.ReducedAvatars} " +
                    $"reducedNoBakes={sample.ReducedWithoutBakes} facesWaiting={sample.FacesWaiting} texInflight={sample.TexInflight}"));
                _countsDone++;
            }

            if (!_lineEmitted && _bakes.Reached && _faces.Reached && _inflight.Reached)
                WriteLine("all milestones reached", lines);
            else if (!_lineEmitted && elapsed >= GiveUpSeconds)
                WriteLine($"not finished after {(int)GiveUpSeconds}s", lines);

            if (_lineEmitted && _countsDone >= CountAt.Length) _active = false;
        }
        Emit(lines);
    }

    private void Start(long handle, string name, double handshakeAt)
    {
        _active = true;
        _handle = handle;
        _name = name;
        _startedAt = handshakeAt;
        _haveBaseline = false;
        _baseBytes = 0;
        _lineEmitted = false;
        _countsDone = 0;
        _last = default;
        _bakes.Reset();
        _faces.Reset();
        _inflight.Reset();
    }

    private void Finish(string reason, List<string> lines)
    {
        if (!_lineEmitted) WriteLine(reason, lines);
        _active = false;
    }

    private void WriteLine(string ended, List<string> lines)
    {
        _lineEmitted = true;
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"[Arrival] region={(_name.Length > 0 ? _name : _handle.ToString(CultureInfo.InvariantCulture))} " +
            $"handshakeAt={_startedAt:0.0}s | bakes={Describe(_bakes)} | noFaceWithoutTexture={Describe(_faces)} | " +
            $"texInflight0={Describe(_inflight)} | at the last sample: fullAvatars={_last.FullAvatars} " +
            $"fullNoBakes={_last.FullWithoutBakes} reducedAvatars={_last.ReducedAvatars} facesWaiting={_last.FacesWaiting} " +
            $"texInflight={_last.TexInflight} | ended: {ended}"));
    }

    private static string Describe(Milestone m) => !m.Reached
        ? "never"
        : string.Create(CultureInfo.InvariantCulture, $"+{m.At:0.0}s/{m.Bytes / (1024.0 * 1024.0):0.0}MB");

    private void Emit(List<string> lines)
    {
        foreach (var line in lines)
        {
            try { _emit(line); }
            catch { /* a diagnostic must never take the process down */ }
        }
    }
}
