using Godot;

namespace SLNG.App;

/// <summary>
/// M0-1 bootstrap entry point. Confirms the Godot .NET project builds and runs.
/// The real client bootstrap (login screen, world view) arrives in later M0
/// tasks; this only proves the toolchain is wired up.
/// </summary>
public partial class Main : Node
{
    public override void _Ready()
    {
        GD.Print($"SLNG bootstrap OK — Godot .NET project is alive (stage: pre-alpha).");

        // Headless smoke test:  godot --headless --path app -- --selftest
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--selftest")
            {
                GD.Print("selftest: pass");
                GetTree().Quit(0);
                return;
            }
        }
    }
}
