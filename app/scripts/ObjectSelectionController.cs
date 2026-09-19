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

        /// <summary>FEAT-UI-04: the in-world move handles. Owned here rather than by the edit
        /// window, because the gizmo has to see the raw click BEFORE the raycast does -- a drag
        /// on an arrow must not also select whatever is behind it.</summary>
        private UI.SelectionGizmo3D? _gizmo;

        public void AttachGizmo(UI.SelectionGizmo3D gizmo) => _gizmo = gizmo;

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

        // FEAT-UI-05: the objects gathered for a link, in the order they were picked. The LAST
        // one becomes the linkset's root -- the viewer's rule, and it keeps its position and
        // rotation while everything else becomes an offset from it. A List and not a HashSet
        // precisely because that order is load-bearing.
        private readonly System.Collections.Generic.List<System.Guid> _linkSelection = new();

        /// <summary>The link selection, oldest first; the last entry is the prospective root.</summary>
        public System.Collections.Generic.IReadOnlyList<System.Guid> LinkSelection => _linkSelection;

        /// <summary>Raised whenever <see cref="LinkSelection"/> changes, so the open edit windows
        /// can re-evaluate whether Link/Unlink are available.</summary>
        public System.Action? OnLinkSelectionChanged;

        public void Pin(System.Guid entityId)
        {
            _pinnedEntityIds.Add(entityId);
            // An object open for editing is part of the link selection by definition -- that is
            // how the viewer behaves, and it means Link works after one shift-click rather than
            // needing the first object picked twice.
            if (!_linkSelection.Contains(entityId)) _linkSelection.Add(entityId);
            OnLinkSelectionChanged?.Invoke();
        }

        public void Unpin(System.Guid entityId)
        {
            _pinnedEntityIds.Remove(entityId);
            _linkSelection.Remove(entityId);
            OnLinkSelectionChanged?.Invoke();
        }

        /// <summary>Shift-click: add the object to the link selection, or drop it if it was
        /// already there. Re-adding moves it to the end, which makes it the root.</summary>
        private void ToggleLinkSelection(Entity entity)
        {
            if (_world == null) return;

            if (_linkSelection.Remove(entity.Id))
            {
                // Never strip the highlight off something that still has its own edit window
                // open; that window would hide itself.
                if (!_pinnedEntityIds.Contains(entity.Id)) _world.DeselectEntity(entity);
            }
            else
            {
                _linkSelection.Add(entity.Id);
                _world.SelectEntity(entity);
                _session.SelectObject(entity.LocalId);
            }
            OnLinkSelectionChanged?.Invoke();
        }

        /// <summary>Drops everything from the link selection except the objects that have their
        /// own edit window open -- what a plain, unmodified click means.</summary>
        private void ResetLinkSelectionToPinned()
        {
            if (_world == null) return;
            for (int i = _linkSelection.Count - 1; i >= 0; i--)
            {
                var id = _linkSelection[i];
                if (_pinnedEntityIds.Contains(id)) continue;
                var entity = _world.GetEntity(id);
                if (entity != null) _world.DeselectEntity(entity);
                _linkSelection.RemoveAt(i);
            }
            OnLinkSelectionChanged?.Invoke();
        }

        /// <summary>FEAT-UI-06: the user left-clicked a prim of a linkset that is already open for
        /// editing, and wants to edit THAT prim. Arguments are the entity the open window is
        /// currently showing, and the entity/localId it should show instead. Boot re-targets the
        /// window; the controller does not know windows exist.</summary>
        public System.Action<System.Guid, Entity, uint>? OnEditTargetPicked;

        /// <summary>The root prim's local id -- the object itself when it is not linked.</summary>
        private static uint RootLocalIdOf(Entity entity)
        {
            var transform = entity.GetComponent<TransformComponent>();
            return transform != null && transform.ParentLocalId != 0 ? transform.ParentLocalId : entity.LocalId;
        }

        /// <summary>Is the clicked prim part of a linkset that already has an edit window open,
        /// and if so, which entity is that window showing?</summary>
        private bool TryFindOpenEditFor(Entity clicked, out System.Guid editedEntityId)
        {
            editedEntityId = System.Guid.Empty;
            if (_world == null || _pinnedEntityIds.Count == 0) return false;

            uint clickedRoot = RootLocalIdOf(clicked);
            foreach (var pinnedId in _pinnedEntityIds)
            {
                var pinned = _world.GetEntity(pinnedId);
                if (pinned == null || pinned.RegionHandle != clicked.RegionHandle) continue;
                if (RootLocalIdOf(pinned) != clickedRoot) continue;
                editedEntityId = pinnedId;
                return true;
            }
            return false;
        }

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

            // FEAT-UI-04: the gizmo gets first refusal on the mouse, and once it has a drag it
            // keeps every event until release -- otherwise a drag that wanders off the arrow
            // would fall through and start selecting things mid-move.
            if (_gizmo != null)
            {
                if (_gizmo.IsDragging)
                {
                    switch (@event)
                    {
                        case InputEventMouseMotion drag:
                            _gizmo.UpdateDrag(drag.Position);
                            GetViewport().SetInputAsHandled();
                            return;
                        case InputEventMouseButton up when !up.Pressed && up.ButtonIndex == MouseButton.Left:
                            _gizmo.EndDrag();
                            GetViewport().SetInputAsHandled();
                            return;
                    }
                }
                else if (@event is InputEventMouseMotion hover)
                {
                    _gizmo.SetHover(hover.Position);
                }
                else if (@event is InputEventMouseButton down && down.Pressed
                         && down.ButtonIndex == MouseButton.Left && !down.AltPressed
                         && GetViewport().GuiGetHoveredControl() == null
                         && _gizmo.TryBeginDrag(down.Position))
                {
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }

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
                    var exclude = new Godot.Collections.Array<Rid>();
                    var result = RaycastFromMouse(mouseBtn.Position, exclude);

                    // Avatar penetration logic:
                    // 1. Left-click always penetrates ALL avatars (avatars have no left-click touch/sit actions).
                    // 2. Right-click on the LOCAL avatar penetrates if there is an interactive object (prim/mesh)
                    //    or a remote avatar behind it, because in 3rd person the player's own avatar frequently
                    //    occludes chairs, benches, pose stands, or tables the player wants to right-click.
                    while (result.Count > 0 && result.ContainsKey("collider"))
                    {
                        var col = result["collider"].As<Node>();
                        if (col is StaticBody3D sb && sb.HasMeta("LocalId") && sb.GetMeta("LocalId").AsString() == "Avatar")
                        {
                            if (mouseBtn.ButtonIndex == MouseButton.Left)
                            {
                                exclude.Add(sb.GetRid());
                                result = RaycastFromMouse(mouseBtn.Position, exclude);
                                continue;
                            }

                            if (mouseBtn.ButtonIndex == MouseButton.Right && sb.HasMeta("EntityId"))
                            {
                                if (System.Guid.TryParse(sb.GetMeta("EntityId").AsString(), out var avGuid))
                                {
                                    var avEntity = _world.GetEntity(avGuid);
                                    if (avEntity?.GetComponent<AvatarComponent>()?.IsLocalAgent == true)
                                    {
                                        // It's the local avatar: peek behind to see if an object or remote avatar is occluded
                                        var peekExclude = new Godot.Collections.Array<Rid>(exclude) { sb.GetRid() };
                                        var peekResult = RaycastFromMouse(mouseBtn.Position, peekExclude);
                                        if (peekResult.Count > 0 && peekResult.ContainsKey("collider"))
                                        {
                                            var peekCol = peekResult["collider"].As<Node>();
                                            bool peekIsObject = peekCol is StaticBody3D tagged && tagged.HasMeta("LocalId")
                                                && uint.TryParse(tagged.GetMeta("LocalId").AsString(), out _);
                                            bool peekIsAvatar = peekCol is StaticBody3D peekAv && peekAv.HasMeta("LocalId")
                                                && peekAv.GetMeta("LocalId").AsString() == "Avatar";

                                            if (peekIsObject || peekIsAvatar)
                                            {
                                                exclude.Add(sb.GetRid());
                                                result = peekResult;
                                                continue;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        break;
                    }

                    if (result.Count > 0)
                    {
                        var collider = result["collider"].As<Node>();

                        // FEAT-UI-13: avatars carry a "LocalId" meta of the literal string
                        // "Avatar" (see AvatarRenderer.CreateVisual) and an "EntityId" meta.
                        // Right-clicking one offers Profile / IM instead of the object menu; a
                        // left-click on an avatar is left alone (no touch/sit target).
                        if (collider is StaticBody3D avatarBody && avatarBody.HasMeta("LocalId")
                            && avatarBody.GetMeta("LocalId").AsString() == "Avatar"
                            && avatarBody.HasMeta("EntityId")
                            && mouseBtn.ButtonIndex == MouseButton.Right)
                        {
                            if (System.Guid.TryParse(avatarBody.GetMeta("EntityId").AsString(), out var avGuid))
                            {
                                var avEntity = _world.GetEntity(avGuid);
                                var avComp = avEntity?.GetComponent<AvatarComponent>();
                                if (avComp != null)
                                {
                                    string name = $"{avComp.FirstName} {avComp.LastName}".Trim();
                                    if (!string.IsNullOrEmpty(avComp.DisplayName)) name = avComp.DisplayName;
                                    bool isSelf = avComp.IsLocalAgent;
                                    _contextMenu.ShowAvatarMenu(mouseBtn.Position, avComp.AgentId, name, isSelf, _session.IsAvatarMuted(avComp.AgentId));
                                    GetViewport().SetInputAsHandled();
                                }
                            }
                            return;
                        }

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
                            if (uint.TryParse(localIdStr, out uint rawLocalId))
                            {
                                var entityIdStr = staticBody.GetMeta("EntityId").AsString();
                                
                                if (System.Guid.TryParse(entityIdStr, out var guid))
                                {
                                    var rawEntity = _world.GetEntity(guid);
                                    if (rawEntity != null)
                                    {
                                        // Texture-placement diagnostic (FEAT-RENDER-01 Phase 2).
                                        // Deliberately here, on the RAW hit, and before both the
                                        // root-walk below and the left-click touch/sit branch that
                                        // returns early. Hanging it off selection instead was
                                        // useless twice over: a left-click never selects at all,
                                        // and with Edit Linked Parts off the selection resolves to
                                        // the linkset ROOT, so it would have reported some other
                                        // prim's faces than the one actually clicked.
                                        ObjectRenderer.LogFaceTextureParams(rawEntity);

                                        // Edit Linked Parts OFF (default): resolve up to the
                                        // linkset's root, matching pre-existing behavior. ON:
                                        // leave the specifically-clicked part as-is (FEAT-UI-06).
                                        var entity = rawEntity;
                                        uint localId = rawLocalId;
                                        if (!SelectionSettings.EditLinkedParts)
                                        {
                                            var transform = rawEntity.GetComponent<TransformComponent>();
                                            if (transform != null && transform.ParentLocalId != 0)
                                            {
                                                var parent = _world.GetEntity(rawEntity.RegionHandle, transform.ParentLocalId);
                                                if (parent != null)
                                                {
                                                    entity = parent;
                                                    localId = transform.ParentLocalId;
                                                }
                                            }
                                        }

                                        if (mouseBtn.ButtonIndex == MouseButton.Left)
                                        {
                                            // FEAT-UI-06: inside an open edit session, a left click
                                            // on the object being edited PICKS a part instead of
                                            // running its click action -- the reference viewer does
                                            // the same, and it is how you reach a child prim once
                                            // "Edit linked parts" is on. Deliberately scoped to the
                                            // linkset that is already open: clicking any other
                                            // object still touches or sits on it, because an open
                                            // build window should not swallow the whole world's
                                            // left click.
                                            // FEAT-UI-05: shift-click gathers objects for a link.
                                            // Only inside an edit session, like the part picking
                                            // below and for the same reason -- outside one, a
                                            // shift-click is just a click on the world.
                                            if (_pinnedEntityIds.Count > 0 && mouseBtn.ShiftPressed)
                                            {
                                                ToggleLinkSelection(entity);
                                                GetViewport().SetInputAsHandled();
                                                return;
                                            }

                                            if (TryFindOpenEditFor(rawEntity, out var editedEntityId))
                                            {
                                                ResetLinkSelectionToPinned();
                                                if (editedEntityId != entity.Id)
                                                {
                                                    OnEditTargetPicked?.Invoke(editedEntityId, entity, localId);
                                                }
                                                GetViewport().SetInputAsHandled();
                                                return;
                                            }

                                            if (_lastClicked != null && !_pinnedEntityIds.Contains(_lastClicked.Id)
                                                && !_linkSelection.Contains(_lastClicked.Id))
                                            {
                                                _world.DeselectEntity(_lastClicked);
                                                _lastClicked = null;
                                                _gizmo?.Detach();
                                            }

                                            // MVP2-1: a plain left-click executes the object's ClickAction (Sit vs. Touch).
                                            // Parity rule: if the avatar is ALREADY sitting on this object / linkset,
                                            // clicking it is ALWAYS a Touch, never Sit -- you cannot sit on what you're
                                            // already seated on (e.g. pose stand pose switching).
                                            uint sittingOn = _session.SittingOnLocalId;
                                            bool isSittingOnThisObject = sittingOn != 0 &&
                                                (sittingOn == rawLocalId || sittingOn == localId ||
                                                 rawEntity.GetComponent<TransformComponent>()?.ParentLocalId == sittingOn ||
                                                 entity.GetComponent<TransformComponent>()?.ParentLocalId == sittingOn);

                                            var rawPrim = rawEntity.GetComponent<PrimitiveComponent>();
                                            var rootPrim = entity != rawEntity ? entity.GetComponent<PrimitiveComponent>() : null;
                                            byte clickAction = (rawPrim != null && rawPrim.ClickAction != 0) ? rawPrim.ClickAction : (rootPrim?.ClickAction ?? 0);
                                            bool wantSit = clickAction == 1 && !isSittingOnThisObject;

                                            if (wantSit)
                                            {
                                                _session.RequestSit(rawLocalId);
                                            }
                                            else
                                            {
                                                var hitPosGodot = result.ContainsKey("position") ? result["position"].AsVector3() : Vector3.Zero;
                                                var hitPosSl = RenderConfig.FromGodot(rawEntity.RegionHandle, hitPosGodot);
                                                _ = _session.ClickObjectAsync(rawLocalId, position: hitPosSl);
                                            }
                                            return;
                                        }

                                        // Only select visually if we are ALREADY in Edit Mode (i.e. at least one ObjectEditWindow is open)
                                        if (_pinnedEntityIds.Count > 0)
                                        {
                                            // Replace the previous plain-click highlight -- but never
                                            // an entity pinned by its own open Edit window; that stays
                                            // selected independently until the window itself closes.
                                            if (_lastClicked != null && _lastClicked.Id != entity.Id
                                                && !_pinnedEntityIds.Contains(_lastClicked.Id))
                                            {
                                                _world.DeselectEntity(_lastClicked);
                                            }

                                            _world.SelectEntity(entity);
                                            // FEAT-UI-04: Attach decides for itself whether the
                                            // agent may move this one, and hides otherwise.
                                            _gizmo?.Attach(entity);
                                            _session.SelectObject(localId);
                                            _lastClicked = entity;
                                        }

                                        _contextMenu.ShowMenu(mouseBtn.Position, entity, localId);
                                        GetViewport().SetInputAsHandled();
                                        
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

        private Godot.Collections.Dictionary RaycastFromMouse(Vector2 mousePos, Godot.Collections.Array<Rid>? exclude = null)
        {
            var spaceState = _camera.GetWorld3D().DirectSpaceState;
            var from = _camera.ProjectRayOrigin(mousePos);
            var to = from + _camera.ProjectRayNormal(mousePos) * 1000f;

            var query = PhysicsRayQueryParameters3D.Create(from, to);
            if (exclude != null && exclude.Count > 0)
            {
                query.Exclude = exclude;
            }
            // No mask set -- deliberately hits every layer, INCLUDING PhysicsLayers.Terrain: the
            // right-click "ground menu" (ShowGroundMenu above) depends on hitting terrain to get
            // a world position. Don't narrow this to PhysicsLayers.Objects the way CursorManager
            // does -- that would silently break right-click-to-create-on-ground.

            return spaceState.IntersectRay(query);
        }
    }
}
