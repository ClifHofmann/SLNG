using SLNG.Core.Logging;
using Xunit;

namespace SLNG.Core.Tests.Logging;

/// <summary>FEAT-UI-76: what the debug log is allowed to contain.</summary>
public class LogSanitizerTests
{
    private static LogSanitizer New(byte salt = 1) => new(Enumerable.Repeat(salt, 16).ToArray());

    [Fact]
    public void A_line_with_nothing_to_hide_is_unchanged()
    {
        var s = New();
        const string line = "[Boot] v0.28.23-alpha started at 12:34:56.789, frame 1204, 3 avatars, 1.5 GB, pos (128.5, 64.25, 22.1)";
        Assert.Equal(line, s.Sanitize(line));
    }

    [Fact]
    public void A_uuid_becomes_a_stable_tag_that_hides_the_value()
    {
        var s = New();
        const string id = "3f2a9c1e-77b4-4d0a-9e5c-12ab34cd56ef";
        string a = s.Sanitize($"avatar {id} appeared");
        string b = s.Sanitize($"avatar {id} left");
        Assert.DoesNotContain(id, a);
        Assert.Matches(@"avatar <id-[0-9a-f]{6}> appeared", a);
        // The same id reads as the same id, which is what makes a log useful.
        Assert.Equal(Tag(a), Tag(b));
        Assert.NotEqual(Tag(a), Tag(s.Sanitize("avatar 00000000-0000-0000-0000-000000000001 appeared")));
    }

    private static string Tag(string line)
    {
        int i = line.IndexOf("<id-", StringComparison.Ordinal);
        return line.Substring(i, 11);
    }

    [Fact]
    public void The_tag_depends_on_the_salt_so_two_sessions_cannot_be_matched_up()
    {
        const string id = "3f2a9c1e-77b4-4d0a-9e5c-12ab34cd56ef";
        Assert.NotEqual(Tag(New(1).Sanitize(id)), Tag(New(2).Sanitize(id)));
    }

    [Theory]
    [InlineData("connected to 203.0.113.42:12043 ok", "connected to <ip> ok")]
    [InlineData("peer 10.0.0.7 timed out", "peer <ip> timed out")]
    [InlineData("peer 2001:db8::1 timed out", "peer <ip> timed out")]
    [InlineData("peer 2001:0db8:0000:0000:0000:ff00:0042:8329 gone", "peer <ip> gone")]
    public void Ip_addresses_are_replaced(string input, string expected)
    {
        Assert.Equal(expected, New().Sanitize(input));
    }

    [Fact]
    public void A_url_keeps_its_host_and_loses_the_rest()
    {
        var s = New();
        string line = s.Sanitize("GET https://sim10110.agni.lindenlab.com:12043/cap/3f2a9c1e-77b4-4d0a-9e5c-12ab34cd56ef?x=1 failed");
        Assert.Equal("GET https://sim10110.agni.lindenlab.com:12043/<redacted> failed", line);
    }

    [Fact]
    public void A_url_with_an_address_for_a_host_loses_the_address_too()
    {
        Assert.Equal("seed http://<ip>/<redacted>", New().Sanitize("seed http://192.168.1.20:9000/seed/abc"));
    }

    [Fact]
    public void A_bare_url_without_a_path_is_left_alone()
    {
        Assert.Equal("login at https://login.agni.lindenlab.com", New().Sanitize("login at https://login.agni.lindenlab.com"));
    }

    [Theory]
    [InlineData("password=hunter2 rest", "password=<secret> rest")]
    [InlineData("login failed, Password: \"my secret pw\" end", "login failed, Password: <secret> end")]
    [InlineData("session_id=3f2a9c1e-77b4-4d0a-9e5c-12ab34cd56ef", "session_id=<secret>")]
    [InlineData("secure_session_id: abc123", "secure_session_id: <secret>")]
    [InlineData("mfa_hash=XYZ", "mfa_hash=<secret>")]
    [InlineData("Authorization: Bearer eyJhbGciOi", "Authorization: <secret>")]
    [InlineData("sent Bearer eyJhbGciOi to server", "sent Bearer <secret> to server")]
    public void Secrets_are_replaced(string input, string expected)
    {
        Assert.Equal(expected, New().Sanitize(input));
    }

    [Fact]
    public void An_email_address_is_replaced()
    {
        Assert.Equal("contact <email> failed", New().Sanitize("contact someone.name+tag@example.co.uk failed"));
    }

