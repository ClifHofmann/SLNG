using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App.Input;

public partial class CursorManager : Node
{
    private World? _world;
    private Camera3D? _camera;

    public void Initialize(World world, Camera3D camera)
    {
        _world = world;
        _camera = camera;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_world == null || _camera == null || !IsInstanceValid(_camera)) return;

        // 1. Alt key overrides hover with Cross/Zoom cursor
        if (Godot.Input.IsKeyPressed(Key.Alt))
        {
            Godot.Input.SetDefaultCursorShape(Godot.Input.CursorShape.Cross);
            return;
        }

        // 2. Check UI hover.
        var viewport = GetViewport();
        if (viewport == null) return;
        var focusOwner = viewport.GuiGetFocusOwner();
        bool hasUiFocus = focusOwner is LineEdit || focusOwner is TextEdit
            || viewport.GuiGetHoveredControl() != null;
        
        if (hasUiFocus)
        {
            Godot.Input.SetDefaultCursorShape(Godot.Input.CursorShape.Arrow);
            return;
        }

        Godot.Input.CursorShape desiredShape = Godot.Input.CursorShape.Arrow;

        // 3. Raycast into the world
        var mousePos = viewport.GetMousePosition();
        var spaceState = _camera.GetWorld3D().DirectSpaceState;
        
        var rayOrigin = _camera.ProjectRayOrigin(mousePos);
        var rayEnd = rayOrigin + _camera.ProjectRayNormal(mousePos) * 1000f;

        var query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayEnd);
        query.CollisionMask = 1u; // Layer 1 (Terrain/Objects)

        var result = spaceState.IntersectRay(query);

        if (result.Count > 0)
        {
            var collider = result["collider"].AsGodotObject();
            if (collider is Node node)
            {
                if (node.HasMeta("EntityId"))
                {
                    var idStr = node.GetMeta("EntityId").AsString();
                    if (System.Guid.TryParse(idStr, out var entityId))
                    {
                        var entity = _world.GetEntity(entityId);
                        if (entity != null)
                        {
                            var prim = entity.GetComponent<PrimitiveComponent>();
                            if (prim != null)
                            {
                                // In SL, Touch (0) is default. We usually only show the hand if
                                // the object actually has a touch script, but we don't sync script presence yet.
                                // However, Sit (1), Buy (2), Pay (3), OpenTask (4), PlayMedia (5), OpenMedia (6)
                                // are explicit actions that always warrant a pointer change.
                                // Let's also show it for Touch (0) if the object name isn't default "Object"? No, that's brittle.
                                // For MVP, we'll show PointingHand for anything with ClickAction != 0.
                                if (prim.ClickAction != 0)
                                {
                                    desiredShape = Godot.Input.CursorShape.PointingHand;
                                }
                            }
                        }
                    }
                }
            }
        }

        Godot.Input.SetDefaultCursorShape(desiredShape);
    }
}
