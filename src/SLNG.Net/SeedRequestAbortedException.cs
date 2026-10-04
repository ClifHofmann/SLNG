namespace SLNG.Net;

/// <summary>
/// Thrown out of the LibreMetaverse log sink on purpose, to stop the library's seed-capability retry
/// loop (BUG-NET-31). It is control flow, not an error.
/// </summary>
/// <remarks>
/// LibreMetaverse 3.1.6's <c>Caps.SeedRequestCompleteHandler</c> logs "Seed capability returned no
/// response. Trying again." and then calls itself again through <c>MakeSeedRequestAsync</c> with no
/// delay, no limit, and — because it cancels its own token first — a request that can never reach the
/// network again. Each round therefore completes synchronously and adds frames to the stack until the
/// process dies. The warning is the only thing SLNG sees of that loop, and it is raised <b>before</b>
/// the retry, so unwinding there ends it: Microsoft.Extensions.Logging re-throws what a provider
/// throws, the library's async method captures it into a task nobody observes, and the call chain
/// returns. <see cref="SeedCapabilityGuard"/> has the whole account.
/// </remarks>
internal sealed class SeedRequestAbortedException : Exception
{
    internal SeedRequestAbortedException()
        : base("The LibreMetaverse seed capability retry loop was stopped by SLNG (BUG-NET-31).")
    {
    }
}
