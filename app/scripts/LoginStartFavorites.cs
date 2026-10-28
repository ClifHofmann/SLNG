using System;
using System.Collections.Generic;
using System.Linq;
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
internal sealed record LoginStartFavoritesUpdate(IReadOnlyList<LoginFavorite> Favorites)
{
    /// <summary>FEAT-UI-77: favorites whose region did not answer; listed greyed out on the login screen.</summary>
    public IReadOnlyList<LoginOfflineFavorite> Offline { get; init; } = Array.Empty<LoginOfflineFavorite>();
}

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
    internal sealed record Timing(TimeSpan Debounce, TimeSpan ResolveSpacing, TimeSpan Reread, TimeSpan PublishEvery,
        TimeSpan? OfflineRecheck = null)
    {
        /// <summary>How long a region that did not answer is believed to be down before it is asked again.</summary>
        public TimeSpan Recheck => OfflineRecheck ?? TimeSpan.FromMinutes(5);

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
    private readonly Dictionary<Guid, DateTime> _offline = new();   // asset -> when its region last failed to answer
    private int _offlineVersion;
    private int _offlineTaken;

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
    /// <param name="cachedOffline">The favorites the disk cache lists as offline; they stay listed until a lookup says otherwise.</param>
    public LoginStartFavorites(ILoginStartSource source, IReadOnlyList<LoginFavorite> cached, bool populated, Timing? timing = null,
        IReadOnlyList<LoginOfflineFavorite>? cachedOffline = null)
    {
        _source = source;
        _timing = timing ?? Timing.Default;
        foreach (var f in cached) _known.TryAdd(f.AssetId, f.Target);
        // Believed down until asked again, which the first pass does (the stamp is long past).
        if (cachedOffline != null)
            foreach (var o in cachedOffline) _offline.TryAdd(o.AssetId, DateTime.MinValue);
        // What is on disk is already published; do not write it back unchanged.
        if (populated) _lastPublishedJson = LoginFavoriteBook.ToJson(cached, cachedOffline);
    }

    /// <summary>
    /// The set of favorites whose region did not answer, when it changed since the last call (main thread; for the favorites
    /// bar, which greys them out). False when nothing changed.
    /// </summary>
    public bool TryTakeOfflineChange(out IReadOnlyCollection<Guid> offlineAssets)
    {
        lock (_gate)
        {
            if (_offlineVersion == _offlineTaken)
            {
                offlineAssets = Array.Empty<Guid>();
                return false;
            }
            _offlineTaken = _offlineVersion;
            offlineAssets = _offline.Keys.ToArray();
            return true;
        }
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
            PublishState(snapshot.Items);
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
        if (!snapshot.Confirmed)
            Console.WriteLine("[LoginStart] flush: the local store does not vouch for the Favorites folder; nothing to add");
        if (snapshot.Confirmed)
        {
            var reconciled = Reconcile(snapshot.Items);
            PublishState(snapshot.Items);
            var toProbe = reconciled.Unresolved.Where(ShouldProbe).ToList();
            Console.WriteLine($"[LoginStart] flush: {snapshot.Items.Count} favorite(s) in the folder, {reconciled.Favorites.Count} resolved, {toProbe.Count} to resolve now");

            if (toProbe.Count > 0)
            {
                using var cts = new CancellationTokenSource(budget);

                // All at once, not one after another: several favorites added shortly before the logout must share the budget,
                // not queue up behind the first one's wait.
                async Task ResolveOne(LandmarkInventoryItem item)
                {
                    var started = DateTime.UtcNow;
                    var lookup = await _source.LookupLandmarkTargetAsync(item.AssetId, cts.Token).ConfigureAwait(false);
                    Record(item, lookup);
                    Console.WriteLine(lookup.Target != null
                        ? $"[LoginStart] flush: resolved '{item.Name}' in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms"
                        : $"[LoginStart] flush: could not resolve '{item.Name}': {lookup.Reason}");
                }

                try
                {
                    await Task.WhenAll(toProbe.Select(ResolveOne)).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // out of time: what was resolved so far is published below
                    Console.WriteLine($"[LoginStart] flush: out of time ({budget.TotalSeconds:0} s); the rest is left for the next session");
                }
                PublishState(snapshot.Items);
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
        PublishState(snapshot.Items);
        var toProbe = reconciled.Unresolved.Where(ShouldProbe).ToList();
        Console.WriteLine($"[LoginStart] pass: {snapshot.Items.Count} favorite(s) in the folder, {reconciled.Favorites.Count} resolved, {toProbe.Count} to resolve");

        var lastPublish = DateTime.UtcNow;
        foreach (var item in toProbe)
        {
            ct.ThrowIfCancellationRequested();

            var started = DateTime.UtcNow;
            var lookup = await _source.LookupLandmarkTargetAsync(item.AssetId, ct).ConfigureAwait(false);
            Record(item, lookup);
            if (lookup.Target == null)
                Console.Error.WriteLine($"[LoginStart] could not resolve '{item.Name}': {lookup.Reason}");
            else
                Console.WriteLine($"[LoginStart] resolved '{item.Name}' in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");

            if (DateTime.UtcNow - lastPublish >= _timing.PublishEvery)
            {
                lastPublish = DateTime.UtcNow;
                PublishState(snapshot.Items);
            }

            await Task.Delay(_timing.ResolveSpacing, ct).ConfigureAwait(false);
        }

        PublishState(snapshot.Items);
    }

    /// <summary>Whether a landmark is worth asking about now: it has an asset, did not fail for good, and -- if its region was
    /// found not to answer -- that was long enough ago for it to be back.</summary>
    private bool ShouldProbe(LandmarkInventoryItem item)
    {
        if (item.AssetId == Guid.Empty) return false;
        lock (_gate)
        {
            if (_failed.Contains(item.AssetId)) return false;
            return !_offline.TryGetValue(item.AssetId, out var at) || DateTime.UtcNow - at >= _timing.Recheck;
        }
    }

    /// <summary>Takes what one lookup found: a place, a region that is not answering, or a failure that is not worth repeating.</summary>
    private void Record(LandmarkInventoryItem item, LandmarkTargetLookup lookup)
    {
        lock (_gate)
        {
            if (lookup.Target != null)
            {
                _known[item.AssetId] = lookup.Target;
                if (_offline.Remove(item.AssetId)) _offlineVersion++;
            }
            else if (lookup.RegionOffline)
            {
                if (!_offline.ContainsKey(item.AssetId)) _offlineVersion++;
                _offline[item.AssetId] = DateTime.UtcNow;
            }
            else
            {
                _failed.Add(item.AssetId);
            }
        }
    }

    /// <summary>Publishes the resolved favorites and, after them, the ones whose region is not answering (folder order).</summary>
    private void PublishState(IReadOnlyList<LandmarkInventoryItem> items)
    {
        var resolved = Reconcile(items).Favorites;
        var resolvedAssets = new HashSet<Guid>(resolved.Select(f => f.AssetId));
        var offline = new List<LoginOfflineFavorite>();
        lock (_gate)
        {
            foreach (var item in FavoritesOrder.Sort(items, Array.Empty<Guid>()))
            {
                if (offline.Count >= LoginFavoriteBook.MaxFavorites) break;
                if (item.AssetId == Guid.Empty || !_offline.ContainsKey(item.AssetId) || resolvedAssets.Contains(item.AssetId)) continue;
                string name = (item.Name ?? "").Trim();
                if (name.Length == 0) continue;
                if (offline.Any(o => o.AssetId == item.AssetId)) continue;
                offline.Add(new LoginOfflineFavorite(item.AssetId, item.Id, name));
            }
        }
        Publish(resolved, offline);
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
    private void Publish(IReadOnlyList<LoginFavorite> favorites, IReadOnlyList<LoginOfflineFavorite> offline)
    {
        string json = LoginFavoriteBook.ToJson(favorites, offline);
        lock (_publishGate)
        {
            if (json == _lastPublishedJson) return;
            _lastPublishedJson = json;
        }
        Interlocked.Exchange(ref _pending, new LoginStartFavoritesUpdate(favorites) { Offline = offline });
    }
}
