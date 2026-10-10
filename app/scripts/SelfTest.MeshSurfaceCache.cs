using System;
using System.Collections.Generic;
using Godot;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>
    /// BUG-PERF-15: Verifies that <see cref="MeshSurfaceCache"/> caches CPU vertex/index arrays
    /// for committed ArrayMeshes, preventing synchronous RenderingServer readbacks.
    /// </summary>
    private static Check CheckMeshSurfaceCache()
    {
        const string Name = "mesh surface cache (BUG-PERF-15)";
        var problems = new List<string>();

        try
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            st.SetNormal(new Vector3(0, 1, 0));
            st.AddVertex(new Vector3(0, 0, 0));
            st.AddVertex(new Vector3(1, 0, 0));
            st.AddVertex(new Vector3(0, 1, 0));
            st.AddIndex(0);
            st.AddIndex(1);
            st.AddIndex(2);
            var arrays = st.CommitToArrays();

            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            MeshSurfaceCache.Store(mesh, new[] { arrays });

            if (!MeshSurfaceCache.TryGetSurfaces(mesh, out var cachedSurfaces))
            {
                problems.Add("stored mesh was not found in cache");
            }
            else if (cachedSurfaces.Length != 1)
            {
                problems.Add($"expected 1 surface, got {cachedSurfaces.Length}");
            }

            if (!MeshSurfaceCache.TryGetSurface(mesh, 0, out var cachedSurface))
            {
                problems.Add("surface 0 was not found in cache");
            }
            else
            {
                var verts = cachedSurface[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                if (verts.Length != 3) problems.Add($"expected 3 vertices, got {verts.Length}");
            }

            if (MeshSurfaceCache.TryGetSurface(mesh, 5, out _))
            {
                problems.Add("out-of-bounds surface index returned true");
            }

            var otherMesh = new ArrayMesh();
            if (MeshSurfaceCache.TryGetSurfaces(otherMesh, out _))
            {
                problems.Add("untracked mesh returned cached surfaces");
            }
        }
        catch (Exception ex)
        {
            problems.Add($"{ex.GetType().Name}: {ex.Message}");
        }

        return problems.Count == 0
            ? new Check(Name, true, "mesh surface cache retains CPU arrays and serves clone surfaces without readbacks")
            : new Check(Name, false, string.Join("; ", problems));
    }
}
