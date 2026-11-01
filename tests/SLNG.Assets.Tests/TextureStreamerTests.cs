using CoreJ2K.Configuration;
using SkiaSharp;
using SLNG.Net;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// FEAT-PERF-29: <see cref="TextureStreamer"/> against a fake range source and a temporary cache folder.
///
/// <para>The fixture is encoded here, not committed: CoreJ2K's streaming preset writes a raw LRCP codestream
/// with three quality layers, the same layout SL assets have (LRCP, one tile-part, 5 levels -- measured over
/// 6,000 cached assets, see the FEAT-PERF-29 spec), so a prefix of it decodes at a reduced resolution the
/// way a real one does. Magick's encoder cannot do this (single layer; see TruncatedCodestreamDecodeTests).</para>
/// </summary>
[Collection("TextureLod.GlobalLodBias")]
public sealed class TextureStreamerTests : IDisposable
{
    private static readonly Lazy<byte[]> Asset = new(() => Encode(512, SKAlphaType.Opaque));        // 3 components
    private static readonly Lazy<byte[]> AlphaAsset = new(() => Encode(512, SKAlphaType.Unpremul)); // 4 components
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "slng-streamer-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _id = Guid.NewGuid();

    public TextureStreamerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        TextureStreaming.FirstRequestBytes = TextureStreaming.DefaultFirstRequestBytes;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] Encode(int size, SKAlphaType alpha)
    {
        using var bmp = new SKBitmap(size, size, SKColorType.Bgra8888, alpha);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                bmp.SetPixel(x, y, new SKColor((byte)(x / 2), (byte)(y / 2), (byte)((x ^ y) & 0xFF), 255));
        return CompleteConfigurationPresets.Streaming.ForLossless().WithFileFormat(false).Encode(bmp);
    }

    /// <summary>Serves <see cref="Asset"/> like the asset CDN: 206 with Content-Range, or the configured misbehaviour.</summary>
    private sealed class FakeCdn
    {
        public readonly List<(long Start, long? End)> Requests = new();
        public bool IgnoreRange, Fail, Gone;
        public byte[] Serves = Asset.Value;

        public Task<TextureRangeResult?> Fetch(Guid id, long start, long? end, float priority)
        {
            Requests.Add((start, end));
            if (Fail) return Task.FromResult<TextureRangeResult?>(null);
            if (Gone) return Task.FromResult<TextureRangeResult?>(new TextureRangeResult(Array.Empty<byte>(), 0, false, -1, Gone: true));
            byte[] all = Serves;
            if (IgnoreRange) return Task.FromResult(TextureRange.Interpret(200, null, null, start, end, all));
            long last = Math.Min(end ?? long.MaxValue, all.Length - 1);
            byte[] body = all[(int)start..(int)(last + 1)];
            return Task.FromResult(TextureRange.Interpret(206, start, all.Length, start, end, body));
        }
    }

    private TextureStreamer Streamer(FakeCdn cdn) => new(_dir, cdn.Fetch);

    private static double Mae(TextureData a, TextureData b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        long sum = 0;
        for (int i = 0; i < a.Rgba.Length; i++) sum += Math.Abs(a.Rgba[i] - b.Rgba[i]);
        return (double)sum / a.Rgba.Length;
    }

    // 512^2 texels: area 64 needs discard 5 (16 px), area 512 discard 4 (32 px).
    private const float FarArea = 64f, MidArea = 512f, NearArea = 512f * 512f;

    [Fact]
    public async Task First_request_is_coarse_and_decodes_as_an_intended_partial()
    {
        var cdn = new FakeCdn();

        var result = await Streamer(cdn).FetchAsync(_id, FarArea, 1f);

        Assert.Equal(TextureStreamer.Outcome.Partial, result.Outcome);
        Assert.Equal(new (long, long?)[] { (0, 18839) }, cdn.Requests);
        var data = result.Data!;
        Assert.True(data.IsPartial);
        Assert.False(data.IsDegraded, "an intended partial stream is not a broken one");
        Assert.Equal(16, data.Width);
        Assert.Equal(512, data.SourceWidth);
        Assert.Equal(18840, new FileInfo(Path.Combine(_dir, _id + "_v5.part")).Length);
        Assert.False(File.Exists(Path.Combine(_dir, _id + "_v5.j2c")));
    }

    [Fact]
    public async Task Refinement_fetches_only_the_missing_bytes_and_decodes_sharper()
    {
        var cdn = new FakeCdn();
        var streamer = Streamer(cdn);
        await streamer.FetchAsync(_id, FarArea, 1f);

        var refined = await streamer.FetchAsync(_id, MidArea, 1f);

        Assert.Equal(TextureStreamer.Outcome.Partial, refined.Outcome);
        int discard4 = J2kByteSizeEstimator.CalcDataSizeJ2C(512, 512, 4);
        Assert.Equal((18839L, (long?)(discard4 - 1)), cdn.Requests[1]);
        Assert.Equal(discard4, new FileInfo(Path.Combine(_dir, _id + "_v5.part")).Length);
        Assert.Equal(32, refined.Data!.Width);
        // Against the complete stream decoded at the same level: the appended bytes are the right ones.
        var reference = AssetService.TryDecodeReduced(Asset.Value, 4)!;
        Assert.True(Mae(refined.Data, reference) < 2.0, $"refined decode is {Mae(refined.Data, reference):0.00} off the complete stream's");
    }

    [Fact]
    public async Task Bytes_in_hand_that_cover_the_target_decode_without_a_request()
    {
        File.WriteAllBytes(Path.Combine(_dir, _id + "_v5.part"), Asset.Value[..J2kByteSizeEstimator.CalcDataSizeJ2C(512, 512, 4)]);
        var cdn = new FakeCdn();

        var result = await Streamer(cdn).FetchAsync(_id, FarArea, 1f);

        Assert.Empty(cdn.Requests);
        Assert.Equal(TextureStreamer.Outcome.Partial, result.Outcome);
        Assert.Equal(16, result.Data!.Width);
    }

    [Fact]
    public async Task The_rest_of_the_asset_is_promoted_to_the_whole_asset_file()
    {
        var cdn = new FakeCdn();
        var streamer = Streamer(cdn);
        await streamer.FetchAsync(_id, NearArea, 1f);   // no header yet: the coarse first request

        var rest = await streamer.FetchAsync(_id, NearArea, 1f);

        Assert.Equal(TextureStreamer.Outcome.Complete, rest.Outcome);
        Assert.Equal((18839L, (long?)null), cdn.Requests[1]);
        Assert.Equal(Asset.Value, File.ReadAllBytes(Path.Combine(_dir, _id + "_v5.j2c")));
        Assert.False(File.Exists(Path.Combine(_dir, _id + "_v5.part")));
    }

    [Fact]
    public async Task A_server_that_ignores_Range_completes_on_the_first_request()
    {
        var cdn = new FakeCdn { IgnoreRange = true };

        var result = await Streamer(cdn).FetchAsync(_id, FarArea, 1f);

        Assert.Equal(TextureStreamer.Outcome.Complete, result.Outcome);
        Assert.Equal(Asset.Value, File.ReadAllBytes(Path.Combine(_dir, _id + "_v5.j2c")));
    }

    [Fact]
    public async Task A_failed_request_falls_back_and_drops_the_partial_bytes()
    {
        File.WriteAllBytes(Path.Combine(_dir, _id + "_v5.part"), Asset.Value[..18840]);
        var cdn = new FakeCdn { Fail = true };

        var result = await Streamer(cdn).FetchAsync(_id, NearArea, 1f);

        Assert.Equal(TextureStreamer.Outcome.FellBack, result.Outcome);
        Assert.False(File.Exists(Path.Combine(_dir, _id + "_v5.part")));
    }

    [Fact]
    public async Task A_refused_texture_is_gone()
    {
        var result = await Streamer(new FakeCdn { Gone = true }).FetchAsync(_id, FarArea, 1f);

        Assert.Equal(TextureStreamer.Outcome.Gone, result.Outcome);
    }

    [Fact]
    public async Task The_same_partial_decoded_twice_gives_the_same_pixels()
    {
        // The spike saw one cut decode differently within one process (FEAT-PERF-29 spec, "not always deterministic").
        File.WriteAllBytes(Path.Combine(_dir, _id + "_v5.part"), Asset.Value[..18840]);
        var streamer = Streamer(new FakeCdn());

        var a = await streamer.FetchAsync(_id, FarArea, 1f);
        var b = await streamer.FetchAsync(_id, FarArea, 1f);

        Assert.Equal(a.Data!.Rgba, b.Data!.Rgba);
    }

    [Fact]
    public async Task An_alpha_texture_far_away_is_still_refined_to_64_px()
    {
        // BUG-RENDER-15's floor: 512 px with alpha stops at discard 3 (64 px), not at the area's discard 5.
        var cdn = new FakeCdn { Serves = AlphaAsset.Value };
        var streamer = Streamer(cdn);
        await streamer.FetchAsync(_id, FarArea, 1f);

        await streamer.FetchAsync(_id, FarArea, 1f);

        Assert.Equal((18839L, (long?)(J2kByteSizeEstimator.CalcDataSizeJ2C(512, 512, 3) - 1)), cdn.Requests[1]);
    }

    [Fact]
    public async Task The_first_request_size_is_a_setting()
    {
        TextureStreaming.FirstRequestBytes = 600;
        var cdn = new FakeCdn();

        await Streamer(cdn).FetchAsync(_id, FarArea, 1f);

        Assert.Equal((0L, (long?)599), cdn.Requests[0]);
    }
}
