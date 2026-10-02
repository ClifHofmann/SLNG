namespace SLNG.Net;

/// <summary>Raised by <see cref="GridSession.CovenantFailed"/> when <see cref="GridSession.RequestCovenant"/>
/// got no <c>EstateCovenantReply</c> in time. Distinct from an estate that has no covenant (that
/// arrives as a <see cref="SLNG.Core.CovenantInfo"/> with
/// <see cref="SLNG.Core.CovenantTextState.None"/>) and from a covenant whose text could not be
/// fetched (a <see cref="SLNG.Core.CovenantInfo"/> with <see cref="SLNG.Core.CovenantTextState.Failed"/>).</summary>
public sealed record CovenantFailure(ulong RegionHandle);
