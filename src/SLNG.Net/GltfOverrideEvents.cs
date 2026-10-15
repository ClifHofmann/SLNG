using System;
using System.Text;
using SLNG.Core;

namespace SLNG.Net;

internal static class GltfOverrideEvents
{
    public static string Preview(byte[] data)
    {
        if (data == null || data.Length == 0) return string.Empty;
        int len = Math.Min(data.Length, 240);
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            byte b = data[i];
            if (b >= 0x20 && b <= 0x7E)
                sb.Append((char)b);
            else
                sb.Append('.');
        }
        return sb.ToString();
    }

    public static bool TryProcess(
        ulong regionHandle,
        byte[] data,
        out GltfMaterialOverrideEvent? evt,
        out string? error)
    {
        evt = null;
        if (!GltfOverrideParser.TryParse(data, out var message, out error))
        {
            return false;
        }

        evt = new GltfMaterialOverrideEvent(regionHandle, message!.LocalId, message.Faces);
        return true;
    }
}
