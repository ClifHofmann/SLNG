using System.Text.RegularExpressions;
using Xunit;

namespace SLNG.Core.Tests;

// BUG-GRID-01. The grid slug is the one thing standing between "Clifton Howlett on Second Life" and
// "Clifton Howlett on OSGrid". It must tell grids apart, must not tell one grid from itself, and must
// always be a single, legal path segment -- whatever a user (or a hostile grid list) types.
public class GridIdentityTests
{
    private const string Agni = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";
    private const string Aditi = "https://login.aditi.lindenlab.com/cgi-bin/login.cgi";

    [Fact]
    public void The_two_Linden_grids_are_distinct_and_readable()
    {
        Assert.Equal("agni", GridIdentity.Slug(Agni));
        Assert.Equal("aditi", GridIdentity.Slug(Aditi));
    }

    [Theory]
    [InlineData("http://login.agni.lindenlab.com/cgi-bin/login.cgi")]
    [InlineData("https://LOGIN.AGNI.LINDENLAB.COM:443/")]
    [InlineData("login.agni.lindenlab.com")]
    public void The_Linden_login_host_written_other_ways_is_still_agni(string uri)
        => Assert.Equal("agni", GridIdentity.Slug(uri));

    [Theory]
    [InlineData("http://hg.osgrid.org/")]
    [InlineData("http://hg.osgrid.org")]
    [InlineData("http://hg.osgrid.org:80")]
    [InlineData("hg.osgrid.org:80")]
    [InlineData("HG.OSGrid.ORG")]
    [InlineData("  https://hg.osgrid.org/login/cgi-bin/login.cgi?x=1#frag  ")]
    [InlineData("http://user:secret@hg.osgrid.org./")]
    public void One_OpenSim_grid_written_many_ways_is_one_slug(string uri)
        => Assert.Equal("hg.osgrid.org", GridIdentity.Slug(uri));

    [Fact]
    public void A_non_default_port_is_part_of_the_identity()
    {
        // Two OpenSim instances on one machine are two grids.
        Assert.Equal("localhost~p9000", GridIdentity.Slug("http://localhost:9000/"));
        Assert.NotEqual(GridIdentity.Slug("http://localhost:9000/"), GridIdentity.Slug("http://localhost:9001/"));
        Assert.Equal("127.0.0.1~p9000", GridIdentity.Slug("http://127.0.0.1:9000"));
    }

    [Fact]
    public void Different_OpenSim_grids_are_different_slugs()
    {
        Assert.NotEqual(GridIdentity.Slug("http://hg.osgrid.org/"), GridIdentity.Slug("http://login.twistedgrid.xyz:8002/"));
        Assert.NotEqual(GridIdentity.Slug("http://grid-a.example/"), GridIdentity.Slug("http://grid-b.example/"));
        // The same host name under another domain is another grid.
        Assert.NotEqual(GridIdentity.Slug("http://login.example.com/"), GridIdentity.Slug("http://login.example.org/"));
    }

    [Fact]
    public void A_host_that_looks_like_a_Linden_grid_name_is_not_one()
    {
        // The slug of a Linden grid is purely [a-z0-9]; a plain host that would also be gets a marker, so
        // a machine called "agni" can never read or write Second Life's data.
        Assert.Equal("agni~h", GridIdentity.Slug("http://agni/"));
        Assert.NotEqual(GridIdentity.Slug(Agni), GridIdentity.Slug("http://agni/"));
        Assert.Equal("localhost~h", GridIdentity.Slug("http://localhost/"));

        // Not the exact Linden shape: four labels, "lindenlab.com" last, a plain name second.
        Assert.NotEqual("agni", GridIdentity.Slug("http://login.agni.lindenlab.com.evil.example/"));
        Assert.NotEqual("agni", GridIdentity.Slug("http://login.agni.lindenlab.com:8002/"));
        Assert.NotEqual("agni", GridIdentity.Slug("http://other.agni.lindenlab.com/"));
    }

    [Fact]
    public void Unreadable_input_still_gets_a_stable_non_empty_slug_that_differs_per_input()
    {
        string empty = GridIdentity.Slug("");
        Assert.NotEmpty(empty);
        Assert.Equal(empty, GridIdentity.Slug(null));
        Assert.Equal(empty, GridIdentity.Slug("   "));

        string a = GridIdentity.Slug("not a uri at all");
        string b = GridIdentity.Slug("neither is this");
        Assert.NotEqual(a, b);
        Assert.NotEqual(empty, a);
        Assert.Equal(a, GridIdentity.Slug("NOT A URI AT ALL")); // case is not part of identity
    }

    [Fact]
    public void A_scheme_that_is_not_http_is_not_a_login_uri()
    {
        string file = GridIdentity.Slug("file:///etc/passwd");
        Assert.StartsWith("unknown~u", file);
        Assert.StartsWith("unknown~u", GridIdentity.Slug("ftp://hg.osgrid.org/"));
    }

