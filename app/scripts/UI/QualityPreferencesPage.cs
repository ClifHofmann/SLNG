using System;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// "Quality / Hardware" tab content for PreferencesWindow.
/// </summary>
public partial class QualityPreferencesPage : VBoxContainer
{
    private GraphicsSettings _settings = null!;
    private Action _apply = null!;
    private Label _drawDistanceValue = null!;
    private Label _textureMemValue = null!;

    // Kept so Refresh can push changed values back into the controls.
    private OptionButton _vsyncOption = null!;
    private OptionButton _fpsOption = null!;
    private HSlider _drawSlider = null!;
    private HSlider _textureMemSlider = null!;
    private OptionButton _msaaOption = null!;
    private OptionButton _shadowResOption = null!;
    private OptionButton _shadowSplitsOption = null!;
    private CheckButton _smallShadowsToggle = null!;

    private bool _refreshing;

    private static readonly int[] FpsChoices = { 0, 30, 60, 90, 120, 144, 240 };
    private static readonly int[] ShadowResChoices = { 1024, 2048, 4096 };

    // FEAT-PERF-04: reads the GpuCache's current byte count for the live "used" readout. Null
    // until a session exists; evaluated each frame so it picks the cache up once it's created.
    private Func<long>? _cacheBytes;
    private Label _textureMemUsage = null!;
    private double _usageRefreshAccum;

    public override void _Ready() => AddThemeConstantOverride("separation", 8);

