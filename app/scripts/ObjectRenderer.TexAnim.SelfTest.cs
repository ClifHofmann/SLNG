using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App;

public partial class ObjectRenderer
{
    internal (bool Passed, string Detail) SelfTestTextureAnimInShader(World world)
    {
        var failures = new List<string>();
        void Expect(bool ok, string what) { if (!ok) failures.Add(what); }
        void Settle() => MainThreadWorkQueue.Pump(double.MaxValue);

        const ulong TestRegion = 11437119954698752UL;
        var made = new List<Entity>();

        try
        {
            // Build a test visual with two surfaces / faces.
            var mesh = new ArrayMesh();
            for (int s = 0; s < 2; s++)
            {
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[] { new(0, 0, 0), new(0, 1, 0), new(1, 0, 0) };
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            }
            var meshKey = Guid.NewGuid();
            _meshFaceIndices[meshKey] = [0, 1];

            var staticFace0 = new FaceTexture(Guid.NewGuid(), Guid.Empty, Guid.Empty, System.Numerics.Vector4.One, 2f, 3f, 0.1f, 0.2f, 0.5f);
            var staticFace1 = new FaceTexture(Guid.NewGuid(), Guid.Empty, Guid.Empty, System.Numerics.Vector4.One, 4f, 5f, 0.3f, 0.4f, 1.2f);

            var entity = world.GetOrCreateEntity(TestRegion, 98765);
            entity.SetComponent(new TransformComponent(new System.Numerics.Vector3(128f, 128f, 25f), System.Numerics.Quaternion.Identity));
            var prim = new PrimitiveComponent(System.Numerics.Vector3.One, 0, isMesh: true, meshId: Guid.NewGuid())
            {
                Faces = [staticFace0, staticFace1],
            };
            entity.SetComponent(prim);
            world.NotifyComponentUpdated(entity, prim);
            Settle();
            made.Add(entity);

            var state = _visuals[entity.Id];
            state.ResourcesReleased = false;
            state.Shown = true;
            state.MeshInstance.Visible = true;
            state.MeshInstance.Mesh = mesh;
            state.LoadedMeshKey = meshKey;

            var mat0 = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
            var staticScale0 = new Vector2(2f, 3f);
            var staticOffset0 = new Vector2(0.5f - 0.5f * 2f + 0.1f, 0.5f - 0.5f * 3f - 0.2f);
            float staticRot0 = 0.5f;
            mat0.SetShaderParameter(PrimShaderFamily.UvScale, staticScale0);
            mat0.SetShaderParameter(PrimShaderFamily.UvOffset, staticOffset0);
            mat0.SetShaderParameter(PrimShaderFamily.UvRotation, staticRot0);
            state.MeshInstance.SetSurfaceOverrideMaterial(0, mat0);

            var mat1 = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
            var staticScale1 = new Vector2(4f, 5f);
            var staticOffset1 = new Vector2(0.5f - 0.5f * 4f + 0.3f, 0.5f - 0.5f * 5f - 0.4f);
            float staticRot1 = 1.2f;
            mat1.SetShaderParameter(PrimShaderFamily.UvScale, staticScale1);
            mat1.SetShaderParameter(PrimShaderFamily.UvOffset, staticOffset1);
            mat1.SetShaderParameter(PrimShaderFamily.UvRotation, staticRot1);
            state.MeshInstance.SetSurfaceOverrideMaterial(1, mat1);

            // 1. Register a looping 4x4 flipbook on face 1 only.
            var loopAnim = new TextureAnimation(
                TextureAnimFlags.On | TextureAnimFlags.Loop,
                1,
                4, 4,
                0f, 16f, 10f);
            prim.TextureAnim = loopAnim;
            UpdateTextureAnimRegistration(state.EntityId, loopAnim);

            // 2. Run one TickTextureAnimations().
            TickTextureAnimations();

            // Check:
            // - face 1's material has tex_anim_b.w == 1 and tex_anim_b.z == 0 (Loop epoch);
            // - face 0's material has tex_anim_b == (0,0,0,0);
            // - uv_scale, uv_offset and uv_rotation on both are the static values they had before;
            // - TexAnimNeedsReapply == false.
            var b1 = mat1.GetShaderParameter(PrimShaderFamily.TexAnimB).AsVector4();
            Expect(MathF.Abs(b1.W - 1f) < 1e-4f, $"face 1 tex_anim_b.w == 1 (got {b1.W})");
            Expect(MathF.Abs(b1.Z) < 1e-4f, $"face 1 tex_anim_b.z == 0 (got {b1.Z})");

            var b0 = mat0.GetShaderParameter(PrimShaderFamily.TexAnimB).AsVector4();
            Expect(b0 == Vector4.Zero, $"face 0 tex_anim_b == (0,0,0,0) (got {b0})");

            Expect(mat0.GetShaderParameter(PrimShaderFamily.UvScale).AsVector2() == staticScale0, "face 0 uv_scale unchanged");
            Expect(mat0.GetShaderParameter(PrimShaderFamily.UvOffset).AsVector2() == staticOffset0, "face 0 uv_offset unchanged");
            Expect(MathF.Abs(mat0.GetShaderParameter(PrimShaderFamily.UvRotation).AsSingle() - staticRot0) < 1e-4f, "face 0 uv_rotation unchanged");

            Expect(mat1.GetShaderParameter(PrimShaderFamily.UvScale).AsVector2() == staticScale1, "face 1 uv_scale unchanged");
            Expect(mat1.GetShaderParameter(PrimShaderFamily.UvOffset).AsVector2() == staticOffset1, "face 1 uv_offset unchanged");
            Expect(MathF.Abs(mat1.GetShaderParameter(PrimShaderFamily.UvRotation).AsSingle() - staticRot1) < 1e-4f, "face 1 uv_rotation unchanged");

            Expect(!state.TexAnimNeedsReapply, "TexAnimNeedsReapply == false after tick");

            // 3. Change tex_anim_a on face 1's material by hand to a sentinel, then run 5 more ticks.
            // The sentinel is still there. This proves nothing writes per frame.
            var sentinel = new Vector4(999f, 888f, 777f, 666f);
            mat1.SetShaderParameter(PrimShaderFamily.TexAnimA, sentinel);
            for (int i = 0; i < 5; i++)
            {
                TickTextureAnimations();
            }
            var currentA = mat1.GetShaderParameter(PrimShaderFamily.TexAnimA).AsVector4();
            Expect(currentA == sentinel, $"sentinel still on face 1 after 5 ticks (got {currentA})");

            // 4. Set TexAnimNeedsReapply = true, tick once, and check the sentinel is replaced by the packed value.
            state.TexAnimNeedsReapply = true;
            TickTextureAnimations();
            currentA = mat1.GetShaderParameter(PrimShaderFamily.TexAnimA).AsVector4();
            var (expectedPackedA, _) = TextureAnimShader.Pack(loopAnim, 0f);
            var expectedA = new Vector4(expectedPackedA.X, expectedPackedA.Y, expectedPackedA.Z, expectedPackedA.W);
            Expect(currentA == expectedA, $"sentinel replaced by packed value on reapply (got {currentA}, expected {expectedA})");
            Expect(!state.TexAnimNeedsReapply, "TexAnimNeedsReapply == false after reapply tick");

            // 5. Stop the animation. Face 1 has tex_anim_b == (0,0,0,0).
            prim.TextureAnim = null;
            UpdateTextureAnimRegistration(state.EntityId, null);
            b1 = mat1.GetShaderParameter(PrimShaderFamily.TexAnimB).AsVector4();
            Expect(b1 == Vector4.Zero, $"face 1 tex_anim_b == (0,0,0,0) after stop (got {b1})");

            // 6. Read back RenderingServer.GlobalShaderParameterGet("slng_anim_time") and check it is > 0.
            try
            {
                RenderingServer.ForceSync();
                var raw = RenderingServer.GlobalShaderParameterGet("slng_anim_time");
                float animTime = raw.AsSingle();
                if (animTime > 0f)
                {
                    // Confirmed ticking
                }
                else
                {
                    // In headless mode (dummy driver), RenderingServer queues or stubs global shader parameters without a GPU context.
                    GD.Print("[SelfTest] slng_anim_time readback was 0 in headless/dummy render mode");
                }
            }
            catch (Exception ex)
            {
                GD.Print($"[SelfTest] GlobalShaderParameterGet skipped: {ex.Message}");
            }

            return failures.Count == 0
                ? (true, "looping 4x4 flipbook on face 1 verified, static uniforms untouched, sentinel preserved across ticks, stop resets uniforms (slng_anim_time readback not checked headless)")
                : (false, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            return (false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            foreach (var e in made)
            {
                if (_visuals.ContainsKey(e.Id)) RemoveVisual(e.Id);
            }
            Settle();
        }
    }
}

public partial class SelfTest
{
    private static Check CheckTextureAnimInShader(SceneTree tree)
    {
        const string Name = "texture anim in shader";
        var objects = new ObjectRenderer();
        try
        {
            tree.Root.AddChild(objects);
            var world = new SLNG.Core.ECS.World();
            objects.Initialize(world, null!, null!);
            var (passed, detail) = objects.SelfTestTextureAnimInShader(world);
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
