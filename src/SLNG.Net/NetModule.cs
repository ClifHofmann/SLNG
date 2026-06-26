namespace SLNG.Net;

/// <summary>
/// Placeholder marker for the network-core layer. Wraps LibreMetaverse and emits
/// engine-agnostic DTOs across the boundary. Real implementation begins in task
/// M0-2 (login flow) and M0-3 (event logger).
/// </summary>
public static class NetModule
{
    /// <summary>Layer identifier, used in diagnostics until real types land.</summary>
    public const string Layer = "net";
}
