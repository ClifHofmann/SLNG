using System.Net;
using System.Text;
using LibreMetaverse;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-SL-02 -- the pure parts of the multi-factor login: telling the grid's challenge apart from a
/// dead connection and from a wrong password, turning credentials into the login request, deciding
/// what to keep of a remembered <c>mfa_hash</c>, and never letting a secret into a printed line.
///
/// Every code, hash and password below is a made-up placeholder. Nothing here talks to a real grid.
/// </summary>
public class MfaLoginTests
{
    // Placeholders -- recognisable on sight so a leak in an assertion message is obvious.
    private const string FakeToken = "246810";
    private const string FakeHash = "FAKE-MFA-HASH-0123456789";
    private const string FakePassword = "$1$00000000000000000000000000fake0";

    private static LoginCredentials Creds() => new()
    {
        FirstName = "Test",
        LastName = "Resident",
        Password = FakePassword,
        GridLoginUri = LoginCredentials.SecondLifeBetaLoginUri,
    };

    // ---- token handling --------------------------------------------------------------------

    [Theory]
    [InlineData("123456", "123456")]
    [InlineData(" 123 456 ", "123456")]
    [InlineData("123\t456\n", "123456")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeToken_strips_all_whitespace(string? raw, string expected)
        => Assert.Equal(expected, MfaLogin.NormalizeToken(raw));

    [Theory]
    [InlineData("123456", true)]
    [InlineData("123 456", true)]   // as authenticator apps display it
    [InlineData("  000000 ", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12345a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("１２３４５６", false)] // full-width digits are not what the grid expects
    public void IsPlausibleToken_wants_exactly_six_ascii_digits(string? raw, bool expected)
        => Assert.Equal(expected, MfaLogin.IsPlausibleToken(raw));

    // ---- challenge detection ---------------------------------------------------------------

    [Fact]
    public void The_challenge_key_is_the_one_the_reference_viewer_tests_for()
        // lllogininstance.cpp:409 `reason_response == "mfa_challenge"`
        => Assert.Equal("mfa_challenge", LoginResult.MfaChallengeErrorKey);

    [Theory]
    [InlineData("mfa_challenge", true, false, false)]
    [InlineData("key", false, false, false)]         // wrong password
    [InlineData("tos", false, true, false)]
    [InlineData("no-response", false, false, false)] // connection never answered
    [InlineData("presence", false, false, false)]
    [InlineData(null, false, false, false)]
    public void A_challenge_is_its_own_outcome(string? key, bool mfa, bool tos, bool critical)
    {
        var result = LoginResult.Fail(key, "text from the grid");

        Assert.Equal(mfa, result.RequiresMfaToken);
        Assert.Equal(tos, result.RequiresTermsAcceptance);
        Assert.Equal(critical, result.RequiresCriticalAcknowledgement);
        Assert.Equal(mfa, MfaLogin.IsChallenge(key));
    }

    [Fact]
    public void A_success_never_asks_for_a_code()
        => Assert.False(LoginResult.Ok("agent", "session", "welcome", FakeHash).RequiresMfaToken);

    [Theory]
    // A challenge that answers a code we sent: the code was wrong or too old.
    [InlineData(true, "mfa_challenge", true)]
    // The first ask, or a remembered hash the grid no longer takes: nothing was wrong yet.
    [InlineData(false, "mfa_challenge", false)]
    // Some other failure after a code was sent is not a rejected code.
    [InlineData(true, "key", false)]
    [InlineData(true, "tos", false)]
    public void A_repeat_challenge_after_a_code_means_the_code_was_rejected(bool sentToken, string key, bool expected)
        => Assert.Equal(expected, MfaLogin.IsRejectedCode(sentToken, key));

    // ---- grid answer -> result -------------------------------------------------------------

    private static LoginResponseData Answer(LoginState login, string reason, string message, string hash = "")
        => new() { Login = login, Reason = reason, Message = message, MfaHash = hash };

    [Fact]
    public void A_challenge_the_library_reports_as_null_comes_back_as_the_challenge_with_its_hash()
    {
        var result = GridSession.FailureFromGridAnswer(
            Answer(LoginState.False, "mfa_challenge", "Enter your code", FakeHash));

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.True(result.RequiresMfaToken);
        Assert.Equal("Enter your code", result.Message);
        Assert.Equal(FakeHash, result.MfaHash);
    }

    [Fact]
    public void A_wrong_password_is_a_failure_with_its_reason_not_a_missing_response()
    {
        var result = GridSession.FailureFromGridAnswer(
            Answer(LoginState.False, "key", "Could not authenticate your avatar."));

        Assert.Equal("key", result!.ErrorKey);
        Assert.False(result.RequiresMfaToken);
        Assert.Null(result.MfaHash);
    }

    [Fact]
    public void Nothing_is_invented_when_there_was_no_answer_to_read()
    {
        Assert.Null(GridSession.FailureFromGridAnswer(null));
        // A redirect is not a refusal.
        Assert.Null(GridSession.FailureFromGridAnswer(Answer(LoginState.Indeterminate, "", "")));
        // Neither a reason nor a message: nothing to show, so the "no response" path keeps the floor.
        Assert.Null(GridSession.FailureFromGridAnswer(Answer(LoginState.False, "", "")));
        // A success is not a failure.
        Assert.Null(GridSession.FailureFromGridAnswer(Answer(LoginState.True, "", "welcome")));
    }

    [Fact]
    public void A_refusal_with_only_a_message_still_surfaces_it()
    {
        var result = GridSession.FailureFromGridAnswer(Answer(LoginState.False, "", "Account suspended."));

        Assert.Equal("unknown", result!.ErrorKey);
        Assert.Equal("Account suspended.", result.Message);
    }

    // ---- credentials -> login parameters ---------------------------------------------------

    [Fact]
    public void A_plain_login_sends_no_mfa_fields()
    {
        using var client = new GridClient();

        var login = GridSession.BuildLoginParams(client, Creds());

        Assert.False(login.MfaEnabled);
        Assert.Equal("", login.Token);
        Assert.Equal("", login.MfaHash);
    }

    [Fact]
    public void A_code_switches_the_fields_on_and_is_normalised()
    {
        using var client = new GridClient();

        var login = GridSession.BuildLoginParams(client, Creds() with { MfaToken = " 246 810 " });

        Assert.True(login.MfaEnabled);
        Assert.Equal(FakeToken, login.Token);
        Assert.Equal("", login.MfaHash);
    }

    [Fact]
    public void A_remembered_hash_alone_switches_the_fields_on()
    {
        using var client = new GridClient();

        var login = GridSession.BuildLoginParams(client, Creds() with { MfaHash = FakeHash });

        Assert.True(login.MfaEnabled);
        Assert.Equal("", login.Token);
        Assert.Equal(FakeHash, login.MfaHash);
    }

    [Fact]
    public void The_library_default_for_the_mfa_switch_is_off_so_building_must_set_it()
    {
        // Documents the trap: left alone, LibreMetaverse would drop token and mfa_hash from the
        // request and every account with MFA would be challenged for ever.
        using var client = new GridClient();
        Assert.False(client.Settings.Connection.MfaEnabled);
    }

    [Fact]
    public void Building_a_request_does_not_touch_the_clients_shared_settings()
    {
        using var client = new GridClient();

        GridSession.BuildLoginParams(client, Creds() with { MfaToken = FakeToken });

        Assert.False(client.Settings.Connection.MfaEnabled);
    }

    [Fact]
    public void The_consent_flags_still_default_to_false_with_mfa_material_present()
    {
        using var client = new GridClient();

        var login = GridSession.BuildLoginParams(client, Creds() with { MfaToken = FakeToken, MfaHash = FakeHash });

        Assert.False(login.AgreeToTos);
        Assert.False(login.ReadCritical);
    }

    // ---- what to keep of the hash ----------------------------------------------------------

    [Theory]
    //          saveLogin challenged remember returned          -> action
    [InlineData(true, false, false, FakeHash, MfaHashAction.Store)] // grid refreshed it on an ordinary login
    [InlineData(true, false, false, null, MfaHashAction.Keep)]  // nothing new, the stored one just worked
    [InlineData(true, true, true, FakeHash, MfaHashAction.Store)] // answered the challenge and ticked remember
    [InlineData(true, true, false, FakeHash, MfaHashAction.Erase)] // answered it, did not tick remember
    [InlineData(true, true, true, null, MfaHashAction.Erase)] // remember, but the grid sent no hash: the old one is stale
    [InlineData(false, false, false, FakeHash, MfaHashAction.Erase)] // "Save Login" off: keep nothing
    [InlineData(false, true, true, FakeHash, MfaHashAction.Erase)]
    public void The_stored_hash_follows_the_reference_viewers_rule(
        bool saveLogin, bool challenged, bool remember, string? returned, MfaHashAction expected)
        => Assert.Equal(expected, MfaLogin.DecideStorage(saveLogin, challenged, remember, returned));

    [Theory]
    [InlineData("a1b2c3|1700000000", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("line\nbreak", false)]
    [InlineData("tab\there", false)]
    public void Only_a_plain_short_hash_is_kept(string? hash, bool expected)
        => Assert.Equal(expected, MfaLogin.IsStorableHash(hash));

    [Fact]
    public void A_runaway_hash_is_not_kept()
        => Assert.False(MfaLogin.IsStorableHash(new string('x', MfaLogin.MaxHashLength + 1)));

    [Fact]
    public void An_unusable_hash_returned_by_the_grid_is_treated_as_none()
    {
        Assert.Equal(MfaHashAction.Keep, MfaLogin.DecideStorage(true, false, false, "bad\nvalue"));
        Assert.Equal(MfaHashAction.Erase, MfaLogin.DecideStorage(true, true, true, "bad\nvalue"));
    }

    // ---- no secret in anything printable ---------------------------------------------------

    [Fact]
    public void Credentials_print_without_password_code_or_hash()
    {
        var creds = Creds() with { MfaToken = FakeToken, MfaHash = FakeHash };

        string text = creds.ToString();
        string interpolated = $"{creds}";

        foreach (string s in new[] { text, interpolated })
        {
            Assert.DoesNotContain(FakeToken, s);
            Assert.DoesNotContain(FakeHash, s);
            Assert.DoesNotContain(FakePassword, s);
            Assert.Contains("Test", s); // still useful for a log line
        }
    }

    [Fact]
    public void Credentials_with_nothing_secret_say_so()
    {
        string text = (Creds() with { Password = "" }).ToString();

        Assert.Contains("MfaToken = [none]", text);
        Assert.Contains("MfaHash = [none]", text);
    }

    [Fact]
    public void A_result_prints_without_its_hash()
    {
        string failed = LoginResult.Fail("mfa_challenge", "Enter your code", FakeHash).ToString();
        string ok = LoginResult.Ok("agent", "session", "welcome", FakeHash).ToString();

        Assert.DoesNotContain(FakeHash, failed);
        Assert.DoesNotContain(FakeHash, ok);
        Assert.Contains("MfaHash = [set]", failed);
    }

    [Fact]
    public void An_empty_hash_is_no_hash()
    {
        Assert.Null(LoginResult.Ok("a", "s", "m", "").MfaHash);
        Assert.Null(LoginResult.Fail("key", "m", "").MfaHash);
    }

    // ---- the whole exchange against a local stand-in for the login server -------------------

    /// <summary>A one-shot HTTP server that records the body it was sent and answers with the
    /// LLSD it was given. Stands in for a grid's login endpoint: no real grid is involved.</summary>
    private sealed class FakeLoginServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }
        public string LastRequestBody { get; private set; } = "";

        public FakeLoginServer(string responseLlsd)
        {
            int port;
            using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                        LastRequestBody = await reader.ReadToEndAsync();
                    byte[] body = Encoding.UTF8.GetBytes(responseLlsd);
                    ctx.Response.ContentType = "application/llsd+xml";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() => _listener.Close();
    }

    private static string ChallengeLlsd(string hash) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><llsd><map>" +
        "<key>login</key><string>false</string>" +
        "<key>reason</key><string>mfa_challenge</string>" +
        "<key>message</key><string>Please enter your authentication code.</string>" +
        (hash.Length > 0 ? $"<key>mfa_hash</key><string>{hash}</string>" : "") +
        "</map></llsd>";

    [Fact]
    public async Task A_challenge_from_the_server_reaches_the_caller_as_a_challenge_and_the_request_carries_the_code()
    {
        using var server = new FakeLoginServer(ChallengeLlsd(FakeHash));
        using var session = new GridSession();
        var creds = Creds() with { GridLoginUri = server.Url, MfaToken = " 246 810 ", MfaHash = "OLD-HASH" };

        var result = await session.LoginAsync(creds);

        Assert.False(result.Success);
        Assert.True(result.RequiresMfaToken);
        Assert.Equal("Please enter your authentication code.", result.Message);
        Assert.Equal(FakeHash, result.MfaHash);
        // What actually went over the wire (an LLSD document: <key>token</key><string>...</string>).
        Assert.Contains("<key>token</key><string>246810</string>", server.LastRequestBody);
        Assert.Contains("<key>mfa_hash</key><string>OLD-HASH</string>", server.LastRequestBody);
        Assert.Contains("<key>agree_to_tos</key><boolean>0</boolean>", server.LastRequestBody);
    }

    [Fact]
    public async Task A_login_without_mfa_material_sends_neither_field()
    {
        using var server = new FakeLoginServer(ChallengeLlsd(""));
        using var session = new GridSession();

        var result = await session.LoginAsync(Creds() with { GridLoginUri = server.Url });

        Assert.True(result.RequiresMfaToken);
        Assert.Null(result.MfaHash);
        Assert.DoesNotContain("<key>token</key>", server.LastRequestBody);
        Assert.DoesNotContain("<key>mfa_hash</key>", server.LastRequestBody);
    }

    [Fact]
    public async Task A_second_attempt_on_the_same_session_is_judged_on_its_own_answer()
    {
        // The Boot loop retries on one session: the first answer must not colour the second.
        using var first = new FakeLoginServer(ChallengeLlsd(FakeHash));
        using var wrong = new FakeLoginServer(
            "<llsd><map><key>login</key><string>false</string><key>reason</key><string>key</string>" +
            "<key>message</key><string>Could not authenticate.</string></map></llsd>");
        using var session = new GridSession();

        var a = await session.LoginAsync(Creds() with { GridLoginUri = first.Url });
        var b = await session.LoginAsync(Creds() with { GridLoginUri = wrong.Url, MfaToken = FakeToken });

        Assert.True(a.RequiresMfaToken);
        Assert.Equal("key", b.ErrorKey);
        Assert.False(b.RequiresMfaToken);
        Assert.Null(b.MfaHash);
    }
}