    [Theory]
    [InlineData(@"loading C:\Users\cid80\AppData\Roaming\Godot\x.cfg", @"loading C:\Users\<user>\AppData\Roaming\Godot\x.cfg")]
    [InlineData("loading c:/users/Anna/cache", "loading c:/users/<user>/cache")]
    [InlineData("loading /home/anna/.local/share/x", "loading /home/<user>/.local/share/x")]
    [InlineData("loading /Users/Anna/Library/x", "loading /Users/<user>/Library/x")]
    public void The_user_name_in_a_home_path_is_replaced(string input, string expected)
    {
        Assert.Equal(expected, New().Sanitize(input));
    }

    [Fact]
    public void A_region_handle_is_replaced_but_a_count_is_not()
    {
        Assert.Equal("region <handle> has 12345 prims", New().Sanitize("region 1099511628032000 has 12345 prims"));
    }

    [Fact]
    public void A_registered_name_is_replaced_ignoring_case_and_only_as_a_whole_word()
    {
        var s = New();
        s.AddTerm("Denise Resident");
        string line = s.Sanitize("im from denise resident; DENISE RESIDENT again; DeniseResidentX stays");
        Assert.DoesNotContain("enise", line.Replace("DeniseResidentX", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DeniseResidentX stays", line);
        Assert.Matches(@"^im from <name-[0-9a-f]{6}>; <name-[0-9a-f]{6}> again", line);
    }

    [Fact]
    public void The_same_name_in_any_case_gets_the_same_tag()
    {
        var s = New();
        s.AddTerm("Denise Resident");
        string[] parts = s.Sanitize("denise resident / DENISE RESIDENT").Split(" / ");
        Assert.Equal(parts[0], parts[1]);
    }

    [Fact]
    public void A_longer_registered_term_wins_over_a_shorter_one_inside_it()
    {
        var s = New();
        s.AddTerm("Denise");
        s.AddTerm("Denise Resident");
        string line = s.Sanitize("hello Denise Resident");
        Assert.Matches(@"^hello <name-[0-9a-f]{6}>$", line);
    }

    [Fact]
    public void A_term_with_regex_characters_is_taken_literally()
    {
        var s = New();
        s.AddTerm("Sim (north) [2]+", "region");
        Assert.Matches(@"^at <region-[0-9a-f]{6}> now$", s.Sanitize("at Sim (north) [2]+ now"));
    }

    [Fact]
    public void A_very_short_term_is_ignored()
    {
        var s = New();
        s.AddTerm("Al");
        Assert.Equal("Al went home", s.Sanitize("Al went home"));
    }

    [Fact]
    public void Names_from_a_source_are_picked_up_and_a_new_source_replaces_the_old()
    {
        var names = new List<string> { "Anna Example" };
        var s = New();
        s.RefreshInterval = TimeSpan.Zero;
        s.SetTermSource("names", "name", () => names.ToArray());
        Assert.DoesNotContain("Anna", s.Sanitize("hi Anna Example"));

        names.Add("Boris Example");
        Assert.DoesNotContain("Boris", s.Sanitize("hi Boris Example"));
    }

    [Fact]
    public void A_source_that_throws_does_not_break_logging()
    {
        var s = New();
        s.RefreshInterval = TimeSpan.Zero;
        s.SetTermSource("bad", "name", () => throw new InvalidOperationException("collection was modified"));
        Assert.Equal("plain line", s.Sanitize("plain line"));
    }

    [Fact]
    public void Secrets_inside_a_url_query_go_with_the_query()
    {
        Assert.Equal("open https://example.org/<redacted>",
            New().Sanitize("open https://example.org/login?token=abc&session_id=123"));
    }

    [Fact]
    public void An_empty_or_null_line_comes_back_as_it_is()
    {
        var s = New();
        Assert.Equal("", s.Sanitize(""));
        Assert.Null(s.Sanitize(null!));
    }

    [Fact]
    public void Nothing_that_was_registered_or_matched_survives_a_realistic_line()
    {
        var s = New();
        s.AddTerm("Denise Resident");
        s.AddTerm("Millenium", "region");
        const string line = "[Net] Denise Resident (3f2a9c1e-77b4-4d0a-9e5c-12ab34cd56ef) entered Millenium at 203.0.113.42 via " +
                            "https://sim1.agni.lindenlab.com:12043/cap/abc password=pw handle 1099511628032000 in C:\\Users\\cid80\\x";
        string clean = s.Sanitize(line);
        foreach (var leak in new[] { "Denise", "3f2a9c1e", "Millenium", "203.0.113.42", "/cap/abc", "=pw", "1099511628032000", "cid80" })
            Assert.DoesNotContain(leak, clean);
    }
}
