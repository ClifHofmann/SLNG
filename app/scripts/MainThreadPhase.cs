using System.Threading;

namespace SLNG.App;

/// <summary>
/// Names what the main thread is currently doing, so <see cref="MainThreadWatchdog"/> can say where
/// a hang happened instead of only that one happened.
///
/// The first watchdog readings established what the stall is NOT: seven stalls of 2.1 s to 12.2 s,
/// every one of them reporting <c>lastWork=(none)</c>, i.e. the main thread was not inside a queued
/// work item. That rules out the entire asset/scene pipeline this project spent its optimisation
/// effort on and leaves everything else that runs on the frame -- the renderer's own sweep, the
/// avatar and terrain nodes, the world-event drain, and Godot's internal frame work such as shader
/// compilation. Narrowing that by reasoning has already gone wrong twice in this investigation, so
/// it gets marked instead.
///
/// Usage is a using-block: <c>using (MainThreadPhase.Enter("cull")) { ... }</c>. Phases nest, and the
/// innermost one wins, because that is the more specific answer.
///
/// Costs a string write and an int per phase. Not free, but this runs a handful of times per frame,
/// not per object -- and an unattributable multi-second freeze costs more.
/// </summary>
public static class MainThreadPhase
{
    private static volatile string _current = "idle";
    private static int _depth;

    /// <summary>Read from the watchdog thread, which is the entire point: it has to be readable while
    /// the main thread is wedged and unable to report anything itself.</summary>
    public static string Current => _current;

    public static Scope Enter(string phase)
    {
        // Only the outermost caller's exit restores "idle"; nested phases restore their parent by
        // simply being overwritten on the way out.
        Interlocked.Increment(ref _depth);
        _current = phase;
        return new Scope(phase);
    }

    public readonly struct Scope : System.IDisposable
    {
        private readonly string _entered;

        internal Scope(string entered) => _entered = entered;

        public void Dispose()
        {
            // Leave the label alone if a nested phase is still running -- it is the more specific
            // answer and the one worth keeping.
            if (Interlocked.Decrement(ref _depth) <= 0)
            {
                _depth = 0;
                _current = "idle";
            }
            else if (_current == _entered)
            {
                _current = "in-frame";
            }
        }
    }
}
