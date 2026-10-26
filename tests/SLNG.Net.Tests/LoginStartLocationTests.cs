using System.Net;
using System.Text;
using System.Xml.Linq;
using LibreMetaverse;
using SLNG.Core.Login;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-UI-72 -- the start location the person picked reaches the grid exactly as SLNG.Core built it.
///
/// LibreMetaverse 3.1.6 runs any <c>Start</c> that is not <c>home</c>/<c>last</c> through its own SLURL parser,
/// which is lossy; a string that is already <c>uri:Region&amp;x&amp;y&amp;z</c> passes through unchanged. These tests
/// pin that our canonical string is what BuildLoginParams hands over and what is on the wire -- including that the
/// <c>&amp;</c> is escaped by the serializer and decodes back to a raw one. Nothing here talks to a real grid.
/// </summary>
public class LoginStartLocationTests
{
    private static LoginCredentials Creds() => new()
    {
        FirstName = "Test",
        LastName = "User",
        Password = "$1$00000000000000000000000000fake0",
        GridLoginUri = LoginCredentials.OpenSimLocalLoginUri,
    };

    [Fact]
    public void BuildLoginParams_keeps_a_canonical_uri_start_byte_identical()
    {
        using var client = new GridClient();
        const string start = "uri:Ahern Park&10&20&30";

        var login = GridSession.BuildLoginParams(client, Creds() with { StartLocation = start });

        Assert.Equal(start, login.Start);
    }

    [Fact]
    public void BuildLoginParams_builds_from_the_core_models_string()
    {
        using var client = new GridClient();
        Assert.True(LoginStart.TryParseTyped("secondlife://Ahern%20Park/10/20/30", 256, out var target));

        var login = GridSession.BuildLoginParams(client, Creds() with { StartLocation = target.ToLoginString() });

        Assert.Equal("uri:Ahern Park&10&20&30", login.Start);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildLoginParams_maps_a_missing_start_to_last(string? start)
    {
        using var client = new GridClient();

        var login = GridSession.BuildLoginParams(client, Creds() with { StartLocation = start! });

        Assert.Equal("last", login.Start);
    }

    [Theory]
    [InlineData("last")]
    [InlineData("home")]
    public void BuildLoginParams_passes_last_and_home_through(string start)
    {
        using var client = new GridClient();

        Assert.Equal(start, GridSession.BuildLoginParams(client, Creds() with { StartLocation = start }).Start);
    }

    [Fact]
    public void A_login_without_a_choice_still_starts_at_last()
    {
        // The default of LoginCredentials is what every login did before the picker existed.
        Assert.Equal("last", Creds().StartLocation);
    }

    // ---- the wire ----

    /// <summary>A one-shot HTTP server that records the body it is sent and answers with a login refusal, which is
    /// all a test of the REQUEST needs. Stands in for a grid's login endpoint.</summary>
    private sealed class FakeLoginServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }
        public string LastRequestBody { get; private set; } = "";

        public FakeLoginServer()
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
                    byte[] body = Encoding.UTF8.GetBytes(
                        "<llsd><map><key>login</key><string>false</string><key>reason</key><string>key</string>" +
                        "<key>message</key><string>Could not authenticate.</string></map></llsd>");
                    ctx.Response.ContentType = "application/llsd+xml";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() => _listener.Close();
    }

    /// <summary>The decoded value of a top-level string key in an LLSD-XML request document.</summary>
    private static string? LlsdString(string xml, string key)
    {
        var doc = XDocument.Parse(xml);
        var map = doc.Root!.Element("map")!;
        var keyElement = map.Elements("key").FirstOrDefault(k => k.Value == key);
        return keyElement?.ElementsAfterSelf().FirstOrDefault()?.Value;
    }

    [Theory]
    [InlineData("uri:Ahern Park&10&20&30")]
    [InlineData("uri:Café Plätze&1&2&3")]
    [InlineData("home")]
    [InlineData("last")]
    public async Task The_start_value_on_the_wire_decodes_to_exactly_what_was_given(string start)
    {
        using var server = new FakeLoginServer();
        using var session = new GridSession();

        var result = await session.LoginAsync(Creds() with { GridLoginUri = server.Url, StartLocation = start });

        Assert.False(result.Success);
        Assert.NotEmpty(server.LastRequestBody);
        // The request is an LLSD-XML document: the serializer escapes '&' on the way out, so reading it back with an XML
        // parser must give the raw string again -- which is what the login service sees after ITS parse.
        Assert.Equal(start, LlsdString(server.LastRequestBody, "start"));
    }
}
