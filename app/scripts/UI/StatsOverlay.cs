using System;
using System.Collections.Generic;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// Always-on performance readout (FEAT-PERF-01), toggled with Ctrl+Shift+1 like the real viewer's
/// statistics floater, or from View -> Performance Stats.
///
/// Deliberately NOT an <see cref="SLNGWindow"/>: the UI standard covers draggable floaters, and this
/// is a passive corner readout in the same family as Boot's position HUD -- it never takes mouse
/// input, because the whole point is to watch it while flying, and a panel that swallows clicks in
/// the corner would fight the camera controls.
///
/// The numbers are chosen to answer "why does it hitch" rather than "how fast is it on average".
/// A mean FPS is nearly useless for stutter: a run that drops one 100 ms frame every second still
/// reads ~55 FPS. So the headline pairs the smoothed FPS with the 1% low, and the rows below split
/// the frame cost into the places it can come from:
///   - process/physics ms   -> main-thread C# work (asset upload, scene mutation, world drain)
///   - queue depth          -> main-thread work that is backlogged rather than lost
///   - draw calls / tris    -> GPU-side batching problems
///   - GC collections/s     -> managed allocation churn, the classic Godot-C# hitch source
/// The frame-time graph underneath exists because the shape of the stutter (periodic spikes vs. a
/// sustained plateau) narrows the cause faster than any single scalar can.
/// </summary>
public partial class StatsOverlay : PanelContainer
{
    /// <summary>~4 s of history at 60 FPS. Long enough to catch a periodic hitch, short enough that
    /// walking into a busy parcel shows up immediately instead of being averaged away.</summary>
    private const int HistoryFrames = 240;

    /// <summary>Text refresh rate. Every frame would make the digits unreadable and add its own
    /// (small) cost to the thing being measured; 5 Hz still reacts instantly to the eye.</summary>
    private const double TextRefreshSeconds = 0.2;

    /// <summary>How often the same figures are written to the log. Independent of whether the panel
    /// is shown: the panel answers "is it bad right now", the log line answers "what was it doing
    /// when it hitched", and the sessions worth diagnosing are exactly the ones nobody opened the
    /// panel for. 5 s keeps a long session to a few hundred lines instead of drowning the asset
    /// logging around it.</summary>
    private const double LogIntervalSeconds = 5.0;

    private const float GraphHeight = 46f;
    private const float GoodMs = 1000f / 60f;
    private const float BadMs = 1000f / 30f;

    private static readonly Color Good = new(0.5f, 0.95f, 0.6f);
    private static readonly Color Caution = new(1f, 0.82f, 0.35f);
    private static readonly Color Warn = new(1f, 0.45f, 0.4f);
    private static readonly Color Neutral = new(0.92f, 0.94f, 0.96f);

    private static readonly string[] RowKeys =
        { "FPS", "Frame", "Spike", "CPU", "Queue", "Draw", "VRAM", "Memory", "GC", "Nodes", "VSync" };

    // Ring buffer: _frameHead is where the NEXT sample goes, so once it has wrapped the oldest sample
    // also lives at _frameHead. Ordering matters for the graph, not just the statistics.
    private readonly double[] _frameMs = new double[HistoryFrames];
    private readonly double[] _ordered = new double[HistoryFrames];
    private readonly double[] _sortScratch = new double[HistoryFrames];
    private int _frameCount;
    private int _frameHead;

    private readonly Dictionary<string, Label> _values = new();
    private FrameGraph _graph = null!;
    private double _textAccum;
    private double _logAccum;

    // Worst frame and hitch count since the last log line, rather than since the last 4 s window: the
    // window would let a spike vanish before it was ever written down.
    private double _worstSinceLog;
    private int _hitchesSinceLog;
    private double _secondsSinceLog;

    // Last values computed by Refresh (5 Hz), reused by the log line so it costs no extra work.
    private double _lastFps, _lastLowFps, _lastMean, _lastMedian, _lastP99;
    private double _lastProcessMs, _lastDrawCalls, _lastPrimitives, _lastVideoMb, _lastManagedMb, _lastNodes;
    private int _lastQueueDepth, _lastQueuePeak;

    // GC counters are cumulative since process start, so a rate needs deltas over a known interval
    // rather than the raw values.
    private int _gc0, _gc1, _gc2;
    private double _gcAccum;
    private double _gc0Rate, _gc1Rate;

