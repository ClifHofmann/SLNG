namespace SLNG.Net;

/// <summary>What became of a request to delete something in the Trash for good. FEAT-INV-10.</summary>
public enum TrashPurgeResult
{
    /// <summary>Deleted. Where the grid has AIS it confirmed the delete; over UDP the message was
    /// sent, and the legacy protocol has no reply to wait for.</summary>
    Purged,

    /// <summary>The Trash is known to be empty — nothing was sent.</summary>
    NothingToDo,

    /// <summary>Refused before the grid was asked: something inside is being worn right now.</summary>
    WornItemsInside,

    /// <summary>Refused before the grid was asked: the thing is not inside the Trash. Deleting for
    /// good is only ever offered there.</summary>
    NotInTrash,

    /// <summary>The grid answered with an error.</summary>
    GridRefused,

    /// <summary>Not logged in, no inventory yet, or no Trash folder we can be sure of.</summary>
    Unavailable,
}
