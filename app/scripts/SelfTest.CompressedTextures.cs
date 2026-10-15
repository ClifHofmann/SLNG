using System;
using System.Collections.Generic;
using Godot;
using SLNG.Assets;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>FEAT-PERF-17: a test texture -- colour ramps, and with <paramref name="alpha"/> a cutout disc
    /// with an anti-aliased rim (foliage, lace).</summary>
    private static TextureData CompressTestTexture(int s, bool alpha)
    {
        var rgba = new byte[s * s * 4];
        for (int y = 0; y < s; y++)
        {
            for (int x = 0; x < s; x++)
            {
                int i = (y * s + x) * 4;
                rgba[i] = (byte)(x * 255 / (s - 1));
                rgba[i + 1] = (byte)(y * 255 / (s - 1));
                rgba[i + 2] = (byte)(128 + 60 * Math.Sin(x * 0.07) * Math.Cos(y * 0.05));
                double d = Math.Sqrt((x - s / 2.0) * (x - s / 2.0) + (y - s / 2.0) * (y - s / 2.0)) - s * 0.35;
                rgba[i + 3] = alpha ? (byte)Math.Clamp((int)(128 - d * 64), 0, 255) : (byte)255;
            }
        }
        return new TextureData(s, s, rgba, false, s, s);
    }

    /// <summary>PSNR of <paramref name="got"/> against <paramref name="want"/> (both RGBA8): colour over
    /// texels whose wanted alpha is visible (&gt; 16), alpha over all.</summary>
    private static (double Rgb, double Alpha) CompressPsnr(byte[] want, byte[] got)
    {
        double seRgb = 0, seA = 0;
        long nRgb = 0, nA = 0;
        for (int i = 0; i + 3 < want.Length && i + 3 < got.Length; i += 4)
        {
            int da = want[i + 3] - got[i + 3];
            seA += da * da;
            nA++;
            if (want[i + 3] <= 16) continue;
            for (int c = 0; c < 3; c++)
            {
                int d = want[i + c] - got[i + c];
                seRgb += d * d;
                nRgb++;
            }
        }
        static double P(double se, long n) => se == 0 || n == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (se / n));
        return (P(seRgb, nRgb), P(seA, nA));
    }

    /// <summary>
    /// FEAT-PERF-17 Part B: an opaque texture is uploaded as BC1 and one with alpha as BC3, mips included;
    /// Godot's own decoder (bcdec) reads our blocks back close to the source; the entry is booked at exactly
    /// the bytes Godot holds; the alpha numbers are those of the UNCOMPRESSED pixels; with the setting off,
    /// or for a normal map, nothing is compressed -- and a "no" from any caller wins.
    /// </summary>
    private static Check CheckCompressedTextureUpload()
    {
        const string Name = "block-compressed upload round trip and accounting";
        var problems = new List<string>();
        var cache = new GpuCache(512L << 20) { CompressTextures = true };
        var notes = new List<string>();
        try
        {
            const int S = 256;
            foreach (bool alpha in new[] { false, true })
            {
                var data = CompressTestTexture(S, alpha);
                var id = Guid.NewGuid();
                var (tex, _, _) = cache.SelfTestUpload(id, data, 0f, generateMipmaps: true, initialRefCount: 1, compress: true);
                var want = alpha ? GpuTextureFormat.Bc3 : GpuTextureFormat.Bc1;
                if (tex == null) { problems.Add($"{want}: nothing uploaded"); continue; }

                var (format, size) = cache.SelfTestEntry(id);
                using var img = tex.GetImage();
                var godotFormat = img.GetFormat();
                if (format != want) problems.Add($"{want}: entry format {format}");
                if (godotFormat != (alpha ? Image.Format.Dxt5 : Image.Format.Dxt1)) problems.Add($"{want}: Godot holds {godotFormat}");
                if (!img.HasMipmaps()) problems.Add($"{want}: no mipmaps");
                long godotBytes = img.GetData().Length;
                long expected = BcLayout.ChainBytes(want, S, S, true);
                if (size != godotBytes || size != expected)
                    problems.Add($"{want}: booked {size} B, Godot holds {godotBytes} B, layout says {expected} B");

                // Godot decodes our blocks; compare with the source as the upload saw it (FixAlphaEdges).
                using var reference = Image.CreateFromData(S, S, false, Image.Format.Rgba8, data.Rgba);
                reference.FixAlphaEdges();
                var top = (Image)img.Duplicate();
                top.ClearMipmaps();
                if (top.Decompress() != Error.Ok) problems.Add($"{want}: Godot could not decompress it");
                else
                {
                    if (top.GetFormat() != Image.Format.Rgba8) top.Convert(Image.Format.Rgba8);
                    var (rgb, a) = CompressPsnr(reference.GetData(), top.GetData());
                    notes.Add($"{want} PSNR rgb {rgb:0.0} alpha {(alpha ? a.ToString("0.0") : "-")} dB, {size >> 10} KB vs RGBA8 {TextureAdmission.TextureBytes(S, S, true) >> 10} KB");
                    if (rgb < 35) problems.Add($"{want}: colour PSNR {rgb:0.0} dB");
                    if (alpha && a < 40) problems.Add($"{want}: alpha PSNR {a:0.0} dB");
                }
                top.Dispose();

                // The alpha numbers come from the uncompressed pixels, exactly.
                var measured = GpuCache.MeasureAlphaStats(data.Rgba, S, S);
                if (!GpuCache.TryGetAlphaStats(id, out var stats) || stats != measured)
                    problems.Add($"{want}: alpha stats {stats} != uncompressed {measured}");
                if (!GpuCache.TryGetAlphaMode(id, out var mode) || (mode == Image.AlphaMode.None) == alpha)
                    problems.Add($"{want}: alpha mode {mode}");
            }

            // Setting off: RGBA8, booked as before.
            cache.CompressTextures = false;
            var offId = Guid.NewGuid();
            var off = cache.SelfTestUpload(offId, CompressTestTexture(S, false), 0f, true, 1, compress: true);
            var (offFormat, offSize) = cache.SelfTestEntry(offId);
            if (off.Texture == null || offFormat != GpuTextureFormat.Rgba8 || offSize != TextureAdmission.TextureBytes(S, S, true))
                problems.Add($"setting off: {offFormat}, {offSize} B");
            cache.CompressTextures = true;

            // A side that is not a multiple of 4 stays RGBA8.
            var oddId = Guid.NewGuid();
            var oddData = new TextureData(6, 10, new byte[6 * 10 * 4], false, 6, 10);
            cache.SelfTestUpload(oddId, oddData, 0f, true, 1, compress: true);
            if (cache.SelfTestEntry(oddId).Format != GpuTextureFormat.Rgba8) problems.Add("a 6x10 texture was compressed");

            // Intent: a normal map's "no" wins over a diffuse "yes", whatever the order; the re-offer (null)
            // changes nothing; a bake channel never records a "yes".
            var shared = Guid.NewGuid();
            cache.GetOrUploadTextureAsync(shared, null, true, compress: true);
            cache.GetOrUploadTextureAsync(shared, null, true, compress: false);
            cache.GetOrUploadTextureAsync(shared, null, true, compress: true);
            cache.GetOrUploadTextureAsync(shared, null, true);
            if (cache.SelfTestCompressIntent(shared) != false) problems.Add($"normal-map intent: {cache.SelfTestCompressIntent(shared)}");
            var bake = Guid.NewGuid();
            cache.GetOrUploadTextureAsync(bake, null, true, bakeChannel: 0, compress: true);
            if (cache.SelfTestCompressIntent(bake) != false) problems.Add($"bake intent: {cache.SelfTestCompressIntent(bake)}");

            return problems.Count == 0
                ? new Check(Name, true, string.Join("; ", notes))
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            cache.DisposeAll();
        }
    }

    /// <summary>
    /// FEAT-PERF-17 Part B: admission and the budget count the compressed bytes. The burst of
    /// <see cref="CheckGpuCacheAdmission"/> (a budget of five 512x512 RGBA8 textures, forty arriving)
    /// fits whole when the textures are BC1 -- not one is cut.
    /// </summary>
    private static Check CheckCompressedAdmission()
    {
        const string Name = "admission and budget count compressed bytes";
        var problems = new List<string>();
        const long Budget = 7_864_320;
        var cache = new GpuCache(Budget) { CompressTextures = true };
        try
        {
            const int S = 512;
            var data = CompressTestTexture(S, alpha: false);
            long each = BcLayout.ChainBytes(GpuTextureFormat.Bc1, S, S, true);
            int cut = 0;
            for (int i = 0; i < 40; i++)
            {
                var (tex, _, extra) = cache.SelfTestUpload(Guid.NewGuid(), data, S * S, true, 1, compress: true);
                if (tex == null) { problems.Add($"texture {i} not uploaded"); break; }
                if (extra > 0 || tex.GetWidth() != S) cut++;
            }
            if (cut > 0) problems.Add($"{cut} of 40 BC1 textures were cut although {Budget / each} fit");
            if (cache.CurrentSizeBytes != 40 * each) problems.Add($"resident {cache.CurrentSizeBytes} B, expected {40 * each} B");
            if (cache.SelfTestReservedBytes != 0) problems.Add($"{cache.SelfTestReservedBytes} B still reserved");
            return problems.Count == 0
                ? new Check(Name, true, $"40 x {S}x{S} BC1 = {cache.CurrentSizeBytes >> 10} KB in a {Budget >> 10} KB budget (RGBA8 held 5)")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            cache.DisposeAll();
        }
    }

    /// <summary>
    /// FEAT-PERF-17 Part B: the warm path and the slice-shrink. A compressed copy stored by the disk cache
    /// and read back is uploaded with no encode, at the level the screen asks for (a slice of its chain),
    /// with the stored alpha numbers; a copy smaller than that level is refused (the caller re-decodes);
    /// and a resident compressed texture shrinks by one level from the copy, staying compressed and booked
    /// at its new size.
    /// </summary>
    private static Check CheckCompressedWarmPathAndShrink()
    {
        const string Name = "compressed copy: disk round trip, warm upload, slice-shrink";
        var problems = new List<string>();
        var cache = new GpuCache(512L << 20) { CompressTextures = true };
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slng_selftest_bc_" + Guid.NewGuid().ToString("N"));
        try
        {
            const int S = 256;
            var data = CompressTestTexture(S, alpha: true);
            var coldId = Guid.NewGuid();
            var (coldTex, _, _) = cache.SelfTestUpload(coldId, data, 0f, true, 1, compress: true);
            if (coldTex == null) return new Check(Name, false, "cold upload failed");
            using var coldImg = coldTex.GetImage();
            GpuCache.TryGetAlphaStats(coldId, out var coldStats);
            GpuCache.TryGetAlphaMode(coldId, out var coldMode);
            GpuCache.TryGetIsAlphaMaskable(coldId, out bool coldMaskable);
            var copy = new CompressedTextureData(GpuTextureFormat.Bc3, S, S, true, coldImg.GetData(), 1024, 1024,
                new TextureAlphaInfo((byte)coldMode, coldMaskable, coldStats.MinAlpha, coldStats.FracMid, coldStats.FracClear));

            // Through the disk cache and back.
            var disk = new DecodedTextureCache(dir);
            disk.PutGpuAsync(coldId, copy).GetAwaiter().GetResult();
            var back = disk.TryGetGpuAsync(coldId).GetAwaiter().GetResult();
            if (back == null || !back.Data.AsSpan().SequenceEqual(copy.Data) || back.Alpha != copy.Alpha)
                return new Check(Name, false, "the disk cache did not give the copy back unchanged");

            // Warm, full size (the asset is 1024; no area means "the whole asset", which this copy is not).
            long encodes = cache.SelfTestEncodeCount;
            var tooSmall = cache.SelfTestUploadFromCompressed(Guid.NewGuid(), back, 0f);
            if (tooSmall.Served) problems.Add("a 256 copy of a 1024 asset served a full-size request");

            // Warm, at the level a small object asks for: 1024 asset, 128x128 px on screen -> 128 (discard 3).
            var warmId = Guid.NewGuid();
            float area = 128f * 128f;
            int wantW = TextureLod.DimensionAfterDiscard(1024, TextureLod.DiscardLevelFor(1024, 1024, area));
            var warm = cache.SelfTestUploadFromCompressed(warmId, back, area);
            if (!warm.Served || warm.Texture == null) problems.Add("the copy did not serve a 128 px request");
            else
            {
                if (warm.Texture.GetWidth() != wantW) problems.Add($"warm upload {warm.Texture.GetWidth()} px, expected {wantW}");
                var (f, size) = cache.SelfTestEntry(warmId);
                if (f != GpuTextureFormat.Bc3 || size != BcLayout.ChainBytes(GpuTextureFormat.Bc3, wantW, wantW, true))
                    problems.Add($"warm entry {f} {size} B");
                if (warm.UploadedFor != area) problems.Add($"warm upload built for {warm.UploadedFor}, expected {area} (sharpenable)");
                if (!GpuCache.TryGetAlphaStats(warmId, out var ws) || ws != coldStats) problems.Add($"warm alpha stats {ws} != {coldStats}");
            }
            if (cache.SelfTestEncodeCount != encodes) problems.Add($"the warm path encoded {cache.SelfTestEncodeCount - encodes} times");

            // Slice-shrink of the resident cold texture: 256 -> 128, still BC3, booked at 128.
            var (nw, nh, nf) = cache.SelfTestShrinkFromCopy(coldId, back);
            if (nw != S / 2 || nh != S / 2 || nf != GpuTextureFormat.Bc3) problems.Add($"slice-shrink gave {nw}x{nh} {nf}");
            var (_, shrunkSize) = cache.SelfTestEntry(coldId);
            if (shrunkSize != BcLayout.ChainBytes(GpuTextureFormat.Bc3, S / 2, S / 2, true)) problems.Add($"shrunk entry booked {shrunkSize} B");
            if (cache.SelfTestEncodeCount != encodes) problems.Add("the slice-shrink encoded");

            return problems.Count == 0
                ? new Check(Name, true, $"warm {wantW}x{wantW} BC3 from a {S} copy with no encode; shrink {S} -> {nw} by slicing")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            cache.DisposeAll();
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// FEAT-PERF-17 Part B (code review): a resident texture and its replacement can be held in different
    /// formats. A RGBA8 256 (uploaded while the setting was off) must sharpen to a BC1 512 although the BC1
    /// chain has fewer bytes; and with the setting switched off, a BC texture that shrinks stays BC -- its
    /// RGBA8 half size would be as large or larger, and a shrink is there to give bytes back.
    /// </summary>
    private static Check CheckCompressedAcrossFormats()
    {
        const string Name = "sharpen and shrink across formats";
        var problems = new List<string>();
        var cache = new GpuCache(512L << 20) { CompressTextures = false };
        try
        {
            const int S = 512;
            var data = CompressTestTexture(S, alpha: false);

            var id = Guid.NewGuid();
            var (tex, _, _) = cache.SelfTestUpload(id, data, (S / 2) * (S / 2), true, 1, compress: false);
            if (tex == null || tex.GetWidth() != S / 2 || cache.SelfTestEntry(id).Format != GpuTextureFormat.Rgba8)
                return new Check(Name, false, $"setup: resident {tex?.GetWidth()} {cache.SelfTestEntry(id).Format}");
            cache.GetOrUploadTextureAsync(id, null, true, compress: true);   // a diffuse caller
            cache.CompressTextures = true;
            var sharpen = cache.SelfTestPrepareSharpen(id, data, S * S);
            if (!sharpen.HasImage || sharpen.Width != S || sharpen.Format != GpuTextureFormat.Bc1)
                problems.Add($"RGBA8 {S / 2} -> sharpen gave image={sharpen.HasImage} nothingSharper={sharpen.NothingSharper} {sharpen.Width} {sharpen.Format}");
            if (cache.SelfTestReservedBytes != 0) problems.Add($"{cache.SelfTestReservedBytes} B still reserved after the sharpen");

            var bcId = Guid.NewGuid();
            var small = CompressTestTexture(256, alpha: false);
            cache.SelfTestUpload(bcId, small, 0f, true, 1, compress: true);
            cache.CompressTextures = false;
            var (nw, _) = cache.SelfTestShrinkNow(bcId, small);
            var (format, size) = cache.SelfTestEntry(bcId);
            if (nw != 128 || format != GpuTextureFormat.Bc1 || size != BcLayout.ChainBytes(GpuTextureFormat.Bc1, 128, 128, true))
                problems.Add($"BC1 256 shrunk with the setting off: {nw} px, {format}, {size} B");

            return problems.Count == 0
                ? new Check(Name, true, $"RGBA8 {S / 2} sharpened to BC1 {S}; BC1 256 shrank to BC1 128 with the setting off")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            cache.DisposeAll();
        }
    }
}