    public override void _Ready()
    {
        Name = "StatsOverlay";
        // On by default while FEAT-PERF-01 is open. The stutter investigation kept losing rounds of
        // data because the panel was closed, and a closed panel used to mean no [Perf]/[WorkCost]
        // lines either -- a whole session with a 12 s freeze in it produced no cost table at all.
        // Ctrl+Shift+1 still hides it.
        Visible = true;
        MouseFilter = MouseFilterEnum.Ignore;

        // Top-left, tucked under the TopMenu bar so it never covers the menus.
        SetAnchorsPreset(LayoutPreset.TopLeft);
        OffsetLeft = 12;
        OffsetTop = 52;

        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.06f, 0.09f, 0.82f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderColor = new Color(0.15f, 0.6f, 0.9f, 0.35f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
        });

        var root = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        root.AddThemeConstantOverride("separation", 4);
        AddChild(root);

        var title = new Label
        {
            Text = "PERFORMANCE   ·   Ctrl+Shift+1",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        title.AddThemeColorOverride("font_color", new Color(0.4f, 0.8f, 1f, 0.75f));
        title.AddThemeFontSizeOverride("font_size", 11);
        root.AddChild(title);

        var grid = new GridContainer { Columns = 2, MouseFilter = MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 2);
        root.AddChild(grid);

        foreach (string key in RowKeys) AddRow(grid, key);

        _graph = new FrameGraph
        {
            CustomMinimumSize = new Vector2(280, GraphHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        root.AddChild(_graph);

        Scale = new Vector2(SLNGWindow.GlobalUiScale, SLNGWindow.GlobalUiScale);
        SLNGWindow.GlobalUiScaleChanged += OnGlobalUiScaleChanged;

        _gc0 = GC.CollectionCount(0);
        _gc1 = GC.CollectionCount(1);
        _gc2 = GC.CollectionCount(2);
    }

    public override void _ExitTree() => SLNGWindow.GlobalUiScaleChanged -= OnGlobalUiScaleChanged;

    private void OnGlobalUiScaleChanged(float scale) => Scale = new Vector2(scale, scale);

    private void AddRow(GridContainer grid, string key)
    {
        var name = new Label { Text = key, MouseFilter = MouseFilterEnum.Ignore };
        name.AddThemeColorOverride("font_color", new Color(0.62f, 0.72f, 0.82f));
        name.AddThemeFontSizeOverride("font_size", 13);
        grid.AddChild(name);

        var value = new Label { Text = "—", MouseFilter = MouseFilterEnum.Ignore };
        value.AddThemeColorOverride("font_color", Neutral);
        value.AddThemeFontSizeOverride("font_size", 13);
        grid.AddChild(value);

        _values[key] = value;
    }

    /// <summary>Shows or hides the panel. History is deliberately NOT reset on show any more: the
    /// samples kept accumulating while it was hidden, so what appears is the real recent past rather
    /// than an empty graph that has to fill up again.</summary>
    public void Toggle()
    {
        Visible = !Visible;
        if (Visible) _textAccum = TextRefreshSeconds; // repaint on the next frame, not in 200 ms
    }

    public override void _Process(double delta)
    {
        // Sampling and logging run whether or not the panel is shown. Tying them to visibility was a
        // mistake: hiding a readout should stop it taking up screen space, not stop it recording --
        // and the sessions worth diagnosing are exactly the ones where nobody thought to open it
        // first. Only the label and graph updates below are skipped while hidden.
        _frameMs[_frameHead] = delta * 1000.0;
        _frameHead = (_frameHead + 1) % HistoryFrames;
        if (_frameCount < HistoryFrames) _frameCount++;

        _gcAccum += delta;
        if (_gcAccum >= 1.0)
        {
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1);
            _gc0Rate = (g0 - _gc0) / _gcAccum;
            _gc1Rate = (g1 - _gc1) / _gcAccum;
            _gc0 = g0;
            _gc1 = g1;
            _gc2 = GC.CollectionCount(2);
            _gcAccum = 0;
        }

        // Worst frame and hitch count are accumulated per FRAME, not read off the 4 s window at log
        // time: at a 5 s log interval the window would have already forgotten a spike that happened
        // early in the interval, which is exactly the spike worth reporting.
        double ms = delta * 1000.0;
        if (ms > _worstSinceLog) _worstSinceLog = ms;
        if (_lastMedian > 0 && ms > Math.Max(_lastMedian * 2.0, GoodMs)) _hitchesSinceLog++;
        _secondsSinceLog += delta;

        _textAccum += delta;
        if (_textAccum >= TextRefreshSeconds)
        {
            _textAccum = 0;
            Refresh();
        }

        _logAccum += delta;
        if (_logAccum >= LogIntervalSeconds)
        {
            _logAccum = 0;
            EmitLogLine();
        }
    }

    /// <summary>One grep-able line per interval, so a stutter report can be read back from the log
    /// afterwards instead of relying on someone catching the number on screen. Deliberately a single
    /// line with fixed keys: the useful operation on it is <c>grep "\[Perf\]"</c> across a whole
    /// session and reading down the columns.</summary>
    private void EmitLogLine()
    {
        if (_frameCount == 0) return;

        double hitchesPerSec = _secondsSinceLog > 0 ? _hitchesSinceLog / _secondsSinceLog : 0;

        Logger.Info(
            $"[Perf] fps={_lastFps:F0} low1%={_lastLowFps:F0} " +
            $"meanMs={_lastMean:F1} medMs={_lastMedian:F1} p99Ms={_lastP99:F1} " +
            $"worstMs={_worstSinceLog:F0} hitches={_hitchesSinceLog} ({hitchesPerSec:F1}/s) " +
            $"processMs={_lastProcessMs:F1} draws={_lastDrawCalls:F0} tris={_lastPrimitives / 1000.0:F0}k " +
            $"vramMB={_lastVideoMb:F0} csMB={_lastManagedMb:F0} gc0ps={_gc0Rate:F0} gc1ps={_gc1Rate:F0} " +
            $"queue={_lastQueueDepth} queuePeak={_lastQueuePeak} " +
            $"nodes={_lastNodes:F0} vsync={DisplayServer.WindowGetVsyncMode()}");

        // Immediately after the [Perf] line, so a session log reads as: what the frame looked like,
        // then what the main thread actually spent that frame's time on.
        MainThreadWorkQueue.ReportCosts();

        _worstSinceLog = 0;
        _hitchesSinceLog = 0;
        _secondsSinceLog = 0;
    }

    private void Refresh()
    {
        int n = _frameCount;
        if (n == 0) return;

        // Oldest-first copy out of the ring, so the graph reads left-to-right in real time order.
        int start = n < HistoryFrames ? 0 : _frameHead;
        for (int i = 0; i < n; i++) _ordered[i] = _frameMs[(start + i) % HistoryFrames];

        Array.Copy(_ordered, _sortScratch, n);
        Array.Sort(_sortScratch, 0, n);

        double median = _sortScratch[n / 2];
        double p99 = _sortScratch[Math.Min(n - 1, (int)(n * 0.99))];
        double worst = _sortScratch[n - 1];

        double sum = 0;
        for (int i = 0; i < n; i++) sum += _ordered[i];
        double mean = sum / n;

        // "1% low" in the sense the benchmarking world uses it: the frame rate you would get if every
        // frame were as slow as the worst 1%. That is the number that tracks what the eye actually
        // calls lag.
        double lowFps = p99 > 0 ? 1000.0 / p99 : 0;
        double fps = mean > 0 ? 1000.0 / mean : 0;

        // A hitch is a frame that took more than twice the local median -- relative rather than a
        // fixed ms threshold, so it still means "a visible jolt" on a machine that runs at 30 FPS.
        double hitchThreshold = Math.Max(median * 2.0, GoodMs);
        int hitches = 0;
        for (int i = 0; i < n; i++) if (_ordered[i] > hitchThreshold) hitches++;
        double windowSeconds = sum / 1000.0;
        double hitchesPerSec = windowSeconds > 0 ? hitches / windowSeconds : 0;

        _lastFps = fps; _lastLowFps = lowFps; _lastMean = mean; _lastMedian = median; _lastP99 = p99;

        SetValue("FPS", $"{fps:F0}    1% low {lowFps:F0}", ColorForMs(p99));
        SetValue("Frame", $"{mean:F1} ms   med {median:F1}   p99 {p99:F1}", ColorForMs(median));
        SetValue("Spike", $"worst {worst:F0} ms   ·   {hitchesPerSec:F1} hitches/s",
                 hitchesPerSec >= 1.0 ? Warn : hitchesPerSec > 0 ? Caution : Good);

        double processMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
        double physicsMs = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
        // Main-thread C# eating most of the frame is the signature of decode/upload work that escaped
        // a worker thread -- the one thing AGENTS.md says must never happen.
        _lastProcessMs = processMs;
        SetValue("CPU", $"process {processMs:F1} ms   ·   physics {physicsMs:F1} ms",
                 processMs > median * 0.6 ? Warn : Good);

        // Backlog in the budgeted main-thread queue. A peak that spikes and drains back to 0 is the
        // budget working as intended. A depth that never returns to 0 means the total cost of the
        // queued work exceeds what the budget can retire -- a throughput problem that no budget can
        // fix, which is what the [WorkCost] log lines break down.
        (_lastQueueDepth, _lastQueuePeak) = MainThreadWorkQueue.TakeStats();
        SetValue("Queue", $"{_lastQueueDepth} waiting   ·   peak {_lastQueuePeak}",
                 _lastQueueDepth > 400 ? Warn : _lastQueueDepth > 0 ? Caution : Good);

        double drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        double primitives = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
        double objects = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
        _lastDrawCalls = drawCalls; _lastPrimitives = primitives;
        SetValue("Draw", $"{drawCalls:F0} calls   ·   {primitives / 1_000_000.0:F2}M tris   ·   {objects:F0} obj",
                 drawCalls > 5000 ? Warn : drawCalls > 2500 ? Caution : Good);

        double videoMb = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0);
        double texMb = Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / (1024.0 * 1024.0);
        double bufMb = Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed) / (1024.0 * 1024.0);
        _lastVideoMb = videoMb;
        SetValue("VRAM", $"{videoMb:F0} MB   (tex {texMb:F0} · buf {bufMb:F0})", Neutral);

