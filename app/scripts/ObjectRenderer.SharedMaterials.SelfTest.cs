using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App;

public partial class ObjectRenderer
{
    internal (bool Passed, string Detail) SelfTestSharedMaterials(World world)
    {
        var failures = new List<string>();
        void Expect(bool ok, string what) { if (!ok) failures.Add(what); }

        bool prevShare = RenderConfig.ShareMaterials;
        RenderConfig.ShareMaterials = true;

        try
        {
            _materialCache.Clear();

            // 1. Equal faces share one material
            var texId = Guid.NewGuid();
            var face1 = new FaceTexture(texId, Guid.Empty, Guid.Empty, System.Numerics.Vector4.One, 1f, 1f, 0f, 0f, 0f);
            var face2 = new FaceTexture(texId, Guid.Empty, Guid.Empty, System.Numerics.Vector4.One, 1f, 1f, 0f, 0f, 0f);
            var scale = System.Numerics.Vector3.One;

            var task1 = BuildFaceMaterialAsync(face1, scale, 1000f, 1f);
            var task2 = BuildFaceMaterialAsync(face2, scale, 1000f, 1f);
            System.Threading.Tasks.Task.WaitAll(task1, task2);

            var (mat1, _) = task1.Result;
            var (mat2, _) = task2.Result;

            Expect(mat1 != null && mat2 != null, "materials built successfully");
            Expect(ReferenceEquals(mat1, mat2), "equal faces share the identical ShaderMaterial instance");
            Expect(_materialCache.Count == 1, $"material cache has 1 unique entry (got {_materialCache.Count})");

            var key = PrimMaterialKey.Create(face1, scale, 0);
            Expect(_materialCache.TryGet(key, out var entry) && entry.RefCount == 2, "entry ref count is 2 for both sharers");

            // Setup entities in world
            var e1 = world.GetOrCreateEntity(1, 1001);
            e1.SetComponent(new TransformComponent(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity));
            var e2 = world.GetOrCreateEntity(1, 1002);
            e2.SetComponent(new TransformComponent(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity));

            // Setup VisualState 1 with 1 surface sharing mat1
            var mi1 = new MeshInstance3D();
            AddChild(mi1);
            var mesh1 = new ArrayMesh();
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[] { new(0, 0, 0), new(0, 1, 0), new(1, 0, 0) };
            mesh1.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mi1.Mesh = mesh1;
            mi1.SetSurfaceOverrideMaterial(0, mat1);

            var state1 = new VisualState
            {
                EntityId = e1.Id,
                MeshInstance = mi1,
                SurfaceMaterialKeys = new PrimMaterialKey[] { key },
                SurfaceDetached = new byte[1]
            };
            _visuals[state1.EntityId] = state1;

            // Setup VisualState 2 with 1 surface sharing mat1 (mat2)
            var mi2 = new MeshInstance3D();
            AddChild(mi2);
            mi2.Mesh = mesh1;
            mi2.SetSurfaceOverrideMaterial(0, mat2);
            var state2 = new VisualState
            {
                EntityId = e2.Id,
                MeshInstance = mi2,
                SurfaceMaterialKeys = new PrimMaterialKey[] { key },
                SurfaceDetached = new byte[1]
            };
            _visuals[state2.EntityId] = state2;

            // 2. Texture upgrade reaches every sharer
            var dummyTex = ImageTexture.CreateFromImage(Image.CreateEmpty(4, 4, false, Image.Format.Rgba8));
            mat1!.SetShaderParameter(PrimShaderFamily.AlbedoTexture, dummyTex);
            var readFrom2 = mi2.GetSurfaceOverrideMaterial(0) as ShaderMaterial;
            Expect(readFrom2 != null && ReferenceEquals(readFrom2.GetShaderParameter(PrimShaderFamily.AlbedoTexture).As<Texture2D>(), dummyTex),
                "texture upgrade on shared material is immediately visible to second sharer");

            // 3. Mirror face detaches and returns
            mat1.SetShaderParameter(PrimShaderFamily.Fullbright, true);
            mat1.SetShaderParameter(PrimShaderFamily.LegacyShininess, 0.9f);
            ApplyMirrorShader(state1, true);
            var detachedMirror = mi1.GetSurfaceOverrideMaterial(0) as ShaderMaterial;
            Expect(detachedMirror != null && !ReferenceEquals(detachedMirror, mat1), "mirror face detaches to private duplicate");
            Expect(detachedMirror != null && ReferenceEquals(detachedMirror.Shader, PrimShaderFamily.Mirror), "detached surface has Mirror shader");
            Expect(ReferenceEquals(mi2.GetSurfaceOverrideMaterial(0), mat1), "second sharer remains on shared material during mirror detach");

            ApplyMirrorShader(state1, false);
            var returnedMirror = mi1.GetSurfaceOverrideMaterial(0) as ShaderMaterial;
            Expect(ReferenceEquals(returnedMirror, mat1), "mirror face returns to shared material when mirror turned off");

            // 4. Selection detaches and returns
            HighlightVisual(state1.EntityId.ToString(), true);
            var detachedSelected = mi1.GetSurfaceOverrideMaterial(0) as ShaderMaterial;
            Expect(detachedSelected != null && !ReferenceEquals(detachedSelected, mat1), "selection detaches to private duplicate");
            Expect(ReferenceEquals(mi2.GetSurfaceOverrideMaterial(0), mat1), "second sharer remains on shared material during selection");

            HighlightVisual(state1.EntityId.ToString(), false);
            var returnedSelected = mi1.GetSurfaceOverrideMaterial(0) as ShaderMaterial;
            Expect(ReferenceEquals(returnedSelected, mat1), "selection returns to shared material on deselect");

            // 5. Texanim detaches and returns
            var anim = new TextureAnimation(TextureAnimFlags.On | TextureAnimFlags.Loop, 0, 2, 2, 0f, 4f, 10f);
            var animState = new TexAnimState { Anim = anim, A = new Vector4(2, 2, 0, 0), B = new Vector4(0, 0, 0, 1) };
            ApplyTexAnimUniforms(state1, animState);
            var detachedAnim = mi1.GetSurfaceOverrideMaterial(0) as ShaderMaterial;
            Expect(detachedAnim != null && !ReferenceEquals(detachedAnim, mat1), "texanim detaches surface to private duplicate");
            Expect(ReferenceEquals(mi2.GetSurfaceOverrideMaterial(0), mat1), "second sharer remains on shared material during texanim");

            ApplyTexAnimUniforms(state1, null);
            var returnedAnim = mi1.GetSurfaceOverrideMaterial(0) as ShaderMaterial;
            Expect(ReferenceEquals(returnedAnim, mat1), "texanim returns surface to shared material when animation turns off");

            // 6. Nothing leaks after removal
            ReleaseSurfaceMaterials(state1);
            Expect(_materialCache.Count == 1, $"after releasing state1, entry still alive (count={_materialCache.Count})");
            Expect(entry!.RefCount == 1, $"entry ref count decremented to 1 (got {entry.RefCount})");

            ReleaseSurfaceMaterials(state2);
            Expect(_materialCache.Count == 0, $"after releasing state2, entry removed and cache is empty (got {_materialCache.Count})");

            _visuals.Remove(state1.EntityId);
            _visuals.Remove(state2.EntityId);
            world.RemoveEntity(1, 1001);
            world.RemoveEntity(1, 1002);
            mi1.QueueFree();
            mi2.QueueFree();
            dummyTex.Dispose();

            return (failures.Count == 0, failures.Count == 0
                ? "equal faces share ShaderMaterial; texanim/mirror/select detach & return; texture upgrade shared; refcounted cleanup"
                : string.Join("; ", failures));
        }
        finally
        {
            RenderConfig.ShareMaterials = prevShare;
            _materialCache.Clear();
        }
    }
}

public partial class SelfTest
{
    private static Check CheckSharedMaterials(SceneTree tree)
    {
        const string Name = "shared materials cache (FEAT-PERF-20 Phase A1)";
        var objects = new ObjectRenderer();
        try
        {
            tree.Root.AddChild(objects);
            var world = new SLNG.Core.ECS.World();
            objects.Initialize(world, null!, null!);
            var (passed, detail) = objects.SelfTestSharedMaterials(world);
            return new Check(Name, passed, detail);
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(objects)) objects.QueueFree();
        }
    }
}
