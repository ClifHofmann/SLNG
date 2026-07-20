using System;

namespace SLNG.Assets;

/// <summary>
/// Decodes the "Weights" binary block of a rigged SL mesh submesh with the viewer's EXACT reader
/// semantics (LLVolume::unpackVolumeFacesInternal, indra/llmath/llvolume.cpp): per vertex, up to
/// four (jointIndex:u8, weight:u16le) influence entries; a joint byte of 0xFF ends the vertex —
/// but after the FOURTH influence the vertex ends WITHOUT a sentinel, because the writer
/// (LLModel serialization, indra/llprimitive/llmodel.cpp) only emits the 0xFF terminator when
/// the influence count is &lt; 4. A reader that keeps scanning for 0xFF after four influences
/// (LibreMetaverse's DecodeVertexWeights does exactly that) mis-consumes the next vertex's bytes
/// and corrupts every subsequent vertex's weights in the submesh — observed in the wild as a
/// mesh head shell "dominated 45–75 % by mPelvis", stretching vertically with the Height slider,
/// while single-influence submeshes (which always carry the sentinel) parsed fine.
/// </summary>
public static class MeshSkinWeightDecoder
{
    /// <summary>Decodes <paramref name="data"/> into one <see cref="VertexBoneWeights"/> per
    /// vertex. Joint indices are raw (not validated against the joint list) and weights are NOT
    /// normalized — both match the viewer, which validates/normalizes later in the skinning
    /// path (our renderer normalizes per vertex too). Unused influence slots have weight 0.</summary>
    public static VertexBoneWeights[] Decode(byte[] data, int vertexCount)
    {
        var result = new VertexBoneWeights[vertexCount];
        int idx = 0;
        int v = 0;

        Span<int> joints = stackalloc int[4];
        Span<float> w = stackalloc float[4];

        while (idx < data.Length && v < vertexCount)
        {
            byte joint = data[idx++];
            int cur = 0;
            joints.Clear();
            w.Clear();

            while (joint != 0xFF && idx + 1 < data.Length + 1 && idx + 2 <= data.Length)
            {
                ushort influence = (ushort)(data[idx] | (data[idx + 1] << 8));
                idx += 2;

                // Same clamp as the viewer (llclamp(influence/65535, 0.001, 0.999)).
                joints[cur] = joint;
                w[cur] = Math.Clamp(influence / 65535f, 0.001f, 0.999f);
                cur++;

                if (cur >= 4)
                    break; // vertex complete — NO sentinel byte follows (the critical rule)
                if (idx >= data.Length)
                    break;
                joint = data[idx++];
            }

            // Viewer fallback: an all-zero weight sum pins the vertex to joint 0.
            if (w[0] + w[1] + w[2] + w[3] <= 0f)
            {
                joints[0] = 0;
                w[0] = 0.999f;
            }

            result[v] = new VertexBoneWeights(
                joints[0], joints[1], joints[2], joints[3],
                w[0], w[1], w[2], w[3]);
            v++;
        }

        // Vertices beyond the data (malformed asset): pin to the submesh's first joint so they
        // at least deform with the mesh instead of sticking at the origin.
        for (; v < vertexCount; v++)
            result[v] = new VertexBoneWeights(0, 0, 0, 0, 1f, 0f, 0f, 0f);

        return result;
    }
}