        double godotMb = Performance.GetMonitor(Performance.Monitor.MemoryStatic) / (1024.0 * 1024.0);
        double managedMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        _lastManagedMb = managedMb;
        SetValue("Memory", $"C# {managedMb:F0} MB   ·   engine {godotMb:F0} MB", Neutral);

        SetValue("GC", $"gen0 {_gc0Rate:F0}/s   gen1 {_gc1Rate:F0}/s   gen2 {_gc2}",
                 _gc1Rate >= 2 ? Warn : _gc0Rate >= 20 ? Caution : Good);

        double nodes = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);
        double orphans = Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount);
        _lastNodes = nodes;
        SetValue("Nodes", $"{nodes:F0}   ·   orphan {orphans:F0}", orphans > 0 ? Caution : Neutral);

        // V-Sync caps the frame rate to the refresh interval, so a steady "60" with vsync on can hide
        // real headroom loss -- worth stating outright next to the FPS it constrains.
        SetValue("VSync", DisplayServer.WindowGetVsyncMode().ToString(), Neutral);

        if (Visible) _graph.Update(_ordered, n, median);
    }

    private static Color ColorForMs(double ms) => ms <= GoodMs * 1.1 ? Good : ms <= BadMs ? Caution : Warn;

    private void SetValue(string key, string text, Color color)
    {
        if (!Visible) return; // stats still computed while hidden; only the UI write is pointless
        if (!_values.TryGetValue(key, out var label)) return;
        label.Text = text;
        label.AddThemeColorOverride("font_color", color);
    }

    /// <summary>
    /// The frame-time history as one bar per frame, oldest on the left, with reference lines at 60
    /// and 30 FPS. Scaled to the worst frame in view rather than a fixed ceiling so a spike is always
    /// visible instead of clipping off the top.
    /// </summary>
    private sealed partial class FrameGraph : Control
    {
        private double[] _samples = Array.Empty<double>();
        private int _count;
        private double _median;

        /// <summary>Borrows the caller's buffer instead of copying: it is only read inside
        /// <see cref="_Draw"/>, which runs on the same (main) thread, and the alternative is a
        /// per-refresh allocation in exactly the panel that reports GC churn.</summary>
        public void Update(double[] samples, int count, double median)
        {
            _samples = samples;
            _count = count;
            _median = median;
            QueueRedraw();
        }

        public override void _Draw()
        {
            var size = Size;
            DrawRect(new Rect2(Vector2.Zero, size), new Color(0, 0, 0, 0.35f));
            if (_count == 0) return;

            double peak = Math.Max(BadMs * 1.2, _median * 3.0);
            for (int i = 0; i < _count; i++) peak = Math.Max(peak, _samples[i]);

            float goodY = YFor(GoodMs, peak, size.Y);
            float badY = YFor(BadMs, peak, size.Y);
            DrawLine(new Vector2(0, goodY), new Vector2(size.X, goodY), new Color(0.5f, 0.95f, 0.6f, 0.3f));
            DrawLine(new Vector2(0, badY), new Vector2(size.X, badY), new Color(1f, 0.45f, 0.4f, 0.3f));

            float barWidth = size.X / _count;
            for (int i = 0; i < _count; i++)
            {
                double ms = _samples[i];
                float y = YFor(ms, peak, size.Y);
                var color = ColorForMs(ms);
                color.A = 0.85f;
                DrawRect(new Rect2(i * barWidth, y, Math.Max(1f, barWidth), size.Y - y), color);
            }
        }

        private static float YFor(double ms, double peak, float height)
            => height - (float)(Math.Clamp(ms / peak, 0, 1) * height);
    }
}
