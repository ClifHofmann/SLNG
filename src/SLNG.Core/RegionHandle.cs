namespace SLNG.Core;

/// <summary>
/// A region handle is the region's south-west corner in global metres: east in the high 32 bits,
/// north in the low 32 (<c>to_region_handle</c> in the reference viewer). Standard regions are
/// 256 m, so dividing by that gives the grid coordinates a map-tile URL is built from.
/// </summary>
public static class RegionHandle
{
    /// <summary>Edge of a standard region, and the unit of the grid the map tiles are laid on.</summary>
    public const int RegionMetres = 256;

    /// <summary>The south-west corner's global east coordinate, in metres.</summary>
    public static uint OriginX(ulong handle) => (uint)(handle >> 32);

    /// <summary>The south-west corner's global north coordinate, in metres.</summary>
    public static uint OriginY(ulong handle) => (uint)(handle & 0xFFFFFFFF);

    /// <summary>Region-grid column (east), e.g. 1000 for the region at 256000 m.</summary>
    public static int GridX(ulong handle) => (int)(OriginX(handle) / RegionMetres);

    /// <summary>Region-grid row (north).</summary>
    public static int GridY(ulong handle) => (int)(OriginY(handle) / RegionMetres);

    /// <summary>The handle for a south-west corner given in global metres.</summary>
    public static ulong FromOrigin(uint originX, uint originY) => ((ulong)originX << 32) | originY;
}
