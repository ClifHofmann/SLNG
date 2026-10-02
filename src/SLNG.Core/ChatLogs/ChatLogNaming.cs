using SLNG.Core.Services;

namespace SLNG.Core.ChatLogs;

/// <summary>Everything about naming and stamping that varies between installations of the other
/// viewer, resolved once per login and handed to <see cref="ChatLogger"/>.</summary>
/// <param name="ImStyle">See <see cref="ImLogNameStyle"/>.</param>
/// <param name="DateSuffix">Firestorm's per-account <c>LogFileNamewithDate</c>: <c>name-YYYY-MM</c>
/// and <c>chat-YYYY-MM-DD</c>.</param>
/// <param name="SystemName">The sender Firestorm writes on system lines: <c>Second Life</c> on a
/// Linden grid, <c>Grid</c> on an OpenSim one (<c>SYSTEM_FROM</c>, <c>llworld.cpp</c>).</param>
public sealed record ChatLogNaming(
    ImLogNameStyle ImStyle = ImLogNameStyle.Legacy,
    bool DateSuffix = false,
    string SystemName = "Second Life")
{
    public static readonly ChatLogNaming Default = new();
}
