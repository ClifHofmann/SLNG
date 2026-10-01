using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// One prim as the radar map draws it (FEAT-UI-39 phase 2b): a filled square. The position is in the
/// CURRENT region's metres -- a neighbour's objects are already offset by where that region lies --
/// so the radar can treat every object the same way it treats a tile or a dot.
/// </summary>
/// <param name="Position">Where the prim's centre is, east and north, in the current region's metres.</param>
/// <param name="Radius">Half the square's side, in metres. Already grown for an owned prim and capped
/// for a huge one; see <see cref="RadarObjects.RadiusFor"/>.</param>
/// <param name="IsYours">The simulator says the logged-in agent owns it. It is the only owner the radar
/// can tell apart: a prim's owner id only arrives once the object has been selected.</param>
/// <param name="BelowWater">The prim sits under its region's water line, which the map draws darker.</param>
/// <param name="Phantom">A phantom prim is drawn a little see-through, as in the reference viewer.</param>
public readonly record struct RadarObject(
    Vector2 Position,
    float Radius,
    bool IsYours,
    bool BelowWater,
    bool Phantom);
