using LibreMetaverse;
using SLNG.Core;

namespace SLNG.Net;

// GridSession, seed-capability recovery. BUG-NET-31.
//
// LibreMetaverse 3.1.6 retries a failed seed request by calling itself with no delay and no limit,
// after cancelling the very token the retry then uses, so every retry fails at once and nests one
// more stack frame until the process dies ("Stack overflow." after ~780 lines of "Seed capability
// returned no response. Trying again."). SeedCapabilityGuard has the whole account; this file is the
// part that knows about the GridClient.
public sealed partial class GridSession
{
    private SeedCapabilityGuard? _seedGuard;

    internal SeedCapabilityGuard? SeedGuard => _seedGuard;

    private void StartSeedCapabilityGuard()
    {
        LastExceptionTracker.Start();
        _seedGuard = new SeedCapabilityGuard(
            TakeSeedSnapshot,
            ReseedUnansweredRegions,
            Task.Delay,
            Console.Error.WriteLine,
            EndSessionOverUnavailableCapabilities);
    }

    private void StopSeedCapabilityGuard()
    {
        _seedGuard?.Cancel();
        LastExceptionTracker.Stop();
    }

    /// <summary>Called from the LibreMetaverse log filter for the library's "Trying again" line. Runs
    /// ON the library's failure path, before its retry, and throws to end it.</summary>
    private void AbortLibreMetaverseSeedRetry()
    {
        _seedGuard?.OnSeedFailure(LastExceptionTracker.DescribeLast());
        throw new SeedRequestAbortedException();
    }

    private static bool IsUnseeded(Simulator sim)
    {
        var caps = sim.Caps;
        return caps != null && caps.Capabilities().Count == 0;
    }

    private Simulator[] SnapshotSimulators()
    {
        // The library's list is changed from its own threads; a copy that races is retried once and
        // then given up on -- this only feeds a recovery that runs again in seconds.
        for (int i = 0; i < 2; i++)
        {
            try { return _client.Network.Simulators.ToArray(); }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
        }
        return Array.Empty<Simulator>();
    }

    private SeedSnapshot TakeSeedSnapshot()
    {
        var net = _client.Network;
        var current = net.CurrentSim;
        if (!net.Connected || current == null) return new SeedSnapshot(false, false, 0);

        int others = 0;
        foreach (var sim in SnapshotSimulators())
            if (sim != current && sim.Connected && IsUnseeded(sim)) others++;
        return new SeedSnapshot(true, IsUnseeded(current), others);
    }

    /// <summary>Gives every region whose seed request produced nothing a fresh capability system.
    /// <c>SetSeedCaps</c> with <c>changedSim</c> disconnects the old one (cancelling whatever it was
    /// waiting on) and starts a new request with a new cancellation token -- the one thing the
    /// library's own retry never has.</summary>
    private void ReseedUnansweredRegions()
    {
        var current = _client.Network.CurrentSim;
        foreach (var sim in SnapshotSimulators())
        {
            if (!sim.Connected || !IsUnseeded(sim)) continue;
            var seed = sim.Caps?.SeedCapsURI;
            if (seed == null) continue;
            sim.SetSeedCaps(seed, changedSim: true);
            Console.Error.WriteLine(
                $"[Caps] re-requested the seed capability for {(sim == current ? "the current region" : "a neighbouring region")} " +
                $"{sim.Name} (host {seed.Host}).");
        }
    }

    /// <summary>The current region's capabilities never arrived, so the session is not worth keeping:
    /// no event queue, no inventory, no HTTP textures. Same shape as
    /// <see cref="EndSessionOverDeadEventQueue"/> -- tell the client first, log out for the grid's
    /// sake second, off the thread that noticed.</summary>
    private void EndSessionOverUnavailableCapabilities()
    {
        RaiseSessionEnded(SessionEndReason.CapabilitiesUnavailable, CurrentRegionName);

        Task.Run(() =>
        {
            try
            {
                if (_client.Network.Connected) _client.Network.Logout();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Net] logout after the seed capability gave up failed: {ex.Message}");
            }
        });
    }
}
