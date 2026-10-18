namespace SLNG.Core;

/// <summary>Outcome of an attempt to rez an inventory object into the world (FEAT-INV-15).</summary>
public enum RezObjectResult
{
    /// <summary>Rez request was sent to the simulator.</summary>
    Success = 0,

    /// <summary>Session is disconnected or has no current region.</summary>
    NotConnected,

    /// <summary>The inventory item was not found in the local inventory store.</summary>
    ItemNotFound,

    /// <summary>The inventory item is not an object or attachment.</summary>
    NotAnObject,

    /// <summary>The agent does not have build permissions on this parcel or sim.</summary>
    NoBuildRights,
}
