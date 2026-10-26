using System;
using Godot;
using SLNG.Core.Camera;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>
    /// FEAT-UI-73: which minimum camera distance an Alt+Click focus gets. The numbers themselves are tested in
    /// SLNG.Core.Tests; this is the engine half, <see cref="AvatarController.ClassifyFocus"/> deciding from a physics
    /// body whether it is a person (or something worn by one), the ground or an object. Runs on bodies that are
    /// never added to the tree, so it touches nothing of the running client or the user's data.
    /// </summary>
    private static Check CheckCameraFocusKinds()
    {
        const string Name = "camera focus kinds (minimum zoom distance)";
        Node3D? root = null;
        try
        {
            root = new Node3D();

            var terrain = new StaticBody3D { CollisionLayer = PhysicsLayers.Terrain };
            var prim = new StaticBody3D { CollisionLayer = PhysicsLayers.Objects };
            var phantom = new StaticBody3D { CollisionLayer = PhysicsLayers.Phantom };
            var person = new StaticBody3D { CollisionLayer = PhysicsLayers.Avatars };

            // A worn item: a phantom body (that is where AvatarRenderer puts attachment colliders) deep under the
            // wearer's skeleton, the nesting AvatarRenderer.BuildPickChunk builds for a rigged worn item.
            var skeleton = new Skeleton3D();
            var attach = new BoneAttachment3D();
            var holder = new Node3D();
            var worn = new StaticBody3D { CollisionLayer = PhysicsLayers.Phantom };
            root.AddChild(skeleton);
            skeleton.AddChild(attach);
            attach.AddChild(holder);
            holder.AddChild(worn);
            root.AddChild(terrain);
            root.AddChild(prim);
            root.AddChild(phantom);
            root.AddChild(person);

            var problems = new System.Collections.Generic.List<string>();
            void Expect(string what, CameraFocusKind got, CameraFocusKind want)
            {
                if (got != want) problems.Add($"{what}: {got}, want {want}");
            }

            Expect("terrain", AvatarController.ClassifyFocus(terrain), CameraFocusKind.Land);
            Expect("no body", AvatarController.ClassifyFocus(null), CameraFocusKind.Land);
            Expect("prim", AvatarController.ClassifyFocus(prim), CameraFocusKind.Object);
            Expect("phantom prim (foliage)", AvatarController.ClassifyFocus(phantom), CameraFocusKind.Object);
            Expect("avatar body", AvatarController.ClassifyFocus(person), CameraFocusKind.Avatar);
            Expect("worn item", AvatarController.ClassifyFocus(worn), CameraFocusKind.Avatar);

            // And the ends of the range they lead to: an object 2 cm, the ground 15 cm, a person 50 cm.
            if (CameraZoomLimits.MinDistance(CameraFocusKind.Object) != 0.02f) problems.Add("object floor is not 0.02 m");
            if (CameraZoomLimits.MinDistance(CameraFocusKind.Land) != 0.15f) problems.Add("ground floor is not 0.15 m");
            if (CameraZoomLimits.MinDistance(CameraFocusKind.Avatar) != 0.5f) problems.Add("person floor is not 0.5 m");

            // The 3D view's shape: a missing viewport reports 16:9, and that gives the viewer's 98.4 degree ceiling.
            float aspect = SLNG.App.UI.CameraSettings.ViewAspect(null);
            if (Math.Abs(CameraFovLimits.MaxDegrees(aspect) - 98.4375f) > 0.01f) problems.Add("16:9 ceiling is not 98.4375");

            return problems.Count == 0
                ? new Check(Name, true, "object 0.02 m, ground 0.15 m, person and worn items 0.5 m; 16:9 FOV range 5-98.4 degrees")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (root != null && GodotObject.IsInstanceValid(root)) root.Free(); // never in the tree: frees the children too
        }
    }
}
