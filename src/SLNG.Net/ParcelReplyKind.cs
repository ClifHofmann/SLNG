namespace SLNG.Net;

/// <summary>How a <c>ParcelProperties</c> reply relates to the Land-Info window, decided from its
/// sequence id alone (<see cref="ParcelInfoMapper.Classify"/>).</summary>
internal enum ParcelReplyKind
{
    /// <summary>Not about the agent's parcel or our lookup: a hover, or a collision/ban-line reply.</summary>
    Ignore,

    /// <summary>The answer to <c>RequestParcelInfoAt</c> / <c>RequestParcelInfoHere</c>.</summary>
    Requested,

    /// <summary>The sim telling us about the agent's own parcel (parcel crossing, edits).</summary>
    AgentPush,
}
