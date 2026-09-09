using System;
using System.Collections.Generic;
using System.IO;

namespace SLNG.Net;

/// <summary>
/// A tiny on-disk copy of the local agent's last <b>healthy</b> self <c>AvatarAppearance</c> —
/// the wire-order visual-parameter array, the per-slot bake texture ids, and the hover offset.
///
/// <para>Why it exists: the simulator does not reliably send the local agent its own
/// <c>AvatarAppearance</c> after login (roughly every second login on Agni). The bake ids can be
/// recovered from our own <c>ObjectUpdate</c> TextureEntry, but the ~253 visual parameters have
/// <b>no other source</b> — miss that packet and the avatar renders with the default shape until
/// the next login that happens to receive one. This cache lets the renderer fall back to the
/// last shape it actually saw. It is read-only with respect to the grid: nothing here is ever
/// sent. A real <c>AvatarAppearance</c> arriving later overrides it, so a stale cache self-heals.</para>
/// </summary>
internal static class SelfAppearanceCache
{
    private const uint Magic = 0x50414C53; // "SLAP" little-endian
    private const byte Version = 1;

    private static string DirPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "SLNG", "self-appearance");

    private static string FilePath(Guid agentId) => Path.Combine(DirPath, agentId.ToString("N") + ".bin");

    /// <summary>BUG-AVATAR-04: when this cache was last written, or null if there is no entry.
    ///
    /// <para>The login summary reports it because the cache's AGE is what separates the two
    /// candidate causes. <see cref="Save"/> only runs when an <c>AvatarAppearance</c> actually
    /// arrives, so after an outfit change that received no further relay the newest entry predates
    /// the change — and the restore path then faithfully restores the OLD outfit, which reads as
    /// "broken" exactly like a default shape does. A timestamp from before the last outfit change
    /// says that happened; a fresh one rules it out.</para></summary>
    internal static DateTime? LastWrittenUtc(Guid agentId)
    {
        try
        {
            var path = FilePath(agentId);
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch
        {
            // Never worth failing a login over a diagnostic timestamp.
            return null;
        }
    }

    /// <summary>Writes the cache for <paramref name="agentId"/>. Never throws.</summary>
    internal static void Save(Guid agentId, byte[] visualParams, IReadOnlyDictionary<int, Guid> bakes, float hoverZ)
    {
        if (agentId == Guid.Empty || visualParams is not { Length: > 0 }) return;
        try
        {
            Directory.CreateDirectory(DirPath);
            using var fs = new FileStream(FilePath(agentId), FileMode.Create, FileAccess.Write, FileShare.None);
            using var w = new BinaryWriter(fs);
            w.Write(Magic);
            w.Write(Version);
            w.Write(agentId.ToByteArray());
            w.Write(hoverZ);
            w.Write(visualParams.Length);
            w.Write(visualParams);
            w.Write(bakes?.Count ?? 0);
            if (bakes != null)
                foreach (var kv in bakes)
                {
                    w.Write(kv.Key);
                    w.Write(kv.Value.ToByteArray());
                }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Appearance] self-appearance cache write failed: {ex.Message}");
        }
    }

    /// <summary>Loads the cache for <paramref name="agentId"/>. Returns false (and empty outputs)
    /// when there is no usable file. Never throws.</summary>
    internal static bool TryLoad(Guid agentId, out byte[] visualParams, out Dictionary<int, Guid> bakes, out float hoverZ)
    {
        visualParams = Array.Empty<byte>();
        bakes = new Dictionary<int, Guid>();
        hoverZ = 0f;
        if (agentId == Guid.Empty) return false;

        try
        {
            var path = FilePath(agentId);
            if (!File.Exists(path)) return false;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var r = new BinaryReader(fs);
            if (r.ReadUInt32() != Magic || r.ReadByte() != Version) return false;
            if (new Guid(r.ReadBytes(16)) != agentId) return false;

            hoverZ = r.ReadSingle();

            int vpLen = r.ReadInt32();
            if (vpLen is <= 0 or > 4096) return false;
            visualParams = r.ReadBytes(vpLen);
            if (visualParams.Length != vpLen) { visualParams = Array.Empty<byte>(); return false; }

            int bakeCount = r.ReadInt32();
            if (bakeCount is < 0 or > 64) return true; // params are the valuable part; skip odd bake blocks
            for (int i = 0; i < bakeCount; i++)
            {
                int slot = r.ReadInt32();
                var g = new Guid(r.ReadBytes(16));
                if (g != Guid.Empty) bakes[slot] = g;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Appearance] self-appearance cache read failed: {ex.Message}");
            visualParams = Array.Empty<byte>();
            bakes = new Dictionary<int, Guid>();
            return false;
        }
    }
}
