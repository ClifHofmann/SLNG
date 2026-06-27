using Godot;
using System.Collections.Generic;

namespace SLNG.App;

/// <summary>
/// Generates a simple procedural humanoid mesh (box-man) skinned to a Skeleton3D.
/// Each body part is a box or sphere attached to the appropriate bone.
/// </summary>
public static class ProceduralAvatarMesh
{
    private struct BodyPart
    {
        public string BoneName;
        public Vector3 Offset;    // Local offset from bone
        public Vector3 Size;      // Box dimensions (x, y, z)
        public bool IsSphere;     // If true, render as sphere instead of box
    }

    private static readonly BodyPart[] _bodyParts = new BodyPart[]
    {
        // Head
        new() { BoneName = "mHead", Offset = new Vector3(0, 0.1f, 0), Size = new Vector3(0.22f, 0.25f, 0.24f), IsSphere = true },
        // Neck
        new() { BoneName = "mNeck", Offset = Vector3.Zero, Size = new Vector3(0.08f, 0.08f, 0.08f) },
        // Chest
        new() { BoneName = "mChest", Offset = new Vector3(0, 0.12f, 0), Size = new Vector3(0.38f, 0.28f, 0.22f) },
        // Torso (abdomen)
        new() { BoneName = "mTorso", Offset = new Vector3(0, 0.10f, 0), Size = new Vector3(0.34f, 0.22f, 0.20f) },
        // Pelvis
        new() { BoneName = "mPelvis", Offset = Vector3.Zero, Size = new Vector3(0.32f, 0.16f, 0.20f) },

        // Left arm
        new() { BoneName = "mShoulderLeft", Offset = new Vector3(0, 0, 0), Size = new Vector3(0.10f, 0.28f, 0.10f) },
        new() { BoneName = "mElbowLeft", Offset = new Vector3(0, 0, 0), Size = new Vector3(0.08f, 0.25f, 0.08f) },
        new() { BoneName = "mWristLeft", Offset = new Vector3(0, 0, 0), Size = new Vector3(0.06f, 0.10f, 0.04f) },

        // Right arm
        new() { BoneName = "mShoulderRight", Offset = new Vector3(0, 0, 0), Size = new Vector3(0.10f, 0.28f, 0.10f) },
        new() { BoneName = "mElbowRight", Offset = new Vector3(0, 0, 0), Size = new Vector3(0.08f, 0.25f, 0.08f) },
        new() { BoneName = "mWristRight", Offset = new Vector3(0, 0, 0), Size = new Vector3(0.06f, 0.10f, 0.04f) },

        // Left leg
        new() { BoneName = "mHipLeft", Offset = new Vector3(0, 0, 0), Size = new Vector3(0.12f, 0.40f, 0.12f) },
        new() { BoneName = "mKneeLeft", Offset = new Vector3(0, 0, 0), Size = new Vector3(0.10f, 0.40f, 0.10f) },
        new() { BoneName = "mAnkleLeft", Offset = new Vector3(0, 0, 0), Size = new Vector3(0.08f, 0.06f, 0.16f) },

        // Right leg
        new() { BoneName = "mHipRight", Offset = new Vector3(0, 0, 0), Size = new Vector3(0.12f, 0.40f, 0.12f) },
        new() { BoneName = "mKneeRight", Offset = new Vector3(0, 0, 0), Size = new Vector3(0.10f, 0.40f, 0.10f) },
        new() { BoneName = "mAnkleRight", Offset = new Vector3(0, 0, 0), Size = new Vector3(0.08f, 0.06f, 0.16f) },
    };

