using Godot;

namespace SLNG.App;

/// <summary>
/// Drives <see cref="MainThreadWorkQueue.Pump"/> once per frame.
///
/// Its own node rather than a call inside <c>ObjectRenderer._Process</c> for two reasons: the queue
/// serves the texture cache as well as the renderer, and ObjectRenderer's _Process returns early on
/// a 4 Hz cull throttle -- draining from there would have quietly capped the queue at four frames'
/// worth of work per second.
/// </summary>
public partial class MainThreadWorkPump : Node
{
    public override void _Ready()
    {
        Name = "MainThreadWorkPump";
        // Ahead of the renderer and the HUD in the frame, so work drained here is visible in the
        // same frame it was applied rather than one frame later.
        ProcessPriority = -100;
        MainThreadWorkQueue.SetPumpActive(true);
    }

    public override void _ExitTree() => MainThreadWorkQueue.SetPumpActive(false);

    public override void _Process(double delta) => MainThreadWorkQueue.Pump(RenderConfig.MainThreadWorkBudgetMs);
}
