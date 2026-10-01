namespace SLNG.Core;

/// <summary>How an avatar's dot on the radar says where it is vertically, relative to the local
/// avatar (FEAT-UI-39). Level is a filled circle, Above and Below are triangles, Unknown a hollow
/// ring: an avatar known only from a coarse location has a pinned height, so the map must not claim
/// it is level with you.</summary>
public enum HeightMarker
{
    Level,
    Above,
    Below,
    Unknown,
}
