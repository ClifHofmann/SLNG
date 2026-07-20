using Godot;
using SLNG.Core;
using SLNG.Core.ECS;
using SLNG.Net;

namespace SLNG.App
{
    public partial class ObjectSelectionController : Node
    {
        private World _world = null!;
        private GridSession _session = null!;
        private Camera3D _camera = null!;
        private UI.InWorldContextMenu _contextMenu = null!;

        public void Initialize(World world, GridSession session, Camera3D camera, UI.InWorldContextMenu contextMenu)
        {
            _world = world;
            _session = session;
            _camera = camera;
            _contextMenu = contextMenu;
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (_world == null || _session == null || _camera == null) return;

            if (@event is InputEventMouseButton mouseBtn && !mouseBtn.Pressed)
            {
                if (mouseBtn.ButtonIndex == MouseButton.Right || mouseBtn.ButtonIndex == MouseButton.Left)
                {
                    var result = RaycastFromMouse(mouseBtn.Position);
                    
                    if (result.Count > 0)
                    {
                        var collider = result["collider"].As<Node>();
                        if (collider is StaticBody3D staticBody && staticBody.HasMeta("LocalId"))
                        {
                            uint localId = uint.Parse(staticBody.GetMeta("LocalId").AsString());
                            var entityIdStr = staticBody.GetMeta("EntityId").AsString();
                            
                            if (System.Guid.TryParse(entityIdStr, out var guid))
                            {
                                var entity = _world.GetEntity(guid);
                                if (entity != null)
                                {
                                    _world.SelectEntity(entity);
                                    _session.SelectObject(localId);

                                    if (mouseBtn.ButtonIndex == MouseButton.Right)
                                    {
                                        _contextMenu.ShowMenu(mouseBtn.Position, entity, localId);
                                    }
                                    
                                    GetViewport().SetInputAsHandled();
                                    return;
                                }
                            }
                        }
                    }
                    else
                    {
                        // Clicked on nothing, deselect
                        if (_world.SelectedEntity != null && mouseBtn.ButtonIndex == MouseButton.Left)
                        {
                            _world.DeselectEntity();
                        }
                    }
                }
            }
        }

        private Godot.Collections.Dictionary RaycastFromMouse(Vector2 mousePos)
        {
            var spaceState = _camera.GetWorld3D().DirectSpaceState;
            var from = _camera.ProjectRayOrigin(mousePos);
            var to = from + _camera.ProjectRayNormal(mousePos) * 1000f;

            var query = PhysicsRayQueryParameters3D.Create(from, to);
            // Optional: Set collision mask here if needed

            return spaceState.IntersectRay(query);
        }
    }
}
