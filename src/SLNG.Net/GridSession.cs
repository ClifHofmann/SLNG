using LibreMetaverse;

namespace SLNG.Net;

/// <summary>
/// A connection to a single Second Life / OpenSim grid. Wraps the LibreMetaverse
/// <see cref="GridClient"/> and exposes an engine-agnostic login API. This is the
/// seam between the protocol stack and the rest of SLNG: nothing above this layer
/// sees a LibreMetaverse type.
/// </summary>
public sealed class GridSession : IDisposable
{
    private readonly GridClient _client;

    public event EventHandler<ChatMessageEvent>? ChatMessageReceived;
    public event EventHandler<ObjectUpdateEvent>? ObjectUpdateReceived;
    public event EventHandler<TerrainPatchEvent>? TerrainPatchReceived;

    internal void RaiseChatMessage(ChatMessageEvent e) => ChatMessageReceived?.Invoke(this, e);
    internal void RaiseObjectUpdate(ObjectUpdateEvent e) => ObjectUpdateReceived?.Invoke(this, e);
    internal void RaiseTerrainPatch(TerrainPatchEvent e) => TerrainPatchReceived?.Invoke(this, e);

    public GridSession()
    {
        _client = new GridClient();
        _client.Self.ChatFromSimulator += OnChatFromSimulator;
        _client.Objects.ObjectUpdate += OnObjectUpdate;
        _client.Terrain.LandPatchReceived += OnLandPatchReceived;
    }

    private void OnChatFromSimulator(object? sender, ChatEventArgs e)
    {
        ChatMessageReceived?.Invoke(this, new ChatMessageEvent(
            e.FromName,
            e.Message,
            (byte)e.Type));
    }

    private void OnObjectUpdate(object? sender, PrimEventArgs e)
    {
        ObjectUpdateReceived?.Invoke(this, new ObjectUpdateEvent(
            e.Prim.LocalID,
            new System.Numerics.Vector3(e.Prim.Position.X, e.Prim.Position.Y, e.Prim.Position.Z),
            new System.Numerics.Quaternion(e.Prim.Rotation.X, e.Prim.Rotation.Y, e.Prim.Rotation.Z, e.Prim.Rotation.W),
            new System.Numerics.Vector3(e.Prim.Scale.X, e.Prim.Scale.Y, e.Prim.Scale.Z),
            (byte)e.Prim.PrimData.ProfileCurve));
    }

    private void OnLandPatchReceived(object? sender, LandPatchReceivedEventArgs e)
    {
        TerrainPatchReceived?.Invoke(this, new TerrainPatchEvent(
            e.X,
            e.Y,
            e.PatchSize,
            e.HeightMap
        ));
    }

    /// <summary>True once a login has succeeded and the circuit is up.</summary>
    public bool IsConnected => _client.Network.Connected;

    /// <summary>Agent UUID of the logged-in avatar, or empty until connected.</summary>
    public string AgentId => _client.Self.AgentID.ToString();

    /// <summary>
    /// Attempts to log in to the grid described by <paramref name="credentials"/>.
    /// Uses LibreMetaverse's async login API; failures (including unreachable grids)
    /// are returned as a failed <see cref="LoginResult"/> rather than thrown.
    /// </summary>
    public async Task<LoginResult> LoginAsync(LoginCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        try
        {
            var login = new LoginParams(
                _client,
                credentials.FirstName,
                credentials.LastName,
                credentials.Password,
                credentials.Channel,
                credentials.Version,
                credentials.GridLoginUri);

            var response = await _client.Network
                .LoginWithResponseAsync(login, ct)
                .ConfigureAwait(false);

            if (response is null)
            {
                return LoginResult.Fail("no-response", "Grid returned no login response.");
            }

            return response.Success
                ? LoginResult.Ok(response.AgentID.ToString(), response.SessionID.ToString(), response.Message)
                : LoginResult.Fail(response.Reason, response.Message);
        }
        catch (Exception ex)
        {
            // Surface the failure through the result; do not swallow it silently.
            return LoginResult.Fail("exception", ex.Message);
        }
    }

    /// <summary>Logs out if connected. Safe to call when already disconnected.</summary>
    public void Logout()
    {
        if (_client.Network.Connected)
        {
            _client.Network.Logout();
        }
    }

    public void Dispose() => Logout();
}
