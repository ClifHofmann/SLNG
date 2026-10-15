using System;
using System.Threading;
using SLNG.Core;

namespace SLNG.Net;

public sealed partial class GridSession
{
    public event EventHandler<GltfMaterialOverrideEvent>? GltfMaterialOverrideReceived;
    internal void RaiseGltfMaterialOverride(GltfMaterialOverrideEvent e) => GltfMaterialOverrideReceived?.Invoke(this, e);

    private int _gltfOverrideMessages;      // parsed
    private int _gltfOverrideRejected;      // failed to parse

    internal int GltfOverrideMessages => _gltfOverrideMessages;
    internal int GltfOverrideRejected => _gltfOverrideRejected;

    /// <summary>Network thread. Parse only, raise the neutral event; never touch the world.</summary>
    private void OnGenericStreamingMessage(object? sender, LibreMetaverse.GenericStreamingMessageEventArgs e)
    {
        if (e.Method != LibreMetaverse.GenericStreamingMethod.GltfMaterialOverride) return;

        if (!GltfOverrideParser.TryParse(e.Data, out var message, out var error))
        {
            int n = Interlocked.Increment(ref _gltfOverrideRejected);
            // The raw payload has never been seen (BUG: LibreMetaverse rejected it): print the first three.
            if (n <= 3) Console.Error.WriteLine($"[GltfOverride] rejected a payload ({error}): {Preview(e.Data)}");
            return;
        }

        Interlocked.Increment(ref _gltfOverrideMessages);
        if (Diag.Verbose)
            Console.Error.WriteLine($"[GltfOverride] region={e.Simulator.Handle} local={message!.LocalId} faces={message.Faces.Count}" +
                                    (message.Faces.Count == 0 ? " (clear)" : ""));
        RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(e.Simulator.Handle, message!.LocalId, message.Faces));
    }

    // Printable ASCII only, at most 240 characters, so a binary payload cannot garble the log.
    private static string Preview(byte[] data) => GltfOverrideEvents.Preview(data);
}
