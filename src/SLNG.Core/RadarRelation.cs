namespace SLNG.Core;

/// <summary>
/// How the viewer user relates to a person in the nearby list (FEAT-UI-39). The table tells a
/// friend and a muted avatar apart from everyone else; an avatar is exactly one of the three.
/// </summary>
public enum RadarRelation
{
    Other,
    Friend,
    Muted,
}
