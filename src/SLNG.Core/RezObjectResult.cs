namespace SLNG.Core;

/// <summary>Outcome of an attempt to rez an inventory object into the world (FEAT-INV-15).</summary>
/// <remarks>There is deliberately no "no build rights" member: the reference viewer does not check parcel
/// rights when an object is dropped, the simulator decides (and says so in chat).</remarks>
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

    /// <summary>The item is being worn. The viewer refuses to rez it
    /// (<c>isWearingAttachment</c>, <c>lltooldraganddrop.cpp</c>:2408 and :2469).</summary>
    Worn,

    /// <summary>The drop point is outside the current region. Only the current region is rezzed into.</summary>
    OutsideRegion,
}
