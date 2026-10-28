using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using SLNG.Core.Landmarks;
using SLNG.Net;

namespace SLNG.App;

/// <summary>Where <see cref="FavoritesService"/> gets the Favorites folder from and how it changes it. The grid session in
/// the client; a fake in the selftest.</summary>
internal interface IFavoritesSource
{
    /// <summary>Whether the account has a Favorites folder at all.</summary>
    bool HasFolder { get; }

    /// <summary>The folder as the local inventory store holds it now. No network.</summary>
    FavoritesSnapshot ReadFromStore();

    /// <summary>Asks the grid for the folder (and its subfolders) first, then reads it.</summary>
    Task<FavoritesSnapshot> FetchAsync(CancellationToken ct);

    Task<FavoriteAddVerdict> AddAsync(Guid itemId, CancellationToken ct);

    Task RemoveAsync(Guid itemId);

    /// <summary>Null when there is no Favorites folder; otherwise whether the landmark was created.</summary>
    Task<bool?> AddCurrentLocationAsync(string name, CancellationToken ct);

    /// <summary>The inventory changed in a way that may concern the folder. Any thread.</summary>
    event EventHandler? Changed;
}

/// <summary>The real source: the logged-in <see cref="GridSession"/>.</summary>
internal sealed class GridSessionFavoritesSource : IFavoritesSource
{
    private readonly GridSession _session;

    public GridSessionFavoritesSource(GridSession session)
    {
        _session = session;
        session.FavoritesChanged += (s, e) => Changed?.Invoke(s, e);
        session.StartFavoritesWatch();
    }

    public bool HasFolder => _session.FavoritesFolderId != null;
    public FavoritesSnapshot ReadFromStore() => _session.ReadFavoritesFromStore();
    public Task<FavoritesSnapshot> FetchAsync(CancellationToken ct) => _session.GetFavoritesAsync(ct);
    public Task<FavoriteAddVerdict> AddAsync(Guid itemId, CancellationToken ct) => _session.AddFavoriteAsync(itemId, ct);
    public Task RemoveAsync(Guid itemId) => _session.RemoveFavoriteAsync(itemId);

    public async Task<bool?> AddCurrentLocationAsync(string name, CancellationToken ct)
    {
        var result = await _session.AddCurrentLocationAsync(name, ct);
        return result?.Success;
    }

    public event EventHandler? Changed;
}

/// <summary>What the favorites bar can honestly say about the folder.</summary>
public enum FavoritesMode
{
    /// <summary>Not read from the grid yet.</summary>
    Loading,
    /// <summary>The Favorites folder, read and confirmed.</summary>
    Folder,
    /// <summary>The grid has no Favorites folder (an OpenSim without one). Nothing can be added.</summary>
    NoFolder,
    /// <summary>The folder exists but the grid did not answer for it. The bar says so and tries again on the next change.</summary>
    Unavailable,
}

/// <summary>
/// FEAT-UI-72 phase 2: the one list behind the favorites bar -- the landmarks in the inventory's Favorites folder, in
/// <see cref="FavoritesOrder"/> order -- and the three things that change it (add, remove, add the current place). The old
/// local list of the bar is gone; nothing was migrated.
///
/// <para><b>Trust.</b> The folder is believed only when the grid confirmed it (<see cref="FavoritesSnapshot.Confirmed"/>): a
/// refused fetch reads as an empty folder and a restored cache hands back an unfetched one as current and empty, so an
/// unconfirmed read never replaces what was shown. Nothing is shown from the store before the first real fetch.</para>
///
/// <para><b>Threads.</b> The source reports changes on a network thread; that only sets a time stamp. The refresh itself runs
/// from <see cref="_Process"/> on the main thread, a moment after the last change (a login fills the whole tree and would
/// otherwise cause hundreds of refreshes). Awaits here deliberately resume on the main thread.</para>
/// </summary>
public partial class FavoritesService : Node
{
    private const ulong DebounceMs = 600;
    private const ulong RefetchAfterAddMs = 3000;

