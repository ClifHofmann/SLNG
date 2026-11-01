namespace SLNG.Assets;

/// <summary>
/// Engine- and protocol-neutral decoded texture: tightly packed RGBA8 pixels, row-major.
/// This is what crosses the <c>SLNG.Assets</c> boundary — never a LibreMetaverse type.
/// </summary>
/// <param name="SourceWidth">Width of the codestream this was decoded FROM, which is larger than
/// <paramref name="Width"/> when the decoder was asked for a reduced resolution
/// (<see cref="TextureLod"/>). 0 means "not known / same as Width". Callers need it to tell a
/// deliberately low-resolution decode from a full one — a texture uploaded at a reduced level must
/// stay eligible for a sharper re-decode when the camera moves closer, and one already at full
/// resolution never is.</param>
/// <param name="IsPartial">FEAT-PERF-29: decoded from an intended partial codestream (texture streaming).
/// More bytes exist, so a sharper level means a further fetch, and the image may be coarser than the
/// screen asked for. Never degraded: the reduced decode of a prefix is clean at its level.</param>
public sealed record TextureData(
    int Width, int Height, byte[] Rgba, bool IsDegraded = false,
    int SourceWidth = 0, int SourceHeight = 0, bool IsPartial = false);