    /// <summary>
    /// Creates a MeshInstance3D with a procedural humanoid mesh skinned to the given skeleton.
    /// </summary>
    public static MeshInstance3D Create(Skeleton3D skeleton, Color color)
    {
        var meshInstance = new MeshInstance3D();
        var surfaceTool = new SurfaceTool();
        surfaceTool.Begin(Mesh.PrimitiveType.Triangles);

        foreach (var part in _bodyParts)
        {
            int boneIdx = skeleton.FindBone(part.BoneName);
            if (boneIdx < 0)
            {
                GD.Print($"[ProceduralAvatarMesh] Bone '{part.BoneName}' not found, skipping body part");
                continue;
            }

            // Author the body part at the bone's global rest transform. The mesh is skinned
            // with bind pose = inverse rest, so vertices authored in mesh-local space collapse
            // back onto the skeleton origin; placing them at the rest transform makes each part
            // appear at its bone and form a spread-out figure.
            Transform3D boneRest = skeleton.GetBoneGlobalRest(boneIdx);

            if (part.IsSphere)
            {
                AddSkinnedSphere(surfaceTool, boneRest, part.Offset, part.Size.X, boneIdx, 8);
            }
            else
            {
                AddSkinnedBox(surfaceTool, boneRest, part.Offset, part.Size, boneIdx);
            }
        }

        surfaceTool.GenerateNormals();
        var mesh = surfaceTool.Commit();
        meshInstance.Mesh = mesh;
        meshInstance.Skeleton = new NodePath("..");

        var material = new StandardMaterial3D
        {
            AlbedoColor = color,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        meshInstance.MaterialOverride = material;

        return meshInstance;
    }

    private static void AddSkinnedBox(SurfaceTool st, Transform3D boneRest, Vector3 center, Vector3 size, int boneIdx)
    {
        var half = size * 0.5f;

        // 8 corners around 'center', each placed at the bone's global rest transform.
        Vector3[] corners = new Vector3[]
        {
            boneRest * (center + new Vector3(-half.X, -half.Y, -half.Z)),
            boneRest * (center + new Vector3( half.X, -half.Y, -half.Z)),
            boneRest * (center + new Vector3( half.X,  half.Y, -half.Z)),
            boneRest * (center + new Vector3(-half.X,  half.Y, -half.Z)),
            boneRest * (center + new Vector3(-half.X, -half.Y,  half.Z)),
            boneRest * (center + new Vector3( half.X, -half.Y,  half.Z)),
            boneRest * (center + new Vector3( half.X,  half.Y,  half.Z)),
            boneRest * (center + new Vector3(-half.X,  half.Y,  half.Z)),
        };

        // 6 faces, 2 triangles each
        int[][] faces = new int[][]
        {
            new[] {0, 1, 2, 3}, // front
            new[] {5, 4, 7, 6}, // back
            new[] {4, 0, 3, 7}, // left
            new[] {1, 5, 6, 2}, // right
            new[] {3, 2, 6, 7}, // top
            new[] {4, 5, 1, 0}, // bottom
        };

        foreach (var face in faces)
        {
            AddSkinnedQuad(st, corners[face[0]], corners[face[1]], corners[face[2]], corners[face[3]], boneIdx);
        }
    }

    private static void AddSkinnedQuad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, int boneIdx)
    {
        // Triangle 1: a, b, c
        AddSkinnedVertex(st, a, boneIdx);
        AddSkinnedVertex(st, b, boneIdx);
        AddSkinnedVertex(st, c, boneIdx);

        // Triangle 2: a, c, d
        AddSkinnedVertex(st, a, boneIdx);
        AddSkinnedVertex(st, c, boneIdx);
        AddSkinnedVertex(st, d, boneIdx);
    }

    private static void AddSkinnedVertex(SurfaceTool st, Vector3 pos, int boneIdx)
    {
        // Set bone weights: 100% weight on the single bone
        st.SetBones(new int[] { boneIdx, 0, 0, 0 });
        st.SetWeights(new float[] { 1.0f, 0.0f, 0.0f, 0.0f });
        st.AddVertex(pos);
    }

    private static void AddSkinnedSphere(SurfaceTool st, Transform3D boneRest, Vector3 center, float radius, int boneIdx, int segments)
    {
        // Simple UV sphere, placed at the bone's global rest transform.
        for (int lat = 0; lat < segments; lat++)
        {
            float theta1 = Mathf.Pi * lat / segments;
            float theta2 = Mathf.Pi * (lat + 1) / segments;

            for (int lon = 0; lon < segments * 2; lon++)
            {
                float phi1 = Mathf.Tau * lon / (segments * 2);
                float phi2 = Mathf.Tau * (lon + 1) / (segments * 2);

                Vector3 p1 = boneRest * (center + SphericalToCartesian(radius, theta1, phi1));
                Vector3 p2 = boneRest * (center + SphericalToCartesian(radius, theta1, phi2));
                Vector3 p3 = boneRest * (center + SphericalToCartesian(radius, theta2, phi2));
                Vector3 p4 = boneRest * (center + SphericalToCartesian(radius, theta2, phi1));

                // Two triangles per quad
                AddSkinnedVertex(st, p1, boneIdx);
                AddSkinnedVertex(st, p3, boneIdx);
                AddSkinnedVertex(st, p2, boneIdx);

                AddSkinnedVertex(st, p1, boneIdx);
                AddSkinnedVertex(st, p4, boneIdx);
                AddSkinnedVertex(st, p3, boneIdx);
            }
        }
    }

    private static Vector3 SphericalToCartesian(float r, float theta, float phi)
    {
        float sinTheta = Mathf.Sin(theta);
        return new Vector3(
            r * sinTheta * Mathf.Cos(phi),
            r * Mathf.Cos(theta),
            r * sinTheta * Mathf.Sin(phi)
        );
    }
}
