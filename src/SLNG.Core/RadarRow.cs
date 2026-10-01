using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// One person in the nearby-people table (FEAT-UI-39). Immutable: the table builds a fresh row set
/// from the world every refresh and sorts/filters copies, so a row never changes under the UI that
/// is drawing it. Engine- and protocol-neutral -- <c>SLNG.Net</c> / <c>app</c> fill it, nothing
/// here knows where the numbers came from.
/// </summary>
/// <param name="AgentId">The avatar this row is about.</param>
/// <param name="Name">The name to show and to filter and sort on.</param>
/// <param name="Position">Region-local metres (x east, y north, z up).</param>
/// <param name="Distance">The 3D distance from the local avatar in metres.</param>
/// <param name="HeightKnown">False means only a coarse location is known, whose height is pinned at
/// the 1020 m ceiling, so <paramref name="Distance"/> is only a lower bound; the range cell then
/// reads "&gt;" rather than pretending to a precise number.</param>
/// <param name="Relation">Friend, muted or neither.</param>
/// <param name="InSameRegion">True if the avatar is in the same region as the local avatar.</param>
/// <param name="IsSitting">True if the avatar is sitting.</param>
/// <param name="IsTyping">True if the avatar is typing in chat.</param>
/// <param name="VoiceLevel">Voice activity, or null for "unknown / no voice" -- which sorts last
/// in either direction rather than being mistaken for silence.</param>
/// <param name="SeenFor">How long the avatar has been in the list. Resets when it leaves and
/// re-enters, which is what makes it useful for spotting a newcomer.</param>
/// <param name="Profile">What the avatar's profile has told us so far (note, payment status, age),
/// or null until it arrives. It trails the avatar by a round trip.</param>
/// <param name="WithinDrawDistance">True if an avatar object exists, i.e. it is actually
/// rendered. Distinct from being in the list, because the list also carries avatars beyond the
/// draw distance.</param>
public sealed record RadarRow(
    Guid AgentId,
    string Name,
    Vector3 Position,
    float Distance,
    bool HeightKnown,
    RadarRelation Relation,
    bool InSameRegion,
    bool IsSitting,
    bool IsTyping,
    float? VoiceLevel,
    TimeSpan SeenFor,
    AvatarBriefProfile? Profile,
    bool WithinDrawDistance);
