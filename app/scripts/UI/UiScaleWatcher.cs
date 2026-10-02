using Godot;

namespace SLNG.App.UI;

/// <summary>
/// Re-reads the display's scale about once a second so an AUTOMATIC UI scale follows the window to
/// another screen, or the user changing the Windows scale while the viewer runs (FEAT-UI-42).
/// <para>Polled rather than event-driven on purpose: Window.dpi_changed is not something to rely
/// on for Windows (the engine documents it for macOS), and a poll needs no per-platform knowledge.
/// It is a few cheap engine calls a second, and does nothing unless the answer changed AND the scale
/// is automatic. A scale the user chose by hand is never touched.</para>
/// </summary>
public partial class UiScaleWatcher : Node
{
    private const double PollSeconds = 1.0;

    private readonly UiSettings _settings;
    private double _accumulated;

    public UiScaleWatcher(UiSettings settings)
    {
        _settings = settings;
        Name = "UiScaleWatcher";
    }

    public override void _Process(double delta)
    {
        _accumulated += delta;
        if (_accumulated < PollSeconds) return;
        _accumulated = 0;
        _settings.OnDisplayChanged();
    }
}
