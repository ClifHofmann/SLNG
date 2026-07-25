using System;

namespace SLNG.Core;

/// <summary>
/// Maintains the continuous heightmap for a region.
/// </summary>
public class RegionTerrain
{
    public const int DefaultRegionSize = 256;
    public const int PatchSize = 16;

    /// <summary>Hard ceiling for how large <see cref="EnsureSize"/> / <see cref="ApplyPatch"/> will
    /// ever grow a heightmap. OpenSim varregions top out at 32x32 standard regions (see the
    /// "Setting Up Mega-Regions" / "Varregion" wiki pages), i.e. 8192x8192m. Bounding growth here
    /// keeps a corrupt or spoofed patch/region-size value from triggering an unbounded allocation —
    /// see <see cref="ApplyPatch"/> for why patch coordinates are trusted for growth at all now.</summary>
    public const int MaxRegionSize = 8192;

    // Terrain Settings
    public Guid TerrainDetail0 { get; set; }
    public Guid TerrainDetail1 { get; set; }
    public Guid TerrainDetail2 { get; set; }
    public Guid TerrainDetail3 { get; set; }

    public float[] TerrainStartHeights { get; } = new float[4];
    public float[] TerrainHeightRanges { get; } = new float[4];

    public float WaterHeight { get; set; } = 20.0f; // Default OpenSim water height

    // The master heightmap (Y-up elevation).
    private float[] _heights;

    // Parallel to _heights: whether a real patch has ever written this cell. A freshly-grown or
    // freshly-constructed heightmap defaults every cell to 0.0f, which is indistinguishable from
    // a legitimate sea-level-ish height -- callers that treat "present in this array" as "safe to
    // stand on" (see AvatarController's ground-clamp fallback) need a way to tell "genuinely flat"
    // apart from "patch for this cell hasn't arrived over the network yet". Right after a landmark
    // teleport into a brand new region, most of the map is the latter for the first several
    // LayerData packets -- treating it as height 0 made the avatar free-fall toward Z~1 before the
    // real (often ~20-25m) terrain height streamed in, i.e. "falls through the floor that isn't
    // there yet".
    private bool[] _loaded;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public RegionTerrain(int width = DefaultRegionSize, int height = DefaultRegionSize)
    {
        Width = width;
        Height = height;
        _heights = new float[Width * Height];
        _loaded = new bool[Width * Height];
    }

    private void Resize(int newWidth, int newHeight)
    {
        float[] newHeights = new float[newWidth * newHeight];
        bool[] newLoaded = new bool[newWidth * newHeight];
        for (int y = 0; y < Height; y++)
        {
            Array.Copy(_heights, y * Width, newHeights, y * newWidth, Width);
            Array.Copy(_loaded, y * Width, newLoaded, y * newWidth, Width);
        }
        Width = newWidth;
        Height = newHeight;
        _heights = newHeights;
        _loaded = newLoaded;
    }

    /// <summary>Grows the heightmap to at least the given dimensions (clamped to
    /// <see cref="MaxRegionSize"/>), preserving existing data.</summary>
    public void EnsureSize(int width, int height)
    {
        width = Math.Min(width, MaxRegionSize);
        height = Math.Min(height, MaxRegionSize);
        if (width > Width || height > Height)
            Resize(Math.Max(Width, width), Math.Max(Height, height));
    }

    /// <summary>
    /// Applies a 16x16 patch to the master heightmap, growing the map to fit if the patch falls
    /// outside the current bounds (clamped to <see cref="MaxRegionSize"/>; a patch that would
    /// still exceed that ceiling is dropped as corrupt/spoofed rather than trusted).
    ///
    /// This used to reject out-of-bounds patches outright on the theory that the region's true
    /// size was already known from the terrain/settings events (<see cref="EnsureSize"/>) by the
    /// time patches arrived. That theory doesn't hold: LibreMetaverse 3.0.0's <c>Simulator.SizeX</c>/
    /// <c>SizeY</c> are set once at construction and never corrected afterward, and neither the
    /// initial login connect (<c>Login.cs</c>, both call sites) nor the legacy UDP
    /// <c>TeleportFinish</c> connect (<c>AgentManager.PacketHandlers.cs</c>) passes the real region
    /// size into that constructor -- both always fall back to the 256x256 default, permanently,
    /// for that simulator. A standard 256x256 region hides the bug (the wrong default happens to
    /// be right); a varregion doesn't: OpenSim keeps sending 16x16 patches for the sim's actual
    /// footprint regardless of what LibreMetaverse thinks the size is, and this method used to
    /// silently drop every one of them past the first 256x256 corner, leaving that portion of the
    /// heightmap at its default 0 and TerrainRenderer with nothing to build a mesh from there --
    /// e.g. OSGrid's "The Dangazi Forest" (a 3x3 var, 768x768m; confirmed via hgsafari.blogspot.com's
    /// "Dangazi Win-Win" writeup). The patch coordinates OpenSim actually sends are the one source of
    /// region size in this pipeline that isn't tainted by that LibreMetaverse gap, so growth is
    /// driven by them directly now, bounded by <see cref="MaxRegionSize"/> so a bogus coordinate
    /// can't force an unbounded allocation.
    /// </summary>
    public void ApplyPatch(int patchX, int patchY, float[] patchHeights)
    {
        if (patchHeights == null || patchHeights.Length != PatchSize * PatchSize)
            return;
        if (patchX < 0 || patchY < 0)
            return;

        int startX = patchX * PatchSize;
        int startY = patchY * PatchSize;

        // Drop a patch that's corrupt/spoofed rather than a legitimately large varregion.
        if (startX + PatchSize > MaxRegionSize || startY + PatchSize > MaxRegionSize)
            return;

        if (startX + PatchSize > Width || startY + PatchSize > Height)
            EnsureSize(startX + PatchSize, startY + PatchSize);

        for (int y = 0; y < PatchSize; y++)
        {
            for (int x = 0; x < PatchSize; x++)
            {
                int localIndex = y * PatchSize + x;
                int globalIndex = (startY + y) * Width + (startX + x);
                _heights[globalIndex] = patchHeights[localIndex];
                _loaded[globalIndex] = true;
            }
        }
    }

    /// <summary>
    /// Gets a copy of the current heightmap.
    /// </summary>
    public float[] GetHeights()
    {
        float[] copy = new float[_heights.Length];
        Array.Copy(_heights, copy, _heights.Length);
        return copy;
    }

    /// <summary>Returns the height at (x, y) only if a real patch has ever written that cell --
    /// see <see cref="_loaded"/>'s doc comment for why a plain 0.0f default can't be trusted as
    /// "ground is here". Out-of-bounds coordinates are always unknown.</summary>
    public bool TryGetKnownHeight(int x, int y, out float height)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
        {
            height = 0f;
            return false;
        }

        int index = y * Width + x;
        height = _heights[index];
        return _loaded[index];
    }
}
