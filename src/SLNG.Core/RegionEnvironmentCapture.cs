namespace SLNG.Core;

/// <summary>A raw, unparsed snapshot of a region's Windlight / EEP environment, plus which of the
/// two environment capabilities the simulator actually advertised (FEAT-ENV-01 Phase A).
///
/// This type exists to answer a question we cannot answer by reasoning: OpenSim's EEP support
/// varies by version, and SL and OpenSim need not agree, so the fallback chain
/// (EEP -> legacy Windlight -> viewer default) cannot be designed against a guess. One login with
/// this in place produces the fixture the parser is then written and tested against.
///
/// The LLSD is carried as TEXT (notation format) rather than as a parsed structure on purpose:
/// this is the layer boundary, and LibreMetaverse's <c>OSD</c> must not cross it (AGENTS.md).
/// Phase B replaces this with a real parsed model; until then a string is exactly as much
/// structure as we can honestly claim to understand.
///
/// Not an <see cref="IWorldEvent"/>: it mutates no world state, same reasoning as
/// <see cref="ChatMessageEvent"/>.</summary>
/// <param name="RegionHandle">The region this snapshot came from.</param>
/// <param name="RegionName">Region name, for the log line and the dump filename.</param>
/// <param name="HasExtEnvironmentCap">Whether the sim advertised <c>ExtEnvironment</c> (EEP).</param>
/// <param name="HasEnvironmentSettingsCap">Whether the sim advertised <c>EnvironmentSettings</c>
/// (legacy Windlight).</param>
/// <param name="ExtEnvironmentLlsd">The EEP day-cycle / sky / water LLSD as notation text, or null
/// if the cap was absent, the request failed, or the region inherits its parent's environment.</param>
/// <param name="LegacyEnvironmentLlsd">The legacy Windlight LLSD as notation text, or null.</param>
/// <param name="DayLength">Length of one full day/night cycle, in seconds.</param>
/// <param name="DayOffset">Offset from the start of the day cycle, in seconds.</param>
/// <param name="IsDefault">Whether the sim reported this as the region/grid default rather than a
/// custom environment.</param>
/// <param name="Error">Populated when the capture itself failed, so a missing environment can be
/// told apart from a broken request.</param>
/// <param name="ParcelId">Local id of the parcel the agent stands on, or -1 when it could not be
/// determined. The environment is a PER-PARCEL setting, not a per-region one: OpenSim's cap handler
/// reads a <c>parcelid</c> query parameter and resolves it through
/// <c>LandChannel.GetLandObject</c> (EnvironmentModule.cs:459-495), and the viewer asks per parcel
/// via <c>LLEnvironment::requestParcel</c>. Asking only for the region returns the region's
/// environment even where the parcel overrides it.</param>
/// <param name="ParcelEnvironmentLlsd">The parcel's own EEP day cycle as notation text, or null
/// when the parcel inherits the region's. Null is the common case and is not an error.</param>
/// <param name="ParcelDayLength">The parcel's day length in seconds, when it has its own.</param>
/// <param name="ParcelDayOffset">The parcel's day offset in seconds, when it has its own.</param>
public record RegionEnvironmentCapture(
    ulong RegionHandle,
    string RegionName,
    bool HasExtEnvironmentCap,
    bool HasEnvironmentSettingsCap,
    string? ExtEnvironmentLlsd,
    string? LegacyEnvironmentLlsd,
    int DayLength,
    int DayOffset,
    bool IsDefault,
    string? Error = null,
    int ParcelId = -1,
    string? ParcelEnvironmentLlsd = null,
    int ParcelDayLength = 0,
    int ParcelDayOffset = 0
);

/// <summary>A region's parsed environment: the day cycle to evaluate, and which source it came
/// from (FEAT-ENV-01 Phase B).
///
/// Separate from <see cref="RegionEnvironmentCapture"/> on purpose. The capture is the raw
/// evidence and stays useful for diagnosing a grid whose LLSD we do not yet handle; this is the
/// model the renderer consumes. Both are raised for the same region.
///
/// Not an <see cref="IWorldEvent"/>: the environment is not world state in the entity sense.</summary>
/// <param name="RegionHandle">The region this environment applies to.</param>
/// <param name="Cycle">The day cycle, already resolved to keyframes and carrying the region's day
/// length and offset. A region with no environment yields <see cref="DayCycle.Default"/>.</param>
/// <param name="Source">Which capability the settings came from.</param>
public record RegionEnvironmentEvent(
    ulong RegionHandle,
    DayCycle Cycle,
    EnvironmentSource Source
);

/// <summary>Where a region's environment came from. Worth carrying rather than discarding: the
/// three cases look identical once parsed, and telling "this grid gave us EEP" from "this grid
/// gave us nothing and you are looking at the viewer default" is the difference between a bug and
/// expected behaviour.</summary>
public enum EnvironmentSource
{
    /// <summary>No environment capability answered; the viewer default is in use.</summary>
    Default,
    /// <summary>Extended Environment (EEP), via the <c>ExtEnvironment</c> capability.</summary>
    ExtendedEnvironment,
    /// <summary>Legacy Windlight, via the <c>EnvironmentSettings</c> capability.</summary>
    LegacyWindlight,
}
