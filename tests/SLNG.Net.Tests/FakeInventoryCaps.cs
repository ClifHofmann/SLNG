using System.Net;
using LibreMetaverse;
using LibreMetaverse.StructuredData;

namespace SLNG.Net.Tests;

/// <summary>
/// A local stand-in for a grid's <c>FetchInventoryDescendents2</c> capability: a folder tree held in
/// memory, answered in the LLSD shape the real service uses. LibreMetaverse parses the reply with its
/// own code, so what a test sees is what a session sees — including the parts of that parser that
/// change the store (folder versions, <c>NeedsUpdate</c>, the child-folder records).
/// </summary>
internal sealed class FakeInventoryCaps : IDisposable
{
    internal sealed record Folder(UUID Id, UUID Parent, string Name, int Version);
    internal sealed record Item(UUID Id, UUID Parent, string Name);

    private readonly HttpListener _listener = new();
    private readonly object _gate = new();
    private readonly List<Folder> _folders = new();
    private readonly List<Item> _items = new();
    private readonly UUID _agent;

    public Uri Uri { get; }

    /// <summary>Every folder id any request asked for, in order.</summary>
    public List<UUID> Requested { get; } = new();

    /// <summary>The owner each request named, parallel to <see cref="Requested"/>.</summary>
    public List<UUID> RequestedOwners { get; } = new();

    /// <summary>When set, requests are answered with an empty folder list instead of data.</summary>
    public bool AnswerNothing { get; set; }

    private int _refuse;
    private HttpStatusCode _refuseWith = HttpStatusCode.ServiceUnavailable;

    /// <summary>The next <paramref name="count"/> requests are refused with an HTTP error status and
    /// an empty body — what a rate-limited or overloaded service sends. Counts requests, not
    /// folders.</summary>
    public void RefuseNext(int count, HttpStatusCode status = HttpStatusCode.ServiceUnavailable)
    {
        lock (_gate) { _refuse = count; _refuseWith = status; }
    }

    /// <summary>How many requests arrived, refused ones included.</summary>
    public int RequestCount { get; private set; }

    public FakeInventoryCaps(UUID agent)
    {
        _agent = agent;
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        Uri = new Uri($"http://127.0.0.1:{port}/FetchInventoryDescendents2");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(Serve);
    }

    public void AddFolder(UUID id, UUID parent, string name, int version)
    {
        lock (_gate) _folders.Add(new Folder(id, parent, name, version));
    }

    public void AddItem(UUID id, UUID parent, string name)
    {
        lock (_gate) _items.Add(new Item(id, parent, name));
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            try
            {
                byte[] request;
                using (var ms = new MemoryStream())
                {
                    await ctx.Request.InputStream.CopyToAsync(ms);
                    request = ms.ToArray();
                }
                HttpStatusCode? refusal = null;
                lock (_gate)
                {
                    RequestCount++;
                    if (_refuse > 0) { _refuse--; refusal = _refuseWith; }
                }
                if (refusal is { } status)
                {
                    ctx.Response.StatusCode = (int)status;
                    ctx.Response.ContentLength64 = 0;
                    continue;
                }
                var reply = Answer((OSDMap)OSDParser.Deserialize(request));
                byte[] body = OSDParser.SerializeLLSDXmlBytes(reply);
                ctx.Response.ContentType = "application/llsd+xml";
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body);
            }
            catch { ctx.Response.StatusCode = 500; }
            finally { ctx.Response.Close(); }
        }
    }

    private OSDMap Answer(OSDMap request)
    {
        var folders = new OSDArray();
        lock (_gate)
        {
            foreach (OSD asked in (OSDArray)request["folders"])
            {
                UUID id = ((OSDMap)asked)["folder_id"].AsUUID();
                Requested.Add(id);
                RequestedOwners.Add(((OSDMap)asked)["owner_id"].AsUUID());
                if (AnswerNothing) continue;
                var self = _folders.First(f => f.Id == id);
                var children = _folders.Where(f => f.Parent == id).ToList();
                var items = _items.Where(i => i.Parent == id).ToList();

                var categories = new OSDArray();
                foreach (var c in children)
                    categories.Add(new OSDMap
                    {
                        ["category_id"] = c.Id,
                        ["parent_id"] = c.Parent,
                        ["agent_id"] = _agent,
                        ["name"] = c.Name,
                        ["type_default"] = -1,
                        ["version"] = c.Version,
                    });
                var itemArray = new OSDArray();
                foreach (var i in items)
                    itemArray.Add(new OSDMap
                    {
                        ["item_id"] = i.Id,
                        ["parent_id"] = i.Parent,
                        ["name"] = i.Name,
                        ["desc"] = "",
                        ["asset_id"] = UUID.Random(),
                        ["type"] = (int)AssetType.Object,
                        ["inv_type"] = (int)InventoryType.Object,
                        ["created_at"] = 1_700_000_000,
                        ["flags"] = 0,
                        ["permissions"] = new OSDMap
                        {
                            ["owner_id"] = _agent,
                            ["creator_id"] = _agent,
                            ["last_owner_id"] = _agent,
                            ["group_id"] = UUID.Zero,
                            ["base_mask"] = 0x7FFFFFFF,
                            ["everyone_mask"] = 0,
                            ["group_mask"] = 0,
                            ["next_owner_mask"] = 0x7FFFFFFF,
                            ["owner_mask"] = 0x7FFFFFFF,
                            ["is_owner_group"] = false,
                        },
                        ["sale_info"] = new OSDMap { ["sale_price"] = 10, ["sale_type"] = 0 },
                    });

                folders.Add(new OSDMap
                {
                    ["folder_id"] = id,
                    ["agent_id"] = _agent,
                    ["owner_id"] = _agent,
                    ["name"] = self.Name,
                    ["version"] = self.Version,
                    ["descendents"] = children.Count + items.Count,
                    ["categories"] = categories,
                    ["items"] = itemArray,
                });
            }
        }
        return new OSDMap { ["folders"] = folders };
    }

    public void Dispose() => _listener.Close();
}