    public static IEnumerable<object[]> HostileInputs() => new[]
    {
        "../../etc/passwd", "..", ".", "/", "\\", "http://../", "http://./", "http://a/b/../../c", "C:\\Windows\\System32",
        "http://exa mple.com/", "http://exa\\mple.com/", "http://münchen.example/", "http://grid.example/\u0000x",
        "http://[::1]:9000/", "http://[fe80::1%25eth0]/", "http://日本語.example/", "con", "nul.example.com", "http://COM1/",
        "http://lpt9.grid.example/", "x" + new string('a', 400), "http://" + new string('a', 300) + ".example/",
        "http://a.example/" + new string('/', 500), "\u202e\u0000\n\t", "http://grid.example:99999/", "http://:80/",
    }.Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(HostileInputs))]
    public void Hostile_input_gives_one_safe_path_segment(string input)
    {
        string slug = GridIdentity.Slug(input);

        AssertSafeSegment(slug);
        Assert.Equal(slug, GridIdentity.Slug(input)); // stable
    }

    [Fact]
    public void Hostile_inputs_do_not_collapse_into_each_other()
    {
        var slugs = HostileInputs().Select(a => GridIdentity.Slug((string)a[0])).ToList();

        // Every input above is different, and none of them may share a directory with another -- except
        // the pairs that really are the same thing once read (none here).
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
    }

    [Fact]
    public void Slugs_are_never_longer_than_one_segment_allows()
    {
        string slug = GridIdentity.Slug("http://" + new string('a', 300) + ".example/");

        Assert.True(slug.Length <= GridIdentity.MaxSegmentLength);
        // The cut keeps the hash of the whole, so two long hosts that start alike still differ.
        Assert.NotEqual(slug, GridIdentity.Slug("http://" + new string('a', 300) + ".example.org/"));
    }

    [Fact]
    public void The_slug_is_the_same_on_every_run_and_machine()
    {
        // Pinned values: a change here moves every user's per-grid data and orphans it.
        Assert.Equal("agni", GridIdentity.Slug(Agni));
        Assert.Equal("hg.osgrid.org", GridIdentity.Slug("http://hg.osgrid.org/"));
        Assert.Equal("unknown~u" + "e3b0c44298fc1c14", GridIdentity.Slug(""));
    }

    [Fact]
    public void Windows_device_names_are_not_used_as_a_segment()
    {
        // "con.example.com" is the device CON to Windows, whatever follows the dot.
        Assert.DoesNotMatch(new Regex(@"^(con|prn|aux|nul|com[0-9]|lpt[0-9])(\.|$)", RegexOptions.IgnoreCase),
            GridIdentity.Slug("http://con.example.com/"));
        Assert.DoesNotMatch(new Regex(@"^(con|prn|aux|nul|com[0-9]|lpt[0-9])(\.|$)", RegexOptions.IgnoreCase),
            GridIdentity.Slug("http://nul.example.com/"));
        Assert.NotEqual(GridIdentity.Slug("http://con.example.com/"), GridIdentity.Slug("http://xon.example.com/"));
    }

    // ---- account slug ------------------------------------------------------------------------

    [Fact]
    public void An_account_is_first_and_last_in_lower_case()
    {
        Assert.Equal("clifton_howlett", GridIdentity.AccountSlug("Clifton", "Howlett"));
        Assert.Equal("clifton_howlett", GridIdentity.AccountSlug(" CLIFTON ", "howlett "));
        Assert.Equal("denise1976_resident", GridIdentity.AccountSlug("Denise1976", "resident"));
    }

    [Fact]
    public void Account_parts_cannot_be_moved_across_the_separator()
    {
        Assert.NotEqual(GridIdentity.AccountSlug("a_b", "c"), GridIdentity.AccountSlug("a", "b_c"));
        Assert.NotEqual(GridIdentity.AccountSlug("ab", "c"), GridIdentity.AccountSlug("a", "bc"));
    }

    [Fact]
    public void An_account_slug_is_never_empty_and_never_the_cache_directory()
    {
        foreach (var (first, last) in new[] { ("", ""), (null, null), ("cache", ""), ("", "cache") })
        {
            string slug = GridIdentity.AccountSlug(first, last);
            AssertSafeSegment(slug);
            Assert.Contains('_', slug);
            Assert.NotEqual("cache", slug);
        }
    }

    [Theory]
    [InlineData("../..", "x")]
    [InlineData("a/b", "c\\d")]
    [InlineData("Jörg", "Müller")]
    [InlineData("日本", "語")]
    [InlineData("\u0000", "\n")]
    public void Hostile_account_names_give_one_safe_segment(string first, string last)
        => AssertSafeSegment(GridIdentity.AccountSlug(first, last));

    // ---- helpers -----------------------------------------------------------------------------

    private static void AssertSafeSegment(string slug)
    {
        Assert.False(string.IsNullOrEmpty(slug));
        Assert.True(slug.Length <= GridIdentity.MaxSegmentLength, $"too long: {slug.Length}");
        Assert.Matches(new Regex(@"^[a-z0-9.~_-]+$"), slug);
        Assert.DoesNotContain('/', slug);
        Assert.DoesNotContain('\\', slug);
        Assert.NotEqual(".", slug);
        Assert.NotEqual("..", slug);
        Assert.False(slug.EndsWith('.') || slug.EndsWith(' '), "Windows strips a trailing dot");
        Assert.Equal(slug, Path.GetFileName(slug)); // a single path segment
        Assert.Empty(slug.Intersect(Path.GetInvalidFileNameChars()));
    }
}
