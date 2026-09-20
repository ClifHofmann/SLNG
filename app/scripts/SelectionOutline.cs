using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SLNG.App;

/// <summary>FEAT-UI-32 / FEAT-UI-23: the selection highlight, shared by the two renderers that
/// draw selectable things -- <see cref="ObjectRenderer"/> for world prims and
/// <see cref="AvatarRenderer"/> for worn items.</summary>
/// <remarks>
/// It started inside ObjectRenderer, which was fine while only world prims could be selected.
/// Worn items are drawn by AvatarRenderer instead, so selecting one produced no outline at all
/// -- reported in-world with a Firestorm screenshot of a rigged shirt outlined and under the
/// gizmo. Rather than a second copy of the hull-welding and the shader wiring, both call this.
///
/// The reasoning behind the inverted hull, the clip-space offset and the welded normals is in
/// docs/specs/FEAT-UI-32-selection-outline.md and in the shader itself.
/// </remarks>
public static class SelectionOutline
{
    /// <summary>The reference viewer's colours, from skins/default/colors.xml:
    /// SilhouetteParentColor is "Yellow" (1 1 0) and SilhouetteChildColor is (0.13 0.42 0.77).</summary>
    public static readonly Color RootColor = new(1.0f, 1.0f, 0.0f);
    public static readonly Color ChildColor = new(0.13f, 0.42f, 0.77f);

    /// <summary>The reference viewer's SelectionHighlightThickness is 0.01 of the camera
    /// distance, and the ribbon straddles the edge so only half of it shows against the
    /// background. At 1080p and 60 degrees that outer half is a little under 5 px.</summary>
    private const float ThicknessPixels = 4.0f;

    public const string NodeName = "SelectionOutline";
    public const string MaskNodeName = "SelectionDepthMask";
    private const string SourceMeta = "slng_outline_source";

    private static ShaderMaterial? _root;
    private static ShaderMaterial? _child;
    private static ShaderMaterial? _mask;

    // Keyed by the source mesh's instance id. Prims share mesh resources, so a linkset of
    // identical parts builds one hull. Cleared wholesale past the cap rather than
    // reference-counted: the entries are only reachable while something is selected.
    private static readonly Dictionary<ulong, ArrayMesh> _hulls = new();
    private const int HullCacheCap = 64;

