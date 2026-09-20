namespace SLNG.Net;

/// <summary>Which parts of an object's transform an update carries.</summary>
/// <remarks>
/// The wire message names them in a type mask and carries only those, in this order. Sending a
/// field the user did not touch is not free: the simulator acts on every one it is given, and an
/// attachment answers a scale that arrives without a position beside it with a region
/// coordinate. The values are the viewer's own UPD_POSITION / UPD_ROTATION / UPD_SCALE.
/// </remarks>
[System.Flags]
public enum TransformFields
{
    None = 0,
    Position = 1,
    Rotation = 2,
    Scale = 4,
    All = Position | Rotation | Scale,
}
