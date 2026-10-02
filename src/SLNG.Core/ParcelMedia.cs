namespace SLNG.Core;

/// <summary>What the Land-Info "Media" tab shows (FEAT-LAND-02): the parcel's media URL and how it is
/// presented. Sources: <c>LLPanelLandMedia::refresh</c> (<c>llpanellandmedia.cpp</c>:120) and
/// <c>LLParcel::unpackMessage</c> (<c>llparcel.cpp</c>:549). A parcel with no media is
/// <see cref="None"/> (never null), so a UI can bind to the members unconditionally.</summary>
public sealed record ParcelMedia
{
    /// <summary>A parcel with no media configured.</summary>
    public static ParcelMedia None { get; } = new();

    /// <summary>"Home Page": the media URL (<c>MediaURL</c>). Empty when none.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>"Type": the media MIME type (<c>MediaType</c>, from the message's <c>MediaData</c> block).
    /// Null when the sim sent none, or the viewer's "none" placeholder <c>none/none</c>
    /// (<c>llmimetypes.cpp</c>:47) -- the tab shows both as "None".</summary>
    public string? MimeType { get; init; }

    /// <summary>"Description": text shown next to the play button (<c>MediaDesc</c>).</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>"Replace Texture": objects wearing this texture show the media (<c>MediaID</c>).
    /// Null when nil.</summary>
    public Guid? TextureId { get; init; }

    /// <summary>"Size" width in pixels; 0 means "default" (<c>MediaWidth</c>). The viewer only shows it
    /// for MIME types that allow resizing (web content) and shows 0 otherwise.</summary>
    public int Width { get; init; }

    /// <summary>"Size" height in pixels; 0 means "default" (<c>MediaHeight</c>).</summary>
    public int Height { get; init; }

    /// <summary>"Auto scale" (<c>MediaAutoScale</c>).</summary>
    public bool AutoScale { get; init; }

    /// <summary>"Loop" (<c>MediaLoop</c>). The viewer only shows it for MIME types that allow looping
    /// (movie, audio) and shows it unticked otherwise (<c>llpanellandmedia.cpp</c>:155).</summary>
    public bool Loop { get; init; }

    /// <summary>True when a media URL is set.</summary>
    public bool HasMedia => Url.Length > 0;
}
