using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>Covers the change detection that decides whether a re-polled environment is published
/// (FEAT-ENV-01).
///
/// This matters more than its size suggests. A RegionInfo packet is only a "something about this
/// region changed" nudge and carries no payload, so the answer is a capability fetch — but on
/// OSGrid's Lbsa Plaza those packets arrive around 45 times a minute, sustained. Measured before
/// the fix: 2649 re-polls in one session, every one of them re-delivering a byte-identical 6060
/// character payload, each re-parsed, re-logged and re-written to disk. The fingerprint below is
/// what makes an unchanged environment cost nothing downstream.</summary>
public class EnvironmentRepollTests
{
    private static RegionEnvironmentCapture Capture(
        string? ext = "{'sky':1}",
        string? parcel = null,
        string? legacy = null,
        int dayLength = 14400,
        int dayOffset = 46800,
        ulong handle = 1234,
        int parcelId = 1) =>
        new(handle, "Lbsa Plaza", true, true, ext, legacy, dayLength, dayOffset, false,
            null, parcelId, parcel, 0, 0);

    [Fact]
    public void IdenticalCaptures_ProduceTheSameFingerprint()
    {
        Assert.Equal(
            GridSession.EnvironmentFingerprint(Capture()),
            GridSession.EnvironmentFingerprint(Capture()));
    }

    [Fact]
    public void RegionName_DoesNotAffectTheFingerprint()
    {
        // The name is for the log line and the dump filename; it never changes what renders, and a
        // sim that renames itself must not force a republish.
        var a = Capture() with { RegionName = "Lbsa Plaza" };
        var b = Capture() with { RegionName = "Lbsa  Plaza" };
        Assert.Equal(GridSession.EnvironmentFingerprint(a), GridSession.EnvironmentFingerprint(b));
    }

    [Fact]
    public void EachMeaningfulChange_ProducesADifferentFingerprint()
    {
        string baseline = GridSession.EnvironmentFingerprint(Capture());

        (string What, RegionEnvironmentCapture Changed)[] cases =
        [
            ("region day cycle", Capture(ext: "{'sky':2}")),
            // A parcel gaining its own environment overrides the region's, so it must republish
            // even though the region's own LLSD is untouched.
            ("parcel gains its own environment", Capture(parcel: "{'sky':9}")),
            ("legacy windlight payload", Capture(legacy: "{'legacy':1}")),
            ("day length", Capture(dayLength: 7200)),
            ("day offset", Capture(dayOffset: 0)),
            // Crossing back into a region seen earlier: the renderer's state moved on in between,
            // so the same environment still has to be delivered again.
            ("region handle", Capture(handle: 5678)),
            ("parcel id", Capture(parcelId: 2)),
        ];

        foreach (var (what, changed) in cases)
        {
            Assert.False(
                baseline == GridSession.EnvironmentFingerprint(changed),
                $"a changed {what} must republish, but the fingerprint was unchanged");
        }
    }

    [Fact]
    public void LosingTheExtEnvironment_ProducesADifferentFingerprint()
    {
        // The failure mode this guards: a region whose EEP cycle is removed falls back to the
        // default sky, and treating "no payload" as "no change" would leave the old sky up.
        Assert.NotEqual(
            GridSession.EnvironmentFingerprint(Capture()),
            GridSession.EnvironmentFingerprint(Capture(ext: null)));
    }

    [Fact]
    public void FieldsAreDelimited_SoAdjacentValuesCannotImpersonateEachOther()
    {
        // Without a separator, ext "a" + parcel "b" and ext "ab" + parcel "" are the same string,
        // and a real environment change would be silently swallowed as "unchanged".
        Assert.NotEqual(
            GridSession.EnvironmentFingerprint(Capture(ext: "a", parcel: "b")),
            GridSession.EnvironmentFingerprint(Capture(ext: "ab", parcel: "")));
    }
}