    public void Initialize(GraphicsSettings settings, Action apply, Func<long>? cacheBytes = null)
    {
        _settings = settings;
        _apply = apply;
        _cacheBytes = cacheBytes;

        AddHeading(L10n.Tr("ui.preferences.graphics_heading"));

        // --- V-Sync -------------------------------------------------------------------------
        AddRow(L10n.Tr("ui.preferences.vsync"), BuildOption(
            new[]
            {
                L10n.Tr("ui.preferences.vsync_off"),
                L10n.Tr("ui.preferences.vsync_on"),
                L10n.Tr("ui.preferences.vsync_adaptive"),
                L10n.Tr("ui.preferences.vsync_mailbox"),
            },
            _settings.VSyncMode,
            index => { if (!_refreshing) { _settings.SetVSyncMode(index); _apply(); } },
            out _vsyncOption));

        AddHint(L10n.Tr("ui.preferences.vsync_hint"));

        // --- Frame cap ----------------------------------------------------------------------
        var fpsLabels = new string[FpsChoices.Length];
        fpsLabels[0] = L10n.Tr("ui.preferences.fps_unlimited");
        for (int i = 1; i < FpsChoices.Length; i++) fpsLabels[i] = FpsChoices[i].ToString();

        int fpsIndex = Array.IndexOf(FpsChoices, _settings.MaxFps);
        AddRow(L10n.Tr("ui.preferences.fps_limit"), BuildOption(
            fpsLabels,
            fpsIndex < 0 ? 0 : fpsIndex,
            index => { if (!_refreshing) { _settings.SetMaxFps(FpsChoices[index]); _apply(); } },
            out _fpsOption));

        AddChild(new HSeparator());

        // --- Draw distance ------------------------------------------------------------------
        AddHeading(L10n.Tr("ui.preferences.draw_distance_heading"));

        var drawRow = new HBoxContainer();
        drawRow.AddThemeConstantOverride("separation", 12);
        AddChild(drawRow);

        _drawSlider = new HSlider
        {
            MinValue = 32,
            MaxValue = 512,
            Step = 16,
            Value = _settings.DrawDistance,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(200, 0),
        };
        drawRow.AddChild(_drawSlider);

        _drawDistanceValue = new Label
        {
            Text = $"{_settings.DrawDistance:0} m",
            CustomMinimumSize = new Vector2(56, 0),
        };
        _drawDistanceValue.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        drawRow.AddChild(_drawDistanceValue);

        _drawSlider.ValueChanged += value =>
        {
            _drawDistanceValue.Text = $"{value:0} m";
            if (_refreshing) return;
            _settings.SetDrawDistance((float)value);
            _apply();
        };

        // --- Texture memory (FEAT-PERF-04) -------------------------------------------------
        AddHeading(L10n.Tr("ui.preferences.texture_memory_heading"));

        var texMemRow = new HBoxContainer();
        texMemRow.AddThemeConstantOverride("separation", 12);
        AddChild(texMemRow);

        _textureMemSlider = new HSlider
        {
            MinValue = 256,
            // No portable way to read the card's total VRAM from Godot; 8192 covers current
            // hardware and the live "GPU" readout below shows how close you actually are.
            MaxValue = 8192,
            Step = 128,
            Value = _settings.TextureMemoryMb,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(200, 0),
        };
        texMemRow.AddChild(_textureMemSlider);

        _textureMemValue = new Label
        {
            Text = $"{_settings.TextureMemoryMb} MB",
            CustomMinimumSize = new Vector2(64, 0),
        };
        _textureMemValue.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        texMemRow.AddChild(_textureMemValue);

        // Live readout under the slider (BlackDragon-style): what the cache is holding right now
        // and Godot's total video memory, so the limit can be set against real numbers.
        _textureMemUsage = new Label { Text = "" };
        _textureMemUsage.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.65f));
        _textureMemUsage.AddThemeFontSizeOverride("font_size", 12);
        AddChild(_textureMemUsage);

        _textureMemSlider.ValueChanged += value =>
        {
            _textureMemValue.Text = $"{value:0} MB";
            if (_refreshing) return;
            _settings.SetTextureMemoryMb((int)value);
            _apply();
        };

        AddChild(new HSeparator());

        // --- Quality ------------------------------------------------------------------------
        AddHeading(L10n.Tr("ui.preferences.quality_heading"));

        AddRow(L10n.Tr("ui.preferences.msaa"), BuildOption(
            new[]
            {
                L10n.Tr("ui.preferences.off"),
                "2x", "4x", "8x",
            },
            _settings.Msaa,
            index => { if (!_refreshing) { _settings.SetMsaa(index); _apply(); } },
            out _msaaOption));

        int resIndex = Array.IndexOf(ShadowResChoices, _settings.ShadowResolution);
        AddRow(L10n.Tr("ui.preferences.shadow_resolution"), BuildOption(
            new[]
            {
                L10n.Tr("ui.preferences.shadow_res_1024"),
                L10n.Tr("ui.preferences.shadow_res_2048"),
                L10n.Tr("ui.preferences.shadow_res_4096"),
            },
            resIndex < 0 ? 2 : resIndex,
            index => { if (!_refreshing) { _settings.SetShadowResolution(ShadowResChoices[index]); _apply(); } },
            out _shadowResOption));

        AddRow(L10n.Tr("ui.preferences.shadow_splits"), BuildOption(
            new[]
            {
                L10n.Tr("ui.preferences.shadow_splits_0"), // "Off (Orthogonal)"
                L10n.Tr("ui.preferences.shadow_splits_2"), // "2 Cascades"
                L10n.Tr("ui.preferences.shadow_splits_4"), // "4 Cascades"
            },
            _settings.ShadowSplits switch { 0 => 0, 1 => 1, _ => 2 },
            index => { if (!_refreshing) { _settings.SetShadowSplits(index == 2 ? 4 : index); _apply(); } },
            out _shadowSplitsOption));

        _smallShadowsToggle = new CheckButton { ButtonPressed = _settings.SmallObjectShadows };
        _smallShadowsToggle.Toggled += pressed => { if (!_refreshing) { _settings.SetSmallObjectShadows(pressed); _apply(); } };
        AddRow(L10n.Tr("ui.preferences.small_object_shadows"), _smallShadowsToggle);
    }

    public override void _Process(double delta)
    {
        // FEAT-PERF-04: refresh the live cache / GPU readout a few times a second, only while the
        // Quality tab is actually on screen.
        if (_textureMemUsage == null || !IsVisibleInTree()) return;
        _usageRefreshAccum += delta;
        if (_usageRefreshAccum < 0.25) return;
        _usageRefreshAccum = 0;

        long cacheMb = (_cacheBytes?.Invoke() ?? 0) >> 20;
        long gpuMb = (long)(Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0));
        _textureMemUsage.Text = cacheMb > 0
            ? $"Cache: {cacheMb} MB   ·   GPU gesamt: {gpuMb} MB"
            : $"GPU gesamt: {gpuMb} MB";
    }

    public void Refresh()
    {
        _refreshing = true;
        try
        {
            _vsyncOption.Select(Mathf.Clamp(_settings.VSyncMode, 0, _vsyncOption.ItemCount - 1));

            int fpsIndex = Array.IndexOf(FpsChoices, _settings.MaxFps);
            _fpsOption.Select(fpsIndex < 0 ? 0 : fpsIndex);

            _drawSlider.Value = _settings.DrawDistance;
            _drawDistanceValue.Text = $"{_settings.DrawDistance:0} m";

            _textureMemSlider.Value = _settings.TextureMemoryMb;
            _textureMemValue.Text = $"{_settings.TextureMemoryMb} MB";

            _msaaOption.Select(Mathf.Clamp(_settings.Msaa, 0, _msaaOption.ItemCount - 1));

            int resIdx = Array.IndexOf(ShadowResChoices, _settings.ShadowResolution);
            _shadowResOption.Select(resIdx < 0 ? 2 : resIdx);

            _shadowSplitsOption.Select(_settings.ShadowSplits switch { 0 => 0, 1 => 1, _ => 2 });
            _smallShadowsToggle.ButtonPressed = _settings.SmallObjectShadows;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void AddHeading(string text)
    {
        var heading = new Label { Text = text };
        heading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        AddChild(heading);
    }

    private void AddHint(string text)
    {
        var hint = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
        hint.AddThemeFontSizeOverride("font_size", 12);
        AddChild(hint);
    }

    private void AddRow(string label, Control control)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);

        var name = new Label
        {
            Text = label,
            CustomMinimumSize = new Vector2(120, 0),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.Fill,
        };
        name.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        row.AddChild(name);

        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (control is OptionButton option)
        {
            option.ClipText = true;
            option.CustomMinimumSize = new Vector2(120, 0);
        }
        row.AddChild(control);

        AddChild(row);
    }

    private static OptionButton BuildOption(string[] labels, int selected, Action<int> onSelected,
                                            out OptionButton created)
    {
        var option = new OptionButton();
        created = option;
        foreach (string label in labels) option.AddItem(label);
        option.Select(Mathf.Clamp(selected, 0, labels.Length - 1));
        option.ItemSelected += index => onSelected((int)index);
        return option;
    }
}
