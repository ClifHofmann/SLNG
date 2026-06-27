using Godot;
using SLNG.Core;

namespace SLNG.App;

/// <summary>
/// Generates a simple static "box-man" placeholder mesh from the SL skeleton's rest pose.
/// Each body part is a box (or a sphere for the head) placed at its bone's global rest
/// position. Static and un-skinned — a stand-in until real avatar appearance (M4-3).
/// </summary>
public static class ProceduralAvatarMesh
{
    private struct BodyPart
    {
        public string BoneName;
        public Vector3 Offset;
        public Vector3 Size;
        public bool IsSphere;
    }

    private static readonly BodyPart[] _bodyParts =
    {
        new() { BoneName = "mHead", Offset = new Vector3(0, 0.1f, 0), Size = new Vector3(0.22f, 0.25f, 0.24f), IsSphere = true },
        new() { BoneName = "mNeck", Offset = Vector3.Zero, Size = new Vector3(0.08f, 0.08f, 0.08f) },
        new() { BoneName = "mChest", Offset = new Vector3(0, 0.12f, 0), Size = new Vector3(0.38f, 0.28f, 0.22f) },
        new() { BoneName = "mTorso", Offset = new Vector3(0, 0.10f, 0), Size = new Vector3(0.34f, 0.22f, 0.20f) },
        new() { BoneName = "mPelvis", Offset = Vector3.Zero, Size = new Vector3(0.32f, 0.16f, 0.20f) },

        new() { BoneName = "mShoulderLeft", Offset = Vector3.Zero, Size = new Vector3(0.10f, 0.28f, 0.10f) },
        new() { BoneName = "mElbowLeft", Offset = Vector3.Zero, Size = new Vector3(0.08f, 0.25f, 0.08f) },
        new() { BoneName = "mWristLeft", Offset = Vector3.Zero, Size = new Vector3(0.06f, 0.10f, 0.04f) },

        new() { BoneName = "mShoulderRight", Offset = Vector3.Zero, Size = new Vector3(0.10f, 0.28f, 0.10f) },
        new() { BoneName = "mElbowRight", Offset = Vector3.Zero, Size = new Vector3(0.08f, 0.25f, 0.08f) },
        new() { BoneName = "mWristRight", Offset = Vector3.Zero, Size = new Vector3(0.06f, 0.10f, 0.04f) },

        new() { BoneName = "mHipLeft", Offset = Vector3.Zero, Size = new Vector3(0.12f, 0.40f, 0.12f) },
        new() { BoneName = "mKneeLeft", Offset = Vector3.Zero, Size = new Vector3(0.10f, 0.40f, 0.10f) },
        new() { BoneName = "mAnkleLeft", Offset = Vector3.Zero, Size = new Vector3(0.08f, 0.06f, 0.16f) },

        new() { BoneName = "mHipRight", Offset = Vector3.Zero, Size = new Vector3(0.12f, 0.40f, 0.12f) },
        new() { BoneName = "mKneeRight", Offset = Vector3.Zero, Size = new Vector3(0.10f, 0.40f, 0.10f) },
        new() { BoneName = "mAnkleRight", Offset = Vector3.Zero, Size = new Vector3(0.08f, 0.06f, 0.16f) },
    };

    /// <summary>
    /// Builds a static box-man MeshInstance3D from the skeleton's rest pose. The figure's
    /// feet sit near the local origin, so parenting it to a node at ground height stands it
    /// on the ground.
    /// </summary>
    public static MeshInstance3D Create(AvatarSkeleton skeleton, Color color)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        foreach (var part in _bodyParts)
        {
            var slPos = skeleton.GetGlobalRestPosition(part.BoneName);
            // SL Z-up -> Godot Y-up
            var center = new Vector3(slPos.X, slPos.Z, -slPos.Y) + part.Offset;

            if (part.IsSphere)
            {
                AddSphere(st, center, part.Size.X, 8);
            }
            else
            {
                AddBox(st, center, part.Size);
            }
        }

        st.GenerateNormals();

        return new MeshInstance3D
        {
            Mesh = st.Commit(),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = color,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
    }

    private static void AddBox(SurfaceTool st, Vector3 center, Vector3 size)
    {
        var h = size * 0.5f;
        Vector3[] c =
        {
            center + new Vector3(-h.X, -h.Y, -h.Z),
            center + new Vector3( h.X, -h.Y, -h.Z),
            center + new Vector3( h.X,  h.Y, -h.Z),
            center + new Vector3(-h.X,  h.Y, -h.Z),
            center + new Vector3(-h.X, -h.Y,  h.Z),
            center + new Vector3( h.X, -h.Y,  h.Z),
            center + new Vector3( h.X,  h.Y,  h.Z),
            center + new Vector3(-h.X,  h.Y,  h.Z),
        };
        int[][] faces =
        {
            new[] { 0, 1, 2, 3 }, // front
            new[] { 5, 4, 7, 6 }, // back
            new[] { 4, 0, 3, 7 }, // left
            new[] { 1, 5, 6, 2 }, // right
            new[] { 3, 2, 6, 7 }, // top
            new[] { 4, 5, 1, 0 }, // bottom
        };
        foreach (var f in faces)
        {
            Quad(st, c[f[0]], c[f[1]], c[f[2]], c[f[3]]);
        }
    }

    private static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
        st.AddVertex(a); st.AddVertex(c); st.AddVertex(d);
    }

    private static void AddSphere(SurfaceTool st, Vector3 center, float radius, int segments)
    {
        for (int lat = 0; lat < segments; lat++)
        {
            float t1 = Mathf.Pi * lat / segments;
            float t2 = Mathf.Pi * (lat + 1) / segments;
            for (int lon = 0; lon < segments * 2; lon++)
            {
                float p1 = Mathf.Tau * lon / (segments * 2);
                float p2 = Mathf.Tau * (lon + 1) / (segments * 2);
                Vector3 v1 = center + Spherical(radius, t1, p1);
                Vector3 v2 = center + Spherical(radius, t1, p2);
                Vector3 v3 = center + Spherical(radius, t2, p2);
                Vector3 v4 = center + Spherical(radius, t2, p1);
                st.AddVertex(v1); st.AddVertex(v3); st.AddVertex(v2);
                st.AddVertex(v1); st.AddVertex(v4); st.AddVertex(v3);
            }
        }
    }

    private static Vector3 Spherical(float r, float theta, float phi)
    {
        float s = Mathf.Sin(theta);
        return new Vector3(r * s * Mathf.Cos(phi), r * Mathf.Cos(theta), r * s * Mathf.Sin(phi));
    }
}
