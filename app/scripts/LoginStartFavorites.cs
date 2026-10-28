using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLNG.Core.Landmarks;
using SLNG.Core.Login;
using SLNG.Net;

namespace SLNG.App;

/// <summary>What <see cref="LoginStartFavorites"/> needs from a session: the Favorites folder, and what a landmark points at.</summary>
internal interface ILoginStartSource
{
    /// <summary>The Favorites folder with its subfolders fetched from the grid (see <see cref="GridSession.GetFavoritesAsync"/>).</summary>
    Task<FavoritesSnapshot> GetFavoritesAsync(CancellationToken ct);

    /// <summary>The folder as the local store holds it right now; no network.</summary>
    FavoritesSnapshot ReadFavoritesFromStore();

    /// <summary>Resolves a landmark asset to a place, or says why not.</summary>
    Task<LandmarkTargetLookup> LookupLandmarkTargetAsync(Guid landmarkAssetId, CancellationToken ct);
}

/// <summary>The real thing: a <see cref="GridSession"/>.</summary>
internal sealed class GridSessionLoginStartSource : ILoginStartSource
{
    private readonly GridSession _session;
    public GridSessionLoginStartSource(GridSession session) => _session = session;

    public Task<FavoritesSnapshot> GetFavoritesAsync(CancellationToken ct) => _session.GetFavoritesAsync(ct);
    public FavoritesSnapshot ReadFavoritesFromStore() => _session.ReadFavoritesFromStore();
    public Task<LandmarkTargetLookup> LookupLandmarkTargetAsync(Guid landmarkAssetId, CancellationToken ct)
        => _session.LookupLandmarkTargetAsync(landmarkAssetId, ct);
}

/// <summary>A snapshot of the login-screen favorites, ready to be written to <c>logins.cfg</c> by the main thread.</summary>
internal sealed record LoginStartFavoritesUpdate(IReadOnlyList<LoginFavorite> Favorites);

/// <summary>
/// FEAT-UI-72: keeps a profile's favorites cache current WHILE A SESSION RUNS, so the login screen -- which cannot read the
/// inventory -- can list them next time. It reads the Favorites folder, resolves each landmark nobody has resolved yet to a
/// region and a local position (one at a time, spaced out, so it never competes with the region loading), and publishes the
/// result as an immutable snapshot. The login screen then reads what was written; the writing is the main thread's job.
///
/// <para><b>No Godot here.</b> This runs on a worker thread. It touches no node and no <c>ConfigFile</c> (neither is
/// thread-safe) and never uses <c>Callable.From(...).CallDeferred()</c> (which crashes from a background thread): it only
/// swaps a reference that <c>Boot._Process</c> picks up with <see cref="TryTakeUpdate"/>.</para>
///
/// <para><b>An unconfirmed read never publishes.</b> A refused folder fetch reads as an empty folder, so a pass that did not
/// come back <see cref="FavoritesSnapshot.Confirmed"/> is dropped; otherwise one bad fetch would wipe the login list.</para>
/// </summary>
internal sealed class LoginStartFavorites : IDisposable
{
    /// <summary>Timings; a test shortens them.</summary>
    internal sealed record Timing(TimeSpan Debounce, TimeSpan ResolveSpacing, TimeSpan Reread, TimeSpan PublishEvery)
    {
        public static Timing Default { get; } = new(
            Debounce: TimeSpan.FromMilliseconds(1500),
            ResolveSpacing: TimeSpan.FromMilliseconds(250),
            Reread: TimeSpan.FromMinutes(5),
            PublishEvery: TimeSpan.FromSeconds(2));
    }

    private readonly ILoginStartSource _source;
    private readonly Timing _timing;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _gate = new();

    // Guarded by _gate.
    private readonly Dictionary<Guid, StartTarget> _known = new();
    private readonly HashSet<Guid> _failed = new();

    private LoginStartFavoritesUpdate? _pending;     // swapped with Interlocked.Exchange
    private string _lastPublishedJson = "\0";        // "\0" = nothing published and nothing on disk yet; guarded by _publishGate
    private int _started;
    private int _disposed;
    private bool _unconfirmedLogged;

    /// <param name="source">Where the folder and the landmarks come from.</param>
    /// <param name="cached">What the profile already has on disk; those targets are reused, never resolved again.</param>
    /// <param name="populated">True when the profile's favorites key exists on disk (even holding an empty list). When it does
    /// not, the first confirmed pass is published even if it finds nothing, because that is what records "this folder was
    /// looked at and is empty" and stops the login screen promising favorites after the next login.</param>
    public LoginStartFavorites(ILoginStartSource source, IReadOnlyList<LoginFavorite> cached, bool populated, Timing? timing = null)
    {
        _source = source;
        _timing = timing ?? Timing.Default;
        foreach (var f in cached) _known.TryAdd(f.AssetId, f.Target);
        // What is on disk is already published; do not write it back unchanged.
        if (populated) _lastPublishedJson = LoginFavoriteBook.ToJson(cached);
    }