    private IFavoritesSource? _source;
    private IReadOnlyList<Guid> _order = Array.Empty<Guid>();
    private Action<IReadOnlyList<Guid>>? _saveOrder;
    private List<LandmarkInventoryItem> _items = new();
    private bool _fetched;
    private bool _fetching;
    private long _dirtySinceMs; // 0 = clean; written from a network thread
    private ulong _refetchAtMs;

    /// <summary>The favorites in display order.</summary>
    public IReadOnlyList<LandmarkInventoryItem> Items => _items;

    public FavoritesMode Mode { get; private set; } = FavoritesMode.Loading;

    /// <summary>Raised on the main thread whenever <see cref="Items"/> or <see cref="Mode"/> changed.</summary>
    public event Action? Changed;

    // FEAT-UI-77: favorites whose region did not answer. Separate from Changed on purpose: Changed makes the login cache read the
    // folder again, and this is the cache's own finding.
    private HashSet<Guid> _offlineAssets = new();

    /// <summary>Raised on the main thread when the set of favorites with an offline region changed.</summary>
    public event Action? OfflineChanged;

    /// <summary>Whether the region of the landmark <paramref name="assetId"/> did not answer when it was last looked up.</summary>
    public bool IsOffline(Guid assetId) => assetId != Guid.Empty && _offlineAssets.Contains(assetId);

    /// <summary>Takes the favorites whose region is not answering (main thread). The bar greys them out.</summary>
    public void SetOfflineAssets(IReadOnlyCollection<Guid> assetIds)
    {
        var next = new HashSet<Guid>(assetIds);
        if (next.SetEquals(_offlineAssets)) return;
        _offlineAssets = next;
        OfflineChanged?.Invoke();
    }

    /// <summary>Item ids and landmark asset ids of every favorite: the "keep" hints of the duplicate cleaner.</summary>
    public HashSet<Guid> KnownIds()
    {
        var ids = new HashSet<Guid>();
        foreach (var item in _items)
        {
            ids.Add(item.Id);
            if (item.AssetId != Guid.Empty) ids.Add(item.AssetId);
        }
        return ids;
    }

    /// <summary>Just the favorites' own item ids, which the cleaner must not propose as duplicates of their originals.</summary>
    public HashSet<Guid> ItemIds() => _items.Select(i => i.Id).ToHashSet();

    internal void Initialize(IFavoritesSource source, IReadOnlyList<Guid> savedOrder, Action<IReadOnlyList<Guid>> saveOrder)
    {
        Reset();
        _source = source;
        _order = savedOrder;
        _saveOrder = saveOrder;
        source.Changed += OnSourceChanged;
    }

    /// <summary>Forgets the session (logout): the next login starts from "Loading".</summary>
    public void Reset()
    {
        if (_source != null) _source.Changed -= OnSourceChanged;
        _source = null;
        _order = Array.Empty<Guid>();
        _saveOrder = null;
        _items = new List<LandmarkInventoryItem>();
        _offlineAssets = new HashSet<Guid>();
        _fetched = false;
        _fetching = false;
        Interlocked.Exchange(ref _dirtySinceMs, 0);
        _refetchAtMs = 0;
        Mode = FavoritesMode.Loading;
        Changed?.Invoke();
    }

    /// <summary>The loading screen has cleared: read the folder from the grid (after the first world load, so it never
    /// competes with it).</summary>
    public void Poke() => _ = FetchAsync();

    private void OnSourceChanged(object? sender, EventArgs e)
        => Interlocked.CompareExchange(ref _dirtySinceMs, (long)Math.Max(1UL, Time.GetTicksMsec()), 0);

    public override void _Process(double delta)
    {
        if (_source == null) return;
        ulong now = Time.GetTicksMsec();

        long dirty = Interlocked.Read(ref _dirtySinceMs);
        if (dirty != 0 && _fetched && now - (ulong)dirty >= DebounceMs)
        {
            Interlocked.Exchange(ref _dirtySinceMs, 0);
            Apply(_source.ReadFromStore());
        }

        if (_refetchAtMs != 0 && now >= _refetchAtMs)
        {
            _refetchAtMs = 0;
            _ = FetchAsync();
        }
    }

