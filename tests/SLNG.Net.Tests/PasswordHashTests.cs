using LibreMetaverse;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-SEC-02. The stakes here are not subtle: if <see cref="PasswordHash"/> and
/// LibreMetaverse's own conversion ever disagree, every saved login stops working at once and
/// the plaintext that would have fixed it is already gone from disk.
/// </summary>
public class PasswordHashTests
{
    private const string Plain = "hunter2";

    /// <summary>The value the pinned library actually returns, captured by invoking it. Hard-coded
    /// rather than computed so that a LibreMetaverse upgrade which changes the helper breaks this
    /// one test instead of every user's saved login.</summary>
    private const string KnownHash = "$1$2ab96390c7dbe3439de74d0c9b0b1767";

    [Fact]
    public void HashMatchesTheLibrarysOwnConversion()
        => Assert.Equal(Utils.MD5(Plain), PasswordHash.Hash(Plain));

    [Fact]
    public void HashMatchesTheGoldenVector()
        => Assert.Equal(KnownHash, PasswordHash.Hash(Plain));

    /// <summary>35 characters is not cosmetic — it is half of LibreMetaverse's passthrough
    /// condition (Login.cs:963). A hash of any other length would be hashed a second time.</summary>
    [Fact]
    public void HashIsExactlyTheLengthTheLoginPassthroughRequires()
    {
        var hashed = PasswordHash.Hash(Plain);
        Assert.Equal(35, hashed.Length);
        Assert.StartsWith("$1$", hashed, StringComparison.Ordinal);
    }

    /// <summary>Re-hashing must be a no-op: the migration runs over the config file on every
    /// start, and a second pass must not destroy what the first one wrote.</summary>
    [Fact]
    public void HashIsIdempotent()
    {
        var once = PasswordHash.Hash(Plain);
        Assert.Equal(once, PasswordHash.Hash(once));
        Assert.Equal(once, PasswordHash.Hash(PasswordHash.Hash(once)));
    }

    [Theory]
    [InlineData("$1$2ab96390c7dbe3439de74d0c9b0b1767", true)]
    [InlineData("hunter2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    // Right prefix, wrong length -- must NOT be taken for a hash, or it goes to the grid unhashed.
    [InlineData("$1$2ab96390c7dbe3439de74d0c9b0b176", false)]
    [InlineData("$1$2ab96390c7dbe3439de74d0c9b0b17677", false)]
    // Right length, wrong prefix.
    [InlineData("$2$2ab96390c7dbe3439de74d0c9b0b1767", false)]
    // A 35-character plaintext must still be hashed.
    [InlineData("correct horse battery staple extra!", false)]
    public void IsHashedAgreesWithTheLibrarysCondition(string? candidate, bool expected)
    {
        Assert.Equal(expected, PasswordHash.IsHashed(candidate));

        // The same test from the other side: mirror Login.cs:963 literally and require the two
        // to agree, so this cannot drift from the library it is modelled on.
        bool libraryWouldPassThrough =
            candidate != null && candidate.Length == 35 && candidate.StartsWith("$1$", StringComparison.Ordinal);
        Assert.Equal(libraryWouldPassThrough, PasswordHash.IsHashed(candidate));
    }

    /// <summary>LibreMetaverse hashes the whole string; the LL viewer truncates to 16 characters
    /// first. They disagree for any password longer than that, and this pins which behaviour ships
    /// — matching the library is what keeps existing logins working, so a change here would be a
    /// deliberate decision, not a refactor.</summary>
    [Fact]
    public void LongPasswordsAreNotTruncatedToSixteenCharacters()
    {
        const string longPassword = "0123456789abcdefTAIL";
        Assert.NotEqual(PasswordHash.Hash("0123456789abcdef"), PasswordHash.Hash(longPassword));
        Assert.Equal(Utils.MD5(longPassword), PasswordHash.Hash(longPassword));
    }

    /// <summary>Two different passwords must not collide into one stored credential.</summary>
    [Fact]
    public void DifferentPasswordsHashDifferently()
        => Assert.NotEqual(PasswordHash.Hash("alpha"), PasswordHash.Hash("beta"));

    [Fact]
    public void EmptyPasswordStillHashes()
    {
        var hashed = PasswordHash.Hash("");
        Assert.Equal(35, hashed.Length);
        Assert.Equal(Utils.MD5(""), hashed);
    }

    [Fact]
    public void NullPasswordThrowsRatherThanSilentlyHashingNothing()
        => Assert.Throws<ArgumentNullException>(() => PasswordHash.Hash(null!));
}