    /// <summary>Asks for a pass (debounced). The first call starts the worker. Safe from any thread.</summary>
    public void Poke()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _started, 1) == 0)
            _ = Task.Run(() => RunAsync(_cts.Token));
        _signal.Release();
    }

    /// <summary>Takes the newest unwritten snapshot, if there is one. Call from the main thread, then write it.</summary>
    public bool TryTakeUpdate(out LoginStartFavoritesUpdate update)
    {
        var taken = Interlocked.Exchange(ref _pending, null);
        update = taken!;
        return taken != null;
    }

    /// <summary>
    /// Brings the snapshot up to date from the local store -- no network, nothing awaited -- and takes it. For the moment
    /// just before a logout, so a favorite added or renamed during the session is covered even if the worker has not got to
    /// it. Does nothing (apart from handing back what the worker already published) when the store cannot vouch for the folder.
    /// </summary>
    public bool TryFlush(out LoginStartFavoritesUpdate update)
    {
        var snapshot = _source.ReadFavoritesFromStore();
        if (snapshot.Confirmed)
            Publish(Reconcile(snapshot.Items).Favorites);
        return TryTakeUpdate(out update);
    }

    /// <summary>
    /// The last write before a logout, WITH the landmarks nobody has resolved yet. <see cref="TryFlush"/> leaves those out, which
    /// is why a favorite added shortly before logging out was missing from the login list: it had not been resolved to a region
    /// and a position, and resolving needs the network. The session is still connected at this point, so each unresolved
    /// landmark is looked up (a few hundred milliseconds apiece) within <paramref name="budget"/>; whatever did not finish is
    /// left out and picked up by the next session. Runs on a worker thread like the rest of this class.
    /// </summary>
    public async Task<LoginStartFavoritesUpdate?> FlushAsync(TimeSpan budget)
    {
        var snapshot = _source.ReadFavoritesFromStore();
        if (snapshot.Confirmed)
        {
            var reconciled = Reconcile(snapshot.Items);
            Publish(reconciled.Favorites);

            if (reconciled.Unresolved.Count > 0)
            {
                using var cts = new CancellationTokenSource(budget);
                try
                {
                    foreach (var item in reconciled.Unresolved)
                    {
                        if (item.AssetId == Guid.Empty) continue;
                        lock (_gate)
                        {
                            if (_failed.Contains(item.AssetId)) continue;
                        }

                        var lookup = await _source.LookupLandmarkTargetAsync(item.AssetId, cts.Token).ConfigureAwait(false);
                        lock (_gate)
                        {
                            if (lookup.Target != null) _known[item.AssetId] = lookup.Target;
                            else _failed.Add(item.AssetId);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // out of time: what was resolved so far is published below
                }
                Publish(Reconcile(snapshot.Items).Favorites);
            }
        }
        return TryTakeUpdate(out var update) ? update : null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _signal.Release();
    }

    // ---- the worker -----------------------------------------------------------------------------------------------

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            bool first = true;
            while (!ct.IsCancellationRequested)
            {
                // The first pass runs on the poke that started the worker; after that, on the next poke or the periodic re-read
                // (a favorite added in another viewer reaches the local store without any event of its own).
                if (!first) await _signal.WaitAsync(_timing.Reread, ct).ConfigureAwait(false);
                first = false;

                // Debounce: pokes arriving in a burst make one pass.
                while (await _signal.WaitAsync(_timing.Debounce, ct).ConfigureAwait(false)) { }

                await PassAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // the session ended
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LoginStart] the favorites refresh stopped: {ex.Message}");
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        var snapshot = await _source.GetFavoritesAsync(ct).ConfigureAwait(false);
        if (!snapshot.Confirmed)
        {
            if (!_unconfirmedLogged)
            {
                _unconfirmedLogged = true;
                Console.Error.WriteLine("[LoginStart] the Favorites folder could not be confirmed; the login list is left as it was");
            }
            return;
        }

        var reconciled = Reconcile(snapshot.Items);
        Publish(reconciled.Favorites);

        var lastPublish = DateTime.UtcNow;
        foreach (var item in reconciled.Unresolved)
        {
            ct.ThrowIfCancellationRequested();
            if (item.AssetId == Guid.Empty) continue;
            lock (_gate)
            {
                if (_failed.Contains(item.AssetId)) continue;
            }

            var lookup = await _source.LookupLandmarkTargetAsync(item.AssetId, ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (lookup.Target != null) _known[item.AssetId] = lookup.Target;
                else _failed.Add(item.AssetId);
            }
            if (lookup.Target == null)
                Console.Error.WriteLine($"[LoginStart] could not resolve '{item.Name}': {lookup.Reason}");

            if (DateTime.UtcNow - lastPublish >= _timing.PublishEvery)
            {
                lastPublish = DateTime.UtcNow;
                Publish(Reconcile(snapshot.Items).Favorites);
            }

            await Task.Delay(_timing.ResolveSpacing, ct).ConfigureAwait(false);
        }

        Publish(Reconcile(snapshot.Items).Favorites);
    }

    /// <summary>The folder's items in the viewer's default order, matched against what has been resolved.</summary>
    private ReconcileResult Reconcile(IReadOnlyList<LandmarkInventoryItem> items)
    {
        // Until the bar becomes the Favorites folder (phase 2) there is no stored order: the viewer's own default, newest first.
        var ordered = FavoritesOrder.Sort(items, Array.Empty<Guid>());
        LoginFavorite[] known;
        lock (_gate)
        {
            var list = new List<LoginFavorite>(_known.Count);
            foreach (var (asset, target) in _known)
                list.Add(new LoginFavorite(asset, Guid.Empty, "", target));
            known = list.ToArray();
        }
        return LoginFavoriteBook.Reconcile(ordered, known);
    }

    private readonly object _publishGate = new();

    /// <summary>Makes <paramref name="favorites"/> the snapshot the main thread will take, unless it is what was last published.</summary>
    private void Publish(IReadOnlyList<LoginFavorite> favorites)
    {
        string json = LoginFavoriteBook.ToJson(favorites);
        lock (_publishGate)
        {
            if (json == _lastPublishedJson) return;
            _lastPublishedJson = json;
        }
        Interlocked.Exchange(ref _pending, new LoginStartFavoritesUpdate(favorites));
    }
}
