namespace SLNG.Core;

/// <summary>
/// Source of world-mutating events. Implemented by the networking layer
/// (<c>SLNG.Net.GridSession</c>) and consumed by <see cref="WorldSimulation"/>.
///
/// This interface is the inversion seam: the engine- and protocol-agnostic core
/// depends on it, so <c>SLNG.Core</c> never references <c>SLNG.Net</c>.
/// </summary>
public interface IWorldEventSource
{
    event EventHandler<ObjectUpdateEvent>? ObjectUpdateReceived;
    event EventHandler<AvatarUpdateEvent>? AvatarUpdateReceived;
    event EventHandler<ObjectRemovedEvent>? ObjectRemovedReceived;
    event EventHandler<TerrainPatchEvent>? TerrainPatchReceived;
    event EventHandler<TerrainSettingsEvent>? TerrainSettingsReceived;
    event EventHandler<RegionDisconnectedEvent>? RegionDisconnectedReceived;
    event EventHandler<AvatarAppearanceEvent>? AvatarAppearanceReceived;
    event EventHandler<AvatarAnimationEvent>? AvatarAnimationReceived;
}