    private static void EnsureMaterials()
    {
        if (_root != null) return;

        var shader = GD.Load<Shader>("res://materials/selection_outline.gdshader");
        _root = new ShaderMaterial { Shader = shader, RenderPriority = 1 };
        _root.SetShaderParameter("outline_color", RootColor);
        _root.SetShaderParameter("outline_thickness_px", ThicknessPixels);
        _child = new ShaderMaterial { Shader = shader, RenderPriority = 1 };
        _child.SetShaderParameter("outline_color", ChildColor);
        _child.SetShaderParameter("outline_thickness_px", ThicknessPixels);

        // The depth mask must reach the depth buffer before the outline is measured against it.
        _mask = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://materials/selection_depth_mask.gdshader"),
            RenderPriority = 0,
        };
    }

    /// <summary>Builds the hull the outline shader inflates: the same geometry, but with vertices
    /// WELDED by position and their normals averaged.</summary>
    /// <remarks>
    /// The welding is the whole point. A prim box arrives with 24 vertices carrying six face
    /// normals, and pushing each face out along its own normal separates them at every corner,
    /// leaving a notch in the outline exactly where the eye looks for a corner. Averaging the
    /// normals of the faces that meet at a position gives that corner one outward direction, and
    /// the hull stays closed. Face normals are accumulated un-normalised so that a large triangle
    /// counts for more than a sliver.
    /// </remarks>
    private static ArrayMesh? BuildOutlineHull(Mesh source)
    {
        var welded = new Dictionary<(int, int, int), int>();
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<int>();

        // 0.1 mm. Fine enough that two genuinely distinct vertices are never merged, coarse
        // enough to catch the float noise in coordinates that came through a mesh decoder.
        const float weldQuantum = 0.0001f;

        // Only an ArrayMesh can be asked what a surface's primitive type is; the procedural
        // fallback shapes (BoxMesh and friends) are PrimitiveMesh and are always triangles.
        var arrayMesh = source as ArrayMesh;

        for (int surface = 0; surface < source.GetSurfaceCount(); surface++)
        {
            if (arrayMesh != null
                && arrayMesh.SurfaceGetPrimitiveType(surface) != Mesh.PrimitiveType.Triangles) continue;

            var arrays = source.SurfaceGetArrays(surface);
            if (arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;
            var sourceVerts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            if (sourceVerts.Length == 0) continue;
            // The mesh's own normals when it has them, which is every prim and every decoded
            // mesh asset. The geometric fallback below exists only for geometry that arrives
            // without them.
            var sourceNormals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            bool haveNormals = sourceNormals.Length == sourceVerts.Length;

            var sourceIndices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            int triangleVertexCount = sourceIndices.Length > 0 ? sourceIndices.Length : sourceVerts.Length;
            if (triangleVertexCount % 3 != 0) continue;

            int Weld(int sourceIndex)
            {
                var v = sourceVerts[sourceIndex];
                var key = (Mathf.RoundToInt(v.X / weldQuantum),
                           Mathf.RoundToInt(v.Y / weldQuantum),
                           Mathf.RoundToInt(v.Z / weldQuantum));
                if (welded.TryGetValue(key, out int existing)) return existing;
                welded[key] = positions.Count;
                positions.Add(v);
                normals.Add(Vector3.Zero);
                return positions.Count - 1;
            }

            for (int i = 0; i < triangleVertexCount; i += 3)
            {
                int sourceA = sourceIndices.Length > 0 ? sourceIndices[i] : i;
                int sourceB = sourceIndices.Length > 0 ? sourceIndices[i + 1] : i + 1;
                int sourceC = sourceIndices.Length > 0 ? sourceIndices[i + 2] : i + 2;
                int a = Weld(sourceA);
                int b = Weld(sourceB);
                int c = Weld(sourceC);
                if (a == b || b == c || a == c) continue;

                if (haveNormals)
                {
                    normals[a] += sourceNormals[sourceA];
                    normals[b] += sourceNormals[sourceB];
                    normals[c] += sourceNormals[sourceC];
                }
                else
                {
                    // NEGATED on purpose. Godot's front faces are wound CLOCKWISE, so for a
                    // front-facing triangle (b-a)x(c-a) points INTO the object. Feeding that
                    // straight in shrinks the hull instead of inflating it, and the outline then
                    // disappears completely behind the object -- which is exactly what a test
                    // render of this function's first version showed.
                    var faceNormal = -(positions[b] - positions[a]).Cross(positions[c] - positions[a]);
                    normals[a] += faceNormal;
                    normals[b] += faceNormal;
                    normals[c] += faceNormal;
                }
                indices.Add(a);
                indices.Add(b);
                indices.Add(c);
            }
        }

        if (indices.Count == 0) return null;

        var normalArray = new Vector3[normals.Count];
        for (int i = 0; i < normals.Count; i++)
        {
            // A vertex whose faces cancel out (a zero-thickness fold) has no outward direction;
            // leaving it at zero makes the shader's edge-on guard skip it rather than push it
            // somewhere arbitrary.
            normalArray[i] = normals[i].LengthSquared() > 0f ? normals[i].Normalized() : Vector3.Zero;
        }

        var meshArrays = new Godot.Collections.Array();
        meshArrays.Resize((int)Mesh.ArrayType.Max);
        meshArrays[(int)Mesh.ArrayType.Vertex] = positions.ToArray();
        meshArrays[(int)Mesh.ArrayType.Normal] = normalArray;
        meshArrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

        var hull = new ArrayMesh();
        hull.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, meshArrays);
        return hull;
    }
    public static ArrayMesh? GetHull(Mesh source)
    {
        ulong key = source.GetInstanceId();
        if (_hulls.TryGetValue(key, out var cached) && GodotObject.IsInstanceValid(cached)) return cached;
        var hull = BuildOutlineHull(source);
        if (hull == null) return null;
        if (_hulls.Count >= HullCacheCap) _hulls.Clear();
        _hulls[key] = hull;
        return hull;
    }

    /// <summary>The named child, unless it is already on its way out.</summary>
    /// <remarks>
    /// QueueFree does not remove the node, it schedules the removal for the end of the frame, so
    /// a plain GetNodeOrNull still finds one that is about to vanish. Rebuilding a highlight
    /// within one frame did exactly that and every outline disappeared.
    /// </remarks>
    public static MeshInstance3D? FindLiveChild(Node parent, string name)
    {
        var node = parent.GetNodeOrNull<MeshInstance3D>(name);
        return node != null && !node.IsQueuedForDeletion() ? node : null;
    }

    /// <summary>Takes the node out of the tree NOW and frees it afterwards, so a rebuild in the
    /// same frame neither finds it nor collides with its name.</summary>
    public static void Discard(Node parent, Node? child)
    {
        if (child == null) return;
        parent.RemoveChild(child);
        child.QueueFree();
    }

    /// <summary>Draws the outline on a mesh instance, or takes it off again.</summary>
    /// <param name="skinnedFrom">For a RIGGED mesh, the instance whose skin and skeleton the
    /// hull must share -- without them the hull would hang in the bind pose while the item it
    /// outlines moves with the body. Null for static geometry.</param>
    public static void Apply(MeshInstance3D target, bool visible, bool isRoot, MeshInstance3D? skinnedFrom = null)
    {
        var existing = FindLiveChild(target, NodeName);
        var existingMask = FindLiveChild(target, MaskNodeName);

        if (!visible || target.Mesh == null)
        {
            Discard(target, existing);
            Discard(target, existingMask);
            return;
        }

        var hull = GetHull(target.Mesh);
        if (hull == null) return;

        EnsureMaterials();
        var material = isRoot ? _root : _child;

        if (existing == null)
        {
            existing = new MeshInstance3D
            {
                Name = NodeName,
                Mesh = hull,
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            target.AddChild(existing);
        }
        else
        {
            existing.Mesh = hull;
            existing.MaterialOverride = material;
        }
        existing.SetMeta(SourceMeta, (long)target.Mesh.GetInstanceId());

        // The mask carries the object's OWN geometry, not the hull: it stands in for the depth
        // the object may not have written itself (an alpha-blended surface writes none).
        if (existingMask == null)
        {
            existingMask = new MeshInstance3D
            {
                Name = MaskNodeName,
                Mesh = target.Mesh,
                MaterialOverride = _mask,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            target.AddChild(existingMask);
        }
        else
        {
            existingMask.Mesh = target.Mesh;
        }

        if (skinnedFrom != null)
        {
            // Same skin and the same skeleton path, so the hull and the mask deform exactly as
            // the item does. The skeleton path is relative to the node, and both sit one level
            // below the instance it was resolved for, so it has to be re-resolved rather than
            // copied verbatim.
            BindSkin(existing, skinnedFrom);
            BindSkin(existingMask, skinnedFrom);
        }
    }

    private static void BindSkin(MeshInstance3D node, MeshInstance3D skinnedFrom)
    {
        node.Skin = skinnedFrom.Skin;
        var skeleton = skinnedFrom.GetNodeOrNull<Skeleton3D>(skinnedFrom.Skeleton);
        if (skeleton != null) node.Skeleton = node.GetPathTo(skeleton);
        // The bind pose can be far from where the item is drawn; without a generous box Godot
        // culls the hull long before the item itself goes off screen.
        node.CustomAabb = skinnedFrom.CustomAabb;
    }

    /// <summary>The mesh the outline on this instance was cut from, so a caller can notice that
    /// the geometry underneath has been replaced.</summary>
    public static long SourceMeshIdOf(MeshInstance3D outline) => outline.GetMeta(SourceMeta, 0L).AsInt64();
}
