using System;
using System.Collections.Concurrent;

namespace SLNG.Core;

/// <summary>
/// FEAT-PERF-28: whose texture a fetch is for. The five classes are the ones ADR 0005 asks the wait-for-a-slot figure to be split
/// by, so that "who waits, and for how long" can be read off the log instead of guessed from the priority function. Declared
/// most important first: when two requests for one texture disagree, the lower value wins.
/// </summary>
public enum TexClass
{
    /// <summary>The local agent's own avatar (bakes and worn textures).</summary>
    Self = 0,
    /// <summary>The avatar the camera is on (<see cref="PrioritySubject"/>), bakes and worn textures.</summary>
    Focus = 1,
    /// <summary>Another avatar's bake.</summary>
    OtherBake = 2,
    /// <summary>A texture worn by another avatar (attachments, system layers that are not bakes).</summary>
    OtherWorn = 3,
    /// <summary>Everything else: the world's objects, terrain, UI.</summary>
    Scenery = 4,
}

public static class TexClassifier
{
    /// <summary>How many classes there are.</summary>
    public const int Count = 5;

    /// <summary>The class of a request, from what the GPU cache is told about it.</summary>
    public static TexClass Classify(bool isSelf, bool isAvatar, bool isBake, bool isFocus)
    {
        if (isSelf) return TexClass.Self;
        if (!isAvatar) return TexClass.Scenery;
        if (isFocus) return TexClass.Focus;
        return isBake ? TexClass.OtherBake : TexClass.OtherWorn;
    }

    /// <summary>The name used in the log lines (<c>[TexWait.OtherBake]</c>).</summary>
    public static string Name(TexClass c) => c switch
    {
        TexClass.Self => "Self",
        TexClass.Focus => "Focus",
        TexClass.OtherBake => "OtherBake",
        TexClass.OtherWorn => "OtherWorn",
        _ => "Scenery",
    };
}

/// <summary>
/// The side channel that gets a request's <see cref="TexClass"/> from the GPU cache (which knows whose texture it is) to the fetch
/// gate (which times the wait), without a new parameter on every layer in between. Diagnostics only: the cache notes a class
/// when <see cref="Diag.Verbose"/> is on, the gate takes it when the wait ends. A texture nobody takes (a disk hit never reaches the
/// gate) is bounded by <c>capacity</c>.
/// </summary>
public sealed class TexClassRegistry
{
    /// <summary>The one the client uses.</summary>
    public static TexClassRegistry Shared { get; } = new();

    private readonly ConcurrentDictionary<Guid, TexClass> _classes = new();
    private readonly int _capacity;

    public TexClassRegistry(int capacity = 50_000) => _capacity = Math.Max(1, capacity);

    public int Count => _classes.Count;

    /// <summary>Records the class of a request for <paramref name="id"/>. A second request keeps the more important class.</summary>
    public void Note(Guid id, TexClass texClass)
    {
        if (_classes.Count >= _capacity) _classes.Clear();
        _classes.AddOrUpdate(id, texClass, (_, had) => texClass < had ? texClass : had);
    }

    /// <summary>The class noted for <paramref name="id"/> (and forgets it), or <see cref="TexClass.Scenery"/> when none was.</summary>
    public TexClass Take(Guid id) => _classes.TryRemove(id, out var c) ? c : TexClass.Scenery;
}
