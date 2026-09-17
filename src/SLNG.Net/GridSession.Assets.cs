using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using LibreMetaverse;
using LibreMetaverse.Imaging;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Microsoft.Extensions.Logging;
using SLNG.Core;

namespace SLNG.Net;

// GridSession, Assets part.
//
// Asset fetches that need the session's capabilities: legacy materials, meshes,
// textures (HTTP range and bake CDN), and animations.
//
// Split out of the single 10,042-line GridSession.cs -- a pure move, no member
// changed. The class is still one type; `partial` only spreads it over files that
// can be read, reviewed and merged independently.
public sealed partial class GridSession
{
    /// <summary>
    /// Fetches the raw bytes of a mesh asset from the simulator. Returns a neutral
    /// payload — no LibreMetaverse type crosses this boundary; decoding lives in
    /// <c>SLNG.Assets</c>.
    /// </summary>
    /// <summary>Fetches legacy Blinn-Phong materials by id from the region's
    /// <c>RenderMaterials</c> capability, as neutral <see cref="LegacyMaterialData"/>.
    ///
    /// <para>Materials are NOT assets: they live in a per-region capability whose request and
    /// response bodies are zlib-compressed LLSD, and a request carries at most 50 ids
    /// (MATERIALS_GET_MAX_ENTRIES, llmaterialmgr.cpp:58). LibreMetaverse implements all of that,
    /// so this method's job is batching to that limit and converting at the boundary.</para>
    ///
    /// <para>Returns only what the sim actually returned -- an id it does not know is simply
    /// absent from the result, never a default-valued entry, so the caller can tell "resolved to
    /// a material with no maps" from "never resolved".</para></summary>
    public async Task<IReadOnlyList<LegacyMaterialData>> FetchLegacyMaterialsAsync(
        IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken = default)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null || materialIds.Count == 0) return Array.Empty<LegacyMaterialData>();

        // BUG-RENDER-06 follow-up: leaf faces on Agni carry a real legacy-material id
        // (seen in [FaceParams] as mat=...) but the whole material never resolved, so every such
        // face fell back to DetectAlpha() and landed in the sorted transparent pass -> flicker.
        // This says which link in the chain is broken: no RenderMaterials cap on this region at
        // all, vs. the cap present but the fetch coming back with fewer (or zero) materials than
        // asked for. One line per fetch call (already batched/debounced upstream in AssetService).
        var capUri = sim.Caps?.CapabilityURI("RenderMaterials");
        var result = new List<LegacyMaterialData>(materialIds.Count);
        var batch = new List<LibreMetaverse.UUID>(MaterialsPerRequest);

        foreach (var id in materialIds)
        {
            if (id == Guid.Empty) continue;
            batch.Add(new LibreMetaverse.UUID(id));
            if (batch.Count < MaterialsPerRequest) continue;
            await FetchOneBatchAsync(sim, batch, result, cancellationToken).ConfigureAwait(false);
            batch.Clear();
        }
        if (batch.Count > 0)
            await FetchOneBatchAsync(sim, batch, result, cancellationToken).ConfigureAwait(false);

        Console.Error.WriteLine($"[LegacyMat] requested {materialIds.Count} -> resolved {result.Count} " +
            $"(RenderMaterials cap {(capUri == null ? "MISSING on this region" : "present")})");

        return result;
    }

    /// <summary>MATERIALS_GET_MAX_ENTRIES (llmaterialmgr.cpp:58). The sim rejects more.</summary>
    private const int MaterialsPerRequest = 50;

    /// <summary>BUG-RENDER-06 follow-up: LibreMetaverse 3.1.3's <c>ObjectManager.RequestMaterialsAsync</c>
    /// builds the RenderMaterials query by <c>array.Add(uuid)</c>, which serialises each id as an
    /// LLSD <c>uuid</c> element. The real viewer sends each id as an LLSD <b>binary(16)</b>:
    /// <c>llmaterialmgr.cpp</c> <c>processGetQueue</c> does <c>materialsData.append((*itMaterial).asLLSD())</c>,
    /// and <c>LLMaterialID::asLLSD()</c> is a 16-byte <c>LLSD::Binary</c> (llmaterialid.cpp). The sim
    /// unzips the request and reads each entry with <c>.asBinary()</c>, which yields nothing for a
    /// <c>uuid</c>-typed element -- so LMV's request matches zero materials and the cap returns an
    /// empty result with no error. Every Blinn-Phong-materialled face on SL then fell back to
    /// <c>Image.DetectAlpha()</c>, and a soft-alpha foliage texture guessed as BLEND lands in the
    /// sorted transparent pass -> the leaf-canopy flicker report. We build and POST the query
    /// ourselves so the ids go out as binary; the response shape is unchanged, so LMV's own
    /// <c>LegacyMaterial(OSDMap)</c> still parses each returned entry.</summary>
    internal static OSDMap BuildRenderMaterialsQuery(IEnumerable<LibreMetaverse.UUID> ids)
    {
        var array = new OSDArray();
        foreach (var id in ids) array.Add(OSD.FromBinary(id.GetBytes()));
        return new OSDMap { ["Zipped"] = OSD.FromBinary(Helpers.ZCompressOSD(array)) };
    }

    private async Task FetchOneBatchAsync(
        LibreMetaverse.Simulator sim, List<LibreMetaverse.UUID> ids,
        List<LegacyMaterialData> into, CancellationToken cancellationToken)
    {
        var uri = sim.Caps?.CapabilityURI("RenderMaterials");
        if (uri == null) return;

        try
        {
            var request = BuildRenderMaterialsQuery(ids);
            var (res, data) = await _client.HttpCapsClient
                .PostAsync(uri, OSDFormat.Xml, request, cancellationToken).ConfigureAwait(false);

            int status = (int)(res?.StatusCode ?? 0);
            if (data == null || data.Length == 0)
            {
                Console.Error.WriteLine($"[LegacyMat] POST {status}: empty body for {ids.Count} ids");
                return;
            }

            if (OSDParser.Deserialize(data) is not OSDMap top || !top.ContainsKey("Zipped"))
            {
                Console.Error.WriteLine($"[LegacyMat] POST {status}: no Zipped field; body starts: " +
                    System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 200)));
                return;
            }

            if (Helpers.ZDecompressOSD(top["Zipped"].AsBinary()) is not OSDArray mats)
            {
                Console.Error.WriteLine($"[LegacyMat] POST {status}: unzipped payload is not an array");
                return;
            }

            int before = into.Count;
            foreach (var entry in mats)
            {
                if (entry is not OSDMap em) continue;
                try { into.Add(ToLegacyMaterialData(new LibreMetaverse.Materials.LegacyMaterial(em))); }
                catch (Exception ex) { Console.Error.WriteLine($"[LegacyMat] entry parse failed: {ex.Message}"); }
            }
            Console.Error.WriteLine($"[LegacyMat] POST {status}: {ids.Count} ids -> {into.Count - before} materials");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LegacyMat] request for {ids.Count} legacy materials failed: {ex.Message}");
        }
    }

    /// <summary>Converts LibreMetaverse's <c>LegacyMaterial</c> to the neutral value. Public and
    /// static so the conversion is testable on its own -- it is the only place the two type
    /// systems meet, and it is where a units mistake (the wire scales every float by 10000, and
    /// the specular tint arrives 0-255) would otherwise be invisible until something rendered
    /// wrong.</summary>
    public static LegacyMaterialData ToLegacyMaterialData(LibreMetaverse.Materials.LegacyMaterial m)
    {
        // SpecularColor is already 0-1 on LibreMetaverse's side; the wire's 0-255 form is decoded
        // by its OSD reader. It becomes a Vector4 here so nothing downstream needs an LMV type.
        return new LegacyMaterialData(
            m.ID.Guid,
            m.NormalMap.Guid,
            new System.Numerics.Vector2((float)m.NormalMapOffsetX, (float)m.NormalMapOffsetY),
            new System.Numerics.Vector2((float)m.NormalMapRepeatX, (float)m.NormalMapRepeatY),
            (float)m.NormalMapRotation,
            m.SpecularMap.Guid,
            new System.Numerics.Vector2((float)m.SpecularMapOffsetX, (float)m.SpecularMapOffsetY),
            new System.Numerics.Vector2((float)m.SpecularMapRepeatX, (float)m.SpecularMapRepeatY),
            (float)m.SpecularMapRotation,
            new System.Numerics.Vector4(m.SpecularColor.R, m.SpecularColor.G, m.SpecularColor.B, m.SpecularColor.A),
            m.SpecularExponent,
            m.EnvironmentIntensity,
            m.AlphaMaskCutoff,
            (LegacyDiffuseAlphaMode)(byte)m.DiffuseAlphaMode);
    }

    public async Task<byte[]?> FetchMeshDataAsync(Guid meshId)
    {
        var asset = await _client.Assets
            .RequestMeshAsync(new UUID(meshId), CancellationToken.None)
            .ConfigureAwait(false);
        return asset?.AssetData;
    }

    private static readonly HttpClient _textureHttpClient = new();

    private static readonly System.Threading.SemaphoreSlim _textureFetchSemaphore = new System.Threading.SemaphoreSlim(8, 8);

    /// <summary>
    /// Fetches the raw bytes of a texture asset (JPEG2000) from the simulator. Returns null
    /// if the fetch times out or fails.
    /// </summary>
    /// <param name="desiredDiscard">SL/OpenSim J2K discard level to request: 0 = full resolution
    /// up to <see cref="J2kByteSizeEstimator.MaxDiscardLevel"/> = coarsest. FEAT-PERF-02 Phase 2:
    /// a higher discard level makes the SIMULATOR send fewer bytes (verified against OpenSim's
    /// GetTextureHandler/J2KImage source, see docs/specs/FEAT-PERF-02-texture-loading-speed.md's
    /// Phase 2.1 write-up), not just a client-side decode/display hint.</param>
    /// <param name="skipHttp">Forces the UDP path. Set by the caller when a previous attempt's
    /// HTTP body arrived intact-looking but would not decode: retrying HTTP just re-fetches the
    /// identical bytes, so without this the UDP fallback is unreachable for exactly the assets
    /// that need it most (see AssetService's retry loop).</param>
    public struct TextureFetchResult
    {
        public byte[]? Data;
        public bool IsReliable;
        /// <summary>The sim's own GetTexture/ViewerAsset cap answered 403/401 -- a permission
        /// decision, not a transient error. No transport will get these bytes; the caller should
        /// stop retrying rather than fall this back onto LibreMetaverse's UDP pipeline (which just
        /// re-hits the same wall and spams "Failed to fetch texture ... Forbidden"). Only the
        /// generic per-face path sets this -- the bake path (BUG-AVATAR-02) has a real fallback.</summary>
        public bool Gone;
    }

    /// <summary>BUG-AVATAR-02: fetches an avatar BAKE texture. Every single bake channel was
    /// coming back <c>HTTP 403</c> ("AccessDenied", a raw S3 error body) through the normal asset
    /// path (<see cref="FetchTextureDataAsync"/>) -- confirmed by capturing the same texture id
    /// fetched successfully by Firestorm: it used a completely different host and URL shape,
    /// <c>http://bake-texture.glb.{grid}.lindenlab.com/texture/{agent}/{slot}/{textureId}</c>, not
    /// the generic <c>GetTexture</c>/<c>ViewerAsset</c> CAP's <c>?texture_id=</c> query. That host
    /// is real, documented reference-viewer infrastructure -- <c>llappcorehttp.h</c> lists
    /// <c>bake-texture</c> as its own HTTP connection-pool destination, distinct from the general
    /// asset <c>cdn</c> -- not something a capability hands out; the reference viewer constructs it
    /// itself from the grid name, the agent id, and the bake slot name (see
    /// <see cref="BakeChannelNames"/> for where the eleven slot-name strings come from).
    ///
    /// Only meaningful on a Linden grid (<see cref="_lindenGridShortName"/> is null everywhere
    /// else -- OpenSim has no such host); returns a failed result immediately otherwise so the
    /// caller can fall back to the normal path without paying for a fetch that cannot succeed.
    /// Always a full fetch (no Range/desiredDiscard) -- the reference viewer does the same for
    /// baked textures, to reduce interim blurring while the bake streams in.
    ///
    /// <paramref name="agentId"/> is the id of the avatar WEARING this bake, and it is part of the
    /// URL path -- the reference viewer builds it from <c>LLVOAvatar::getID()</c>
    /// (<c>llvoavatar.cpp</c> <c>getImageURL</c>: <c>url = ... + "texture/" + getID().asString() +
    /// "/" + mDefaultImageName + "/" + uuid.asString()</c>), which is the DISPLAYED avatar, not the
    /// viewing agent. Passing our own id for someone else's bake gets a flat 403 from the CDN and
    /// then a second 403 from the generic fallback, leaving every other mesh-body avatar untextured
    /// (found live on Agni 2026-09-02: only the local avatar's own bakes ever resolved). Empty
    /// falls back to our own id so a caller that only ever deals with the local avatar can omit
    /// it.</summary>
    public async Task<TextureFetchResult> FetchBakeTextureDataAsync(Guid textureId, int bakeChannel, Guid agentId = default, CancellationToken ct = default)
    {
        if (_lindenGridShortName == null) return new TextureFetchResult { Data = null, IsReliable = false };

        var slot = SLNG.Core.BakeChannelNames.NameFor(bakeChannel);
        if (slot == null) return new TextureFetchResult { Data = null, IsReliable = false };

        var owner = agentId == Guid.Empty ? _client.Self.AgentID.Guid : agentId;
        var url = BuildBakeTextureUrl(_lindenGridShortName, owner, slot, textureId);
        var bytes = await FetchTextureViaHttpRangeAsync(textureId, desiredDiscard: 0, capUri: null, maxRetries: 2, fetchUrl: url).ConfigureAwait(false);
        return new TextureFetchResult { Data = bytes, IsReliable = bytes != null };
    }

    /// <summary>Builds the dedicated bake-texture CDN URL -- pulled out as a pure function so a
    /// test can pin the shape (and specifically that the OWNING avatar's id, not the viewer's,
    /// lands in the path). Mirrors <c>LLVOAvatar::getImageURL</c>; see
    /// <see cref="FetchBakeTextureDataAsync"/>.</summary>
    internal static Uri BuildBakeTextureUrl(string gridShortName, Guid agentId, string slot, Guid textureId) =>
        new($"http://bake-texture.glb.{gridShortName}.lindenlab.com/texture/{agentId}/{slot}/{textureId}");

    public async Task<TextureFetchResult> FetchTextureDataAsync(Guid textureId, int desiredDiscard = 0, bool skipHttp = false)
    {
        // FEAT-PERF-02 Phase 2: prefer our own HTTP GetTexture Range fetch over the UDP path
        // below. Two wins over the pre-existing code: (1) HTTP is the faster transport (no UDP
        // packet/ACK overhead or agent-throttle pacing) even for a full (discard 0) fetch --
        // LibreMetaverse's own built-in HTTP texture path was configured as preferred
        // (UseHttpTextures=true, see the TexturePipeline setup above) but was never actually
        // reached, because the reflection call below always targets the UDP TexturePipeline
        // regardless of that setting; (2) for discard > 0, a Range request makes the SIMULATOR
        // send fewer bytes -- LibreMetaverse's built-in HTTP fetch (AssetManager.
        // HttpRequestTexture) ignores discardLevel/priority entirely and always downloads the
        // whole asset, so it can't do this at all, which is why this method builds the HTTP
        // request itself instead of calling into LibreMetaverse's HTTP path.
        // Already proven permanently denied (403/401) by the generic cap earlier this session --
        // don't re-run the HTTP attempts and, crucially, don't fall through to LibreMetaverse's
        // UDP pipeline, which re-hits the same wall and spams its own logger (a remote avatar's
        // hair face flickering all session, 2026-09-03). A relog / restart clears the set.
        if (_permanentlyDeniedTextures.ContainsKey(textureId))
            return new TextureFetchResult { Data = null, IsReliable = true, Gone = true };

        // Already 403'd on HTTP this session: skip the caps entirely (a permission decision will
        // not have changed) but still go down to the UDP pipeline below -- see _httpDeniedTextures.
        bool httpDenied = _httpDeniedTextures.ContainsKey(textureId);
        skipHttp = skipHttp || httpDenied;

        var capUri = skipHttp ? null : _client.Network.CurrentSim?.Caps?.GetTextureCapURI();
        var viewerAssetCap = skipHttp ? null : _client.Network.CurrentSim?.Caps?.CapabilityURI("ViewerAsset");
        var getTextureCap = skipHttp ? null : _client.Network.CurrentSim?.Caps?.CapabilityURI("GetTexture");

        if (viewerAssetCap != null)
        {
            var httpResult = await FetchTextureViaHttpRangeAsync(textureId, desiredDiscard, viewerAssetCap).ConfigureAwait(false);
            if (httpResult != null) return new TextureFetchResult { Data = httpResult, IsReliable = true };
        }

        if (getTextureCap != null && getTextureCap != viewerAssetCap)
        {
            var httpResult = await FetchTextureViaHttpRangeAsync(textureId, desiredDiscard, getTextureCap, maxRetries: 5).ConfigureAwait(false);
            if (httpResult != null) return new TextureFetchResult { Data = httpResult, IsReliable = true };
        }

        // A cap attempt just above may have marked this 403/401. That is NOT the end of the road:
        // Firestorm shows textures the generic cap refuses us, so the UDP pipeline below gets one
        // attempt. It is only recorded as genuinely gone if that comes back empty too.
        bool deniedByCaps = httpDenied || _httpDeniedTextures.ContainsKey(textureId);

        // Every "UDP produced nothing" exit goes through this. When the caps already refused the
        // id, that combination -- 403 on HTTP AND empty over UDP -- is the only evidence strong
        // enough to call a texture genuinely gone and stop asking for the rest of the session.
        // Without the caps denial an empty UDP result is just a normal transient failure and
        // AssetService's own 45 s negative cache handles it.
        TextureFetchResult UdpGaveNothing(byte[]? data)
        {
            if (data is { Length: > 0 }) return new TextureFetchResult { Data = data, IsReliable = false };
            if (deniedByCaps && _permanentlyDeniedTextures.TryAdd(textureId, 0))
                Console.Error.WriteLine($"[TextureFetch] {textureId}: 403 on HTTP and nothing over UDP -- giving up for this session");
            return new TextureFetchResult { Data = null, IsReliable = deniedByCaps, Gone = deniedByCaps };
        }

        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var pipeline = typeof(AssetManager).GetField("Texture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(_client.Assets);
            if (pipeline == null)
                return UdpGaveNothing(UdpFailed(textureId, "AssetManager.Texture field not found (reflection)"));
            {
                var reqMethod = pipeline.GetType().GetMethod("RequestTexture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (reqMethod == null)
                    return UdpGaveNothing(UdpFailed(textureId, "TexturePipeline.RequestTexture not found (reflection)"));
                {
                    var callbackType = reqMethod.GetParameters()[5].ParameterType;
                    Action<TextureRequestState, LibreMetaverse.Assets.AssetTexture> action = (state, assetTexture) =>
                    {
                        if (state == TextureRequestState.Finished)
                        {
                            var data = assetTexture?.AssetData;
                            tcs.TrySetResult(data is { Length: > 0 } ? data : null);
                        }
                        else if (state == TextureRequestState.NotFound || state == TextureRequestState.Aborted || state == TextureRequestState.Timeout)
                        {
                            UdpFailed(textureId, $"pipeline reported {state}");
                            tcs.TrySetResult(null);
                        }
                    };
                    var delegateObj = Delegate.CreateDelegate(callbackType, action.Target, action.Method);

                    // RequestTexture(UUID textureID, ImageType imageType, float priority, int discardLevel,
                    //                uint packetStart, TextureDownloadCallback callback, bool progressive).
                    // discardLevel now passed through instead of hardcoded 0 -- this UDP path DOES
                    // honor it server-side (unlike LibreMetaverse's HTTP path), so even this
                    // fallback benefits from a non-zero desiredDiscard when HTTP isn't reachable.
                    reqMethod.Invoke(pipeline, new object[] { new UUID(textureId), ImageType.Normal, 100000.0f, desiredDiscard, 0u, delegateObj, false });

                    // The pipeline can simply never call back -- e.g. if it was never started
                    // because the client is configured to prefer HTTP textures. Awaiting the bare
                    // TaskCompletionSource would then hang until AssetService's own 60 s timeout,
                    // three times per texture, with nothing in the log to say why. Bound it here
                    // and name it instead.
                    var udpTimeout = Task.Delay(TimeSpan.FromSeconds(20));
                    if (await Task.WhenAny(tcs.Task, udpTimeout).ConfigureAwait(false) != tcs.Task)
                        return UdpGaveNothing(UdpFailed(textureId, "TexturePipeline never called back within 20s"));

                    var udpBytes = await tcs.Task.ConfigureAwait(false);
                    if (udpBytes is { Length: > 0 })
                    {
                        if (deniedByCaps)
                            Console.Error.WriteLine($"[TextureFetch] {textureId}: HTTP 403 but UDP delivered {udpBytes.Length} bytes -- the asset exists, the CDN just would not serve it");
                        return new TextureFetchResult { Data = udpBytes, IsReliable = false };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GridSession] TexturePipeline reflection failed: {ex.Message}");
        }

        // Fallback if reflection fails
        var fallbackTcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _client.Assets.RequestImageAsync(new UUID(textureId), ImageType.Normal, CancellationToken.None)
            .ContinueWith(t =>
            {
                var data = t.Result?.AssetData;
                fallbackTcs.TrySetResult(data is { Length: > 0 } ? data : null);
            });

        return UdpGaveNothing(await fallbackTcs.Task);
    }

    /// <summary>
    /// Issues our own HTTP GET against the region's GetTexture capability, with a Range header
    /// when <paramref name="desiredDiscard"/> is above 0 -- see <see cref="FetchTextureDataAsync"/>'s
    /// doc comment for why LibreMetaverse's own HTTP path can't do this. A partial (206) response
    /// is treated exactly like a full (200) one -- the caller (AssetService) knows this data may
    /// be truncated-on-purpose and routes it to the tolerant decoder, same as any other incomplete
    /// J2C stream. Returns null on ANY failure (non-success status, network error) so the caller
    /// falls back to the UDP path -- never throws.
    /// </summary>
    // Why a texture fetch gave up, once per texture id. The HTTP path has five separate silent
    // "return null" exits and the UDP fallback a sixth, all of which surfaced to the renderer as
    // the same "fetch/decode returned null" line -- which is how 14 textures could fail
    // consistently in one view with no way to tell a missing asset from a rejected codestream.
    // Deduped because a failing texture is retried and re-requested by every face that uses it.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _fetchFailureLogged = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _udpFailureLogged = new();

    // Texture ids the sim's own GetTexture/ViewerAsset cap answered 403/401 for. A permission
    // decision does not change within a session, so HTTP is never retried for these -- that
    // unwinnable retry loop was the flickering remote-avatar hair face (2026-09-03).
    // NOT populated for the bake path (fetchUrl != null): BUG-AVATAR-02's bake 403 has a real
    // fallback to the generic cap.
    //
    // v0.20.40 also skipped the UDP fallback for these, on the reasoning that "no transport will
    // get the bytes". That reasoning was wrong, and the user disproved it: Firestorm displays the
    // very texture SLNG gives up on (dda710d4, a remote avatar's hair, confirmed 2026-09-03). The
    // asset EXISTS and is servable -- the CDN just will not serve it over the generic cap. So an
    // HTTP 403 now costs exactly ONE UDP attempt through LibreMetaverse's legacy image transfer,
    // the same transport the reference viewer falls back to; only if that also comes back empty is
    // the id recorded as genuinely gone.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _httpDeniedTextures = new();

    /// <summary>Ids that 403'd on HTTP **and** produced nothing over UDP. Session-permanent, and
    /// the only set that makes <see cref="TextureFetchResult.Gone"/> true -- an id in
    /// <see cref="_httpDeniedTextures"/> alone is still worth one UDP attempt per request.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _permanentlyDeniedTextures = new();

    private static byte[]? UdpFailed(Guid textureId, string reason)
    {
        if (_udpFailureLogged.TryAdd(textureId, 0))
            Console.Error.WriteLine($"[TextureFetch] {textureId}: UDP fallback failed: {reason}");
        return null;
    }

    private static byte[]? FetchFailed(Guid textureId, string reason)
    {
        if (_fetchFailureLogged.TryAdd(textureId, 0))
            Console.Error.WriteLine($"[TextureFetch] {textureId} HTTP fetch gave up: {reason} — falling back to UDP");
        return null;
    }

    /// <summary><paramref name="fetchUrl"/>, when given, is used verbatim instead of the usual
    /// <c>{capUri}?texture_id={id}</c> shape -- BUG-AVATAR-02's bake-texture fetch needs a
    /// completely different URL (<c>/texture/&lt;agent&gt;/&lt;slot&gt;/&lt;id&gt;</c>, no query
    /// string, always a full fetch) but the same validation and retry logic below, which is why
    /// this exists as an extra parameter here rather than a copy of the whole method.</summary>
    private async Task<byte[]?> FetchTextureViaHttpRangeAsync(Guid textureId, int desiredDiscard, Uri? capUri, int maxRetries = 0, Uri? fetchUrl = null)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var url = fetchUrl ?? new Uri($"{capUri}?texture_id={textureId}");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (desiredDiscard > 0)
                {
                    int byteLimit = J2kByteSizeEstimator.CalcDataSizeJ2C(0, 0, desiredDiscard);
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, byteLimit - 1);
                }

                HttpResponseMessage? response = null;
                byte[]? bytes = null;
                long? declaredLength = null;
                await _textureFetchSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    response = await _textureHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        // Forbidden is deliberately NOT retryable, unlike ServiceUnavailable and
                        // NotFound. A 403 is a permission decision the server already made -- no
                        // amount of retrying changes it, and retrying it anyway (5 attempts, 2s
                        // apart) is exactly what turned one denied texture into repeated "Failed
                        // to fetch texture ... Forbidden" log spam and real, wasted load against
                        // the sim. 503/404 stay retryable: a 503 can be genuinely transient, and a
                        // 404 can be an asset that was just created and has not propagated yet.
                        bool retryable = response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                                      || response.StatusCode == System.Net.HttpStatusCode.NotFound;
                        if (attempt < maxRetries && retryable)
                        {
                            // Will retry. Release semaphore and delay.
                        }
                        else
                        {
                            // A 403/401 from the generic cap is permanent -- remember it so the
                            // caller skips the UDP fallback (see _permanentlyDeniedTextures). The
                            // bake path (fetchUrl != null) is excluded: it 403s by design and then
                            // legitimately falls back to the generic cap (BUG-AVATAR-02).
                            if (fetchUrl == null &&
                                (response.StatusCode == System.Net.HttpStatusCode.Forbidden
                                 || response.StatusCode == System.Net.HttpStatusCode.Unauthorized))
                                _httpDeniedTextures.TryAdd(textureId, 0);
                            // Host in the message: a 403 could be the wrong URL class (see
                            // BUG-AVATAR-02 -- bakes needed bake-texture.glb..., not the generic
                            // asset CDN) or a genuine permission denial / non-persisted local
                            // texture. The host is the first thing that tells those apart.
                            return FetchFailed(textureId, $"HTTP {(int)response.StatusCode} from {url.Host}{url.AbsolutePath}");
                        }
                    }
                    else
                    {
                        declaredLength = response.Content.Headers.ContentLength;
                        bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    response?.Dispose();
                    _textureFetchSemaphore.Release();
                }

                if (bytes != null)
                {
                    if (bytes.Length == 0) return FetchFailed(textureId, "empty body");

                    // The body must actually BE a JPEG2000 codestream. Measured on OSGrid 2026-08-02: of
                    // 14 textures that rendered white, four came back as 1-3 byte bodies -- three of them
                    // the literal bytes 9E E9 65, one a single 00 -- with a success status and a matching
                    // Content-Length. Those were handed on as image data, failed both decoders, and the
                    // surface stayed blank.
                    //
                    // The damage was not the failed decode, it was that HTTP "succeeded": the caller only
                    // falls through to the UDP path when this method returns null, so a garbage body meant
                    // UDP was never tried at all and three retries just re-fetched the same rubbish. A raw
                    // J2C starts with SOC (FF 4F); a JP2-wrapped one starts with the 12-byte JP2 signature
                    // box. Anything else is not a texture, whatever the status line claimed.
                    bool isJ2c = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0x4F;
                    bool isJp2 = bytes.Length >= 12 && bytes[4] == 0x6A && bytes[5] == 0x50
                                 && bytes[6] == 0x20 && bytes[7] == 0x20;
                    if (!isJ2c && !isJp2)
                        return FetchFailed(textureId, $"not a JPEG2000 stream ({bytes.Length} bytes, " +
                            $"head {string.Join("", bytes.Take(Math.Min(4, bytes.Length)).Select(b => b.ToString("X2")))})");

                    // OpenSim's embedded HTTP server has been observed (empirically, right after a
                    // teleport/region-crossing burst of many simultaneous texture GETs) to close the
                    // connection early and return fewer bytes than its own declared Content-Length --
                    // with a 200/206 success status and no exception from HttpClient, since an early
                    // clean connection close is indistinguishable from "body complete" once the socket
                    // just stops sending. Handing that short body to the J2K decoder is exactly the
                    // "Codestream truncated" case this method exists to avoid (see the doc comment
                    // above) even though we never sent a Range header ourselves. Treat a short read as a
                    // failed fetch so the caller falls back to the UDP path in the SAME attempt, instead
                    // of silently decoding (and, for Magick.NET, likely failing on) partial data.
                    if (declaredLength.HasValue && bytes.Length < declaredLength.Value)
                        return FetchFailed(textureId, $"short read {bytes.Length}/{declaredLength.Value}");

                    // The transport-level checks above can only catch a truncation the TRANSPORT knows
                    // about. A body that is short but whose Content-Length agrees with it -- the sim
                    // serving a partial asset and honestly declaring the partial size -- passes both, and
                    // then decodes "degraded": gap-filled pixels. For an ordinary texture that is a
                    // cosmetic problem; for a SCULPT MAP the pixels ARE the vertex positions, so the
                    // renderer (correctly) refuses the result and substitutes a placeholder solid, which
                    // is how a rock ends up on screen as a smooth flat disc.
                    //
                    // Content-Length only catches truncation when the server actually sends that
                    // header -- OpenSim's embedded HTTP server can respond chunked (no Content-Length)
                    // for texture bodies, which would let a short chunked read straight through the
                    // check above. A complete J2C codestream (SOC marker 0xFF4F at the start, verified
                    // by AssetService.DecodeTexture) always ends with an EOC marker (0xFFD9) -- that's
                    // true regardless of transport, so it catches the chunked-encoding gap. Only applies
                    // to a full (desiredDiscard 0) fetch: an intentional Range request never contains
                    // the EOC by design.
                    if (desiredDiscard == 0 && bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0x4F
                        && (bytes[^2] != 0xFF || bytes[^1] != 0xD9))
                    {
                        // A missing EOC used to REJECT the response outright. That threw away perfectly
                        // usable data: JPEG2000 is progressive and SL assets are routinely stored without
                        // a terminating EOC, so the real viewer decodes such streams on purpose. Measured
                        // 2026-08-02 on OSGrid: 14 textures in a single view failed here, every one of
                        // them having already passed the Content-Length check -- i.e. the body was
                        // complete, just not EOC-terminated -- and the objects using them rendered white
                        // while Firestorm drew them fine.
                        //
                        // Kept as a WARNING, not a failure: the check was added to catch chunked-encoding
                        // truncation that Content-Length cannot see, and that concern is real. It is just
                        // not decidable from the EOC alone. Hand the bytes to the tolerant decoder
                        // instead; AssetService already detects a degraded decode and retries, which
                        // distinguishes "genuinely truncated" from "simply not EOC-terminated" by the one
                        // thing that actually settles it -- whether it decodes.
                        // if (_fetchFailureLogged.TryAdd(textureId, 0))
                        //     Console.Error.WriteLine($"[TextureFetch] {textureId}: no EOC marker " +
                        //         $"({bytes.Length} bytes, tail {bytes[^2]:X2}{bytes[^1]:X2}) — decoding anyway");
                    }

                    return bytes;
                }
            }
            catch (Exception ex)
            {
                if (attempt == maxRetries) return FetchFailed(textureId, $"Exception: {ex.Message}");
            }
            if (attempt < maxRetries) await Task.Delay(2000).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Fetches a GLTF PBR Material asset from the simulator. Returns null if the fetch fails.
    /// </summary>
    public async Task<LibreMetaverse.Assets.AssetMaterial?> FetchMaterialDataAsync(Guid materialId)
    {
        var asset = await _client.Assets
            .RequestAssetAsync(new UUID(materialId), AssetType.Material, true, CancellationToken.None)
            .ConfigureAwait(false);
        return asset as LibreMetaverse.Assets.AssetMaterial;
    }

    /// <summary>
    /// Fetches the raw bytes of an animation asset from the simulator.
    /// </summary>
    public async Task<byte[]?> FetchAnimationDataAsync(Guid animId)
    {
        var asset = await _client.Assets
            .RequestAssetAsync(new UUID(animId), AssetType.Animation, true, CancellationToken.None)
            .ConfigureAwait(false);
        return asset?.AssetData;
    }
}