    private async Task FetchAsync()
    {
        var source = _source;
        if (source == null || _fetching) return;
        _fetching = true;
        try
        {
            var snapshot = await source.FetchAsync(CancellationToken.None);
            if (!ReferenceEquals(source, _source)) return; // logged out meanwhile
            _fetched = true;
            Apply(snapshot);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Favorites] reading the Favorites folder failed: {ex.Message}");
            if (ReferenceEquals(source, _source) && Mode == FavoritesMode.Loading) SetMode(FavoritesMode.Unavailable);
        }
        finally
        {
            _fetching = false;
        }
    }

    /// <summary>Takes what the folder holds. Public to the selftest through <see cref="ApplyForTest"/>.</summary>
    private void Apply(FavoritesSnapshot snapshot)
    {
        if (_source == null) return;

        if (!_source.HasFolder)
        {
            _items = new List<LandmarkInventoryItem>();
            SetMode(FavoritesMode.NoFolder);
            return;
        }

        if (!snapshot.Confirmed)
        {
            // Never believe an unconfirmed (possibly empty) answer: keep what was shown, or say it is unavailable.
            if (Mode != FavoritesMode.Folder) SetMode(FavoritesMode.Unavailable);
            return;
        }

        var sorted = FavoritesOrder.Sort(snapshot.Items, _order).ToList();
        if (FavoritesOrder.NeedsToSaveOrder(sorted, _order))
        {
            // The viewer's needToSaveItemsOrder: once an item has been shown, its place is frozen.
            _order = sorted.Select(i => i.Id).ToList();
            _saveOrder?.Invoke(_order);
        }
        else
        {
            var pruned = FavoritesOrder.Pruned(_order, sorted);
            if (pruned.Count != _order.Count)
            {
                _order = pruned;
                _saveOrder?.Invoke(_order);
            }
        }

        bool same = Mode == FavoritesMode.Folder && _items.Select(i => (i.Id, i.Name)).SequenceEqual(sorted.Select(i => (i.Id, i.Name)));
        _items = sorted;
        Mode = FavoritesMode.Folder;
        if (!same) Changed?.Invoke();
    }

    internal void ApplyForTest(FavoritesSnapshot snapshot)
    {
        _fetched = true;
        Apply(snapshot);
    }

    private void SetMode(FavoritesMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        Changed?.Invoke();
    }

    /// <summary>Makes a landmark a favorite. Returns what was decided; <see cref="FavoriteAddVerdict.Add"/> means the copy
    /// was requested, and the bar shows it once the grid's update arrives.</summary>
    public async Task<FavoriteAddVerdict> AddAsync(Guid itemId)
    {
        if (_source == null) return FavoriteAddVerdict.NoFolder;
        var verdict = await _source.AddAsync(itemId, CancellationToken.None);
        if (verdict == FavoriteAddVerdict.Add) _refetchAtMs = Time.GetTicksMsec() + RefetchAfterAddMs; // belt and braces
        return verdict;
    }

    /// <summary>Takes a favorite out: its copy goes to the Trash. The bar drops it at once; the folder read that follows
    /// confirms it.</summary>
    public async Task RemoveAsync(Guid itemId)
    {
        if (_source == null) return;
        _items = _items.Where(i => i.Id != itemId).ToList();
        Changed?.Invoke();
        await _source.RemoveAsync(itemId);
    }

    /// <summary>Creates a landmark for the current place in the Favorites folder. Null: no such folder.</summary>
    public async Task<bool?> AddCurrentLocationAsync(string name)
    {
        if (_source == null) return null;
        bool? created = await _source.AddCurrentLocationAsync(name, CancellationToken.None);
        if (created == true) _refetchAtMs = Time.GetTicksMsec() + RefetchAfterAddMs;
        return created;
    }
}
