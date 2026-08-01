using Godot;
using SLNG.Core;
using SLNG.Core.Components;
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

        // World.SelectEntity is additive (multiple objects can be selected/edited independently
        // at once, one per open ObjectEditWindow) -- this tracks only what THIS controller most
        // recently raycast-clicked, so "click empty space to deselect" clears just that, not
        // every object some other window still has pinned open.
        private Entity? _lastClicked;

        // Entities with an open ObjectEditWindow (see Boot.cs Pin/Unpin calls) -- these stay
        // selected/highlighted no matter what else gets clicked, since deselecting them would
        // auto-hide their window (ObjectEditWindow.OnEntityDeselected). Anything NOT in this set
        // is just a plain click-highlight, which a click elsewhere should replace -- without this
        // distinction, every plain click left the previous object's highlight stuck forever
        // (_lastClicked only remembers the ONE most recent click, so the one before it was never
        // deselected again).
        private readonly System.Collections.Generic.HashSet<System.Guid> _pinnedEntityIds = new();

        public void Pin(System.Guid entityId) => _pinnedEntityIds.Add(entityId);
        public void Unpin(System.Guid entityId) => _pinnedEntityIds.Remove(entityId);

        public void Initialize(World world, GridSession session, Camera3D camera, UI.InWorldContextMenu contextMenu)
        {
            _world = world;
            _session = session;
            _camera = camera;
            _contextMenu = contextMenu;
            SetProcessUnhandledInput(true);
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (_world == null || _session == null || _camera == null) return;

            if (@event is InputEventMouseButton mouseBtn && mouseBtn.Pressed && !mouseBtn.AltPressed)
            {
                // Same defensive hover check as AvatarController's wheel-zoom guard: reaching
                // _UnhandledInput is supposed to already mean "no Control claimed this," but live
                // testing showed clicks on a window could still land here and raycast/select
                // whatever's in the 3D scene behind it. Checking GuiGetHoveredControl() directly
                // closes that regardless of why the click wasn't actually consumed upstream.
                if (GetViewport().GuiGetHoveredControl() != null) return;

                if (mouseBtn.ButtonIndex == MouseButton.Right || mouseBtn.ButtonIndex == MouseButton.Left)
                {
                    var result = RaycastFromMouse(mouseBtn.Position);

                    if (result.Count > 0)
                    {
                        var collider = result["collider"].As<Node>();
                        // TerrainRenderer's StaticBody also carries a "LocalId" meta (literal
                        // string "TERRAIN", not a real prim local ID) so it renders/highlights
                        // through the same object-tagging convention -- HasMeta alone can't tell
                        // it apart from a real object, so require the value to actually parse as
                        // the uint every real prim LocalId is.
                        bool isTaggedObject = collider is StaticBody3D taggedBody && taggedBody.HasMeta("LocalId")
                            && uint.TryParse(taggedBody.GetMeta("LocalId").AsString(), out _);

                        // Right-click on terrain (or anything untagged) offers "Create" instead
                        // of the object menu -- there's no entity here to Edit/Touch/Inspect.
                        if (!isTaggedObject && mouseBtn.ButtonIndex == MouseButton.Right)
                        {
                            _contextMenu.ShowGroundMenu(mouseBtn.Position, result["position"].AsVector3());
                            GetViewport().SetInputAsHandled();
                            return;
                        }

                        if (collider is StaticBody3D staticBody && staticBody.HasMeta("LocalId"))
                        {
                            var localIdStr = staticBody.GetMeta("LocalId").AsString();
                            if (uint.TryParse(localIdStr, out uint localId))
                            {
                                var entityIdStr = staticBody.GetMeta("EntityId").AsString();
                                
                                if (System.Guid.TryParse(entityIdStr, out var guid))
                                {
                                    var entity = _world.GetEntity(guid);
                                    if (entity != null)
                                    {
                                        // Edit Linked Parts OFF (default): resolve up to the
                                        // linkset's root, matching pre-existing behavior. ON:
                                        // leave the specifically-clicked part as-is (FEAT-UI-06).
                                        if (!SelectionSettings.EditLinkedParts)
                                        {
                                            var transform = entity.GetComponent<TransformComponent>();
                                            if (transform != null && transform.ParentLocalId != 0)
                                            {
                                                var parent = _world.GetEntity(entity.RegionHandle, transform.ParentLocalId);
                                                if (parent != null)
                                                {
                                                    entity = parent;
                                                    localId = transform.ParentLocalId;
                                                }
                                            }
                                        }

                                        if (mouseBtn.ButtonIndex == MouseButton.Left)
                                        {
                                            if (_lastClicked != null && !_pinnedEntityIds.Contains(_lastClicked.Id))
                                            {
                                                _world.DeselectEntity(_lastClicked);
                                                // We don't have localId for deselect here easily, but LibreMetaverse handles it
                                                _lastClicked = null;
                                            }

                                            // MVP2-1: a plain left-click executes the object's ClickAction,
                                            // same as the real viewer -- SL's ClickAction.Sit (byte 1) means
                                            // sit down instead of touch, so a sit-target prim (chair, vehicle
                                            // seat) doesn't fire a touch script it may not even have.
                                            // Everything else still defaults to "touch" (grab/de-grab pair),
                                            // which is what fires touch_start/touch_end on the object's
                                            // script (e.g. a vendor menu that calls llDialog, M5-4). Right-
                                            // click still owns selection/Edit via the context menu below;
                                            // this was previously a dead end that only deselected and never
                                            // touched at all.
                                            var prim = entity.GetComponent<PrimitiveComponent>();
                                            if (prim != null && prim.ClickAction == 1)
                                            {
                                                GD.Print($"[Sit] sitting on entity {guid:N} (LocalId {localId})");
                                                _session.RequestSit(localId);
                                            }
                                            else
                                            {
                                                GD.Print($"[Touch] touched entity {guid:N} (LocalId {localId})");
                                                _ = _session.ClickObjectAsync(localId);
                                            }
                                            return;
                                        }

                                        // Replace the previous plain-click highlight -- but never
                                        // an entity pinned by its own open Edit window; that stays
                                        // selected independently until the window itself closes.
                                        if (_lastClicked != null && _lastClicked.Id != entity.Id
                                            && !_pinnedEntityIds.Contains(_lastClicked.Id))
                                        {
                                            _world.DeselectEntity(_lastClicked);
                                        }

                                        _world.SelectEntity(entity);
                                        _session.SelectObject(localId);
                                        _lastClicked = entity;

                                        if (mouseBtn.ButtonIndex == MouseButton.Right)
                                        {
                                            _contextMenu.ShowMenu(mouseBtn.Position, entity, localId);
                                            GetViewport().SetInputAsHandled();
                                        }
                                        
                                        return;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // Clicked on nothing: deselect only what this controller last clicked --
                        // other objects pinned open in their own ObjectEditWindow are untouched.
                        if (_lastClicked != null && mouseBtn.ButtonIndex == MouseButton.Left)
                        {
                            if (!_pinnedEntityIds.Contains(_lastClicked.Id))
                            {
                                _world.DeselectEntity(_lastClicked);
                            }
                            _lastClicked = null;
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
