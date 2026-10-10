namespace SLNG.App;

using System.Runtime.CompilerServices;
using Godot;

/// <summary>
/// BUG-PERF-15: Retains CPU vertex/index arrays for committed <see cref="ArrayMesh"/> instances.
///
/// <para>Calling <c>ArrayMesh.SurfaceGetArrays</c> or <c>CreateTrimeshShape</c> triggers a synchronous
/// GPU read-back and RenderingServer round-trip. Under the separate render thread (FEAT-PERF-13),
/// this causes a full pipeline stall ("Hänger"). By retaining the CPU arrays that were prepared
/// on worker threads, operations like <c>SplitSortedSurfaces</c>, pick body generation, and outline
/// construction can clone surfaces or extract geometry without any RenderingServer read-backs.</para>
/// </summary>
internal static class MeshSurfaceCache
{
    private static readonly ConditionalWeakTable<ArrayMesh, Godot.Collections.Array[]> _cache = new();

    /// <summary>Stores the prepared surface arrays for the given mesh.</summary>
    public static void Store(ArrayMesh? mesh, Godot.Collections.Array[]? surfaces)
    {
        if (mesh != null && surfaces != null)
            _cache.AddOrUpdate(mesh, surfaces);
    }

    /// <summary>Returns true if CPU surface arrays are cached for <paramref name="mesh"/>.</summary>
    public static bool TryGetSurfaces(ArrayMesh mesh, out Godot.Collections.Array[] surfaces)
        => _cache.TryGetValue(mesh, out surfaces!);

    /// <summary>Returns true if the specific surface at <paramref name="surfaceIndex"/> is cached.</summary>
    public static bool TryGetSurface(ArrayMesh mesh, int surfaceIndex, out Godot.Collections.Array surface)
    {
        if (_cache.TryGetValue(mesh, out var surfaces) && surfaceIndex >= 0 && surfaceIndex < surfaces.Length)
        {
            surface = surfaces[surfaceIndex];
            return true;
        }
        surface = null!;
        return false;
    }
}
