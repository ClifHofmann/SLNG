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

        // FEAT-UI-05: ONE edit session, with ONE ordered selection. That is the reference
        // viewer's model -- LLSelectMgr holds a single selection and the build floater shows
        // whatever is in it (lltoolselect.cpp: an unmodified click calls deselectAll() and then
        // selects what was hit; shift or ctrl toggles instead). SLNG used to open an independent
        // window per object and keep a set of "pinned" entities beside the click highlight, so
        // clicking back and forth between objects behaved differently depending on which of them
        // happened to own a window.
        private bool _editSessionOpen;

        // SL's click actions, in the simulator's own numbering (indra_constants.h, marked "DO NOT
        // CHANGE THE SEQUENCE OF THIS LIST"). Only the ones this viewer acts on are named; Open,
        // Play, OpenMedia and Zoom fall through to a touch, which is what an unhandled action did
        // before and is harmless.
        private const byte ClickActionSit = 1;
        private const byte ClickActionBuy = 2;
        private const byte ClickActionPay = 3;
        private const byte ClickActionDisabled = 8;
        private const byte ClickActionIgnore = 9;

        /// <summary>FEAT-ECON-02: a left click asked to pay or buy this object -- Boot owns the
        /// two dialogs, and the context menu reaches them through the same pair.</summary>
        public System.Action<Entity, uint>? OnPayRequested;
        public System.Action<Entity, uint>? OnBuyRequested;

        /// <summary>The other prims of an object's linkset, supplied by Boot -- only
        /// WorldSimulation keeps the parent index.</summary>
        public System.Func<Entity, System.Collections.Generic.IReadOnlyList<Entity>>? LinksetParts;

        /// <summary>Tells the simulator what is selected: the whole object, or one prim of it
        /// when "edit linked parts" is on.</summary>
        /// <remarks>
        /// Viewer parity, and load-bearing rather than cosmetic. LLSelectMgr::selectObjectAndFamily
        /// sends ONE ObjectSelect naming every prim of the linkset; the simulator keeps that
        /// selection per agent and a linked-set edit acts on it. Selecting only the root made a
        /// resize change the root prim alone -- reported in-world as "beim Großziehen wird das
        /// non-root Prim nicht größer".
        /// </remarks>
        private void SelectFamily(Entity entity, uint localId)
        {
            if (SelectionSettings.EditLinkedParts)
            {
                _session.SelectObject(localId);
                return;
            }

            var parts = LinksetParts?.Invoke(entity);
            if (parts == null || parts.Count == 0)
            {
                _session.SelectObject(localId);
                return;
            }

            var ids = new System.Collections.Generic.List<uint>(parts.Count + 1) { localId };
            foreach (var part in parts)
            {
                if (part.LocalId != localId) ids.Add(part.LocalId);
            }
            _session.SelectObjects(ids);
        }

        /// <summary>Is this collider one of the LOCAL agent's worn items? Only those carry one at
        /// all (see AvatarRenderer.AddAttachmentPickBody), so the entity lookup is what decides.</summary>
        private bool IsOwnAttachmentBody(StaticBody3D body)
        {
            if (_world == null || !body.HasMeta("EntityId")) return false;
            if (!System.Guid.TryParse(body.GetMeta("EntityId").AsString(), out var id)) return false;
            return _world.GetEntity(id)?.GetComponent<AttachmentComponent>() != null;
        }

        // The selection, oldest first. The LAST entry is the primary: it is what the edit window
        // shows, and on a link it becomes the linkset's root -- keeping its own position while
        // everything else turns into an offset from it. A List and not a HashSet precisely
        // because that order is load-bearing.
        private readonly System.Collections.Generic.List<System.Guid> _selection = new();

        /// <summary>The selection, oldest first; the last entry is the primary.</summary>
        public System.Collections.Generic.IReadOnlyList<System.Guid> LinkSelection => _selection;

        /// <summary>Raised whenever the selection changes -- for the Link / Unlink buttons, and
        /// so the edit window can close once nothing is selected any more.</summary>
        public System.Action? OnLinkSelectionChanged;

        /// <summary>Raised when a different object becomes the primary, so the one edit window
        /// can follow it.</summary>
        public System.Action<Entity, uint>? OnPrimarySelectionChanged;

        /// <summary>Boot has opened the edit window on this object. From here until
        /// <see cref="EndEditSession"/> a left click picks objects instead of touching them,
        /// which is what build mode means in the reference viewer.</summary>
        public void BeginEditSession(Entity entity, uint localId)
        {
            _editSessionOpen = true;
            SelectOnly(entity, localId);
        }

        /// <summary>The edit window closed: nothing stays selected, and a click means what it
        /// means outside build mode again.</summary>
        public void EndEditSession()
        {
            _editSessionOpen = false;
            DeselectAll();
        }

        private void DeselectAll()
        {
            if (_world == null) return;
            foreach (var id in _selection)
            {
                var entity = _world.GetEntity(id);
                if (entity != null) _world.DeselectEntity(entity);
            }
            _selection.Clear();
            _lastClicked = null;
            _gizmo?.Detach();
            OnLinkSelectionChanged?.Invoke();
        }

        /// <summary>A plain click: this object and nothing else. Mirrors the reference viewer,
        /// where an unmodified click is deselectAll() followed by selecting what was hit.</summary>
        private void SelectOnly(Entity entity, uint localId)
        {
            if (_world == null) return;

            for (int i = _selection.Count - 1; i >= 0; i--)
            {
                if (_selection[i] == entity.Id) continue;
                var previous = _world.GetEntity(_selection[i]);
                if (previous != null) _world.DeselectEntity(previous);
                _selection.RemoveAt(i);
            }
            if (_selection.Count == 0) _selection.Add(entity.Id);

            _world.SelectEntity(entity);
            SelectFamily(entity, localId);
            _gizmo?.Attach(entity);
            _lastClicked = entity;

            OnPrimarySelectionChanged?.Invoke(entity, localId);
            OnLinkSelectionChanged?.Invoke();
        }

        /// <summary>Shift-click: add the object, or drop it again if it was already in.
        /// Re-adding moves it to the end, which makes it the primary -- and therefore the root
        /// of a link.</summary>
        private void ToggleSelection(Entity entity, uint localId)
        {
            if (_world == null) return;

            if (_selection.Remove(entity.Id))
            {
                _world.DeselectEntity(entity);
                // Something else has to show in the window now; the newest survivor takes over.
                if (_selection.Count > 0)
                {
                    var primary = _world.GetEntity(_selection[_selection.Count - 1]);
                    if (primary != null)
                    {
                        _gizmo?.Attach(primary);
                        _lastClicked = primary;
                        OnPrimarySelectionChanged?.Invoke(primary, primary.LocalId);
                    }
                }
                else
                {
                    _gizmo?.Detach();
                    _lastClicked = null;
                }
            }
            else
            {
                _selection.Add(entity.Id);
                _world.SelectEntity(entity);
                SelectFamily(entity, localId);
                _gizmo?.Attach(entity);
                _lastClicked = entity;
                OnPrimarySelectionChanged?.Invoke(entity, localId);
            }
            OnLinkSelectionChanged?.Invoke();
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

                        // FEAT-UI-23: your own worn items are pickable now, and in third person
                        // they sit between the camera and everything else -- prim hair over the
                        // head is directly in the way of most of the screen. Left through, they
                        // swallow every click meant for the world, which is what "ich kann meine
                        // Objekte nicht auswählen" was.
                        //
                        // So a LEFT click gets the same treatment the local avatar already gets:
                        // peek behind, and if there is anything else there, use that instead --
                        // a worn item has no touch or sit action of its own, so nothing is lost.
                        //
                        // A RIGHT click does not peek. It is the gesture that means "this one",
                        // and it is how the reference viewer opens an attachment's own menu
                        // (Edit / Detach). Peeking would hand that menu to whatever the avatar
                        // happens to stand in front of -- the ground, if nothing else -- so a
                        // worn item could only ever be right-clicked against the sky, and the
                        // right-click highlight landed on the ground instead of the shirt.
                        //
                        // Nor does a left click inside an edit session, where it means "pick
                        // this" rather than "touch this" -- that is how a part of a worn linkset
                        // gets selected with "edit linked parts" on.
                        if (col is StaticBody3D wornBody && IsOwnAttachmentBody(wornBody))
                        {
                            if (mouseBtn.ButtonIndex == MouseButton.Right || _editSessionOpen) break;

                            var behind = new Godot.Collections.Array<Rid>(exclude) { wornBody.GetRid() };
                            var behindResult = RaycastFromMouse(mouseBtn.Position, behind);
                            if (behindResult.Count > 0 && behindResult.ContainsKey("collider"))
                            {
                                exclude.Add(wornBody.GetRid());
                                result = behindResult;
                                continue;
                            }
                            break;
                        }

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
                            _contextMenu.ShowGroundMenu(mouseBtn.Position, RezPointFrom(result));
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
                                                // An AVATAR is not a link root. A worn item's ParentLocalId is the avatar that wears it, so walking up would select the avatar instead of the item (FEAT-UI-23). WorldSimulation.ResolveWorldTransform draws the same line.
                                                if (parent != null && parent.GetComponent<AvatarComponent>() == null)
                                                {
                                                    entity = parent;
                                                    localId = transform.ParentLocalId;
                                                }
                                            }
                                        }

                                        if (mouseBtn.ButtonIndex == MouseButton.Left)
                                        {
                                            // FEAT-UI-05/06: while the edit window is open, a left
                                            // click PICKS -- it does not touch or sit. That is
                                            // build mode in the reference viewer: an unmodified
                                            // click replaces the selection with what was hit
                                            // (lltoolselect.cpp calls deselectAll() first), and
                                            // shift toggles that object in or out instead. Which
                                            // prim gets picked, the whole linkset or one part of
                                            // it, was already decided above by "edit linked
                                            // parts".
                                            if (_editSessionOpen)
                                            {
                                                if (mouseBtn.ShiftPressed) ToggleSelection(entity, localId);
                                                else SelectOnly(entity, localId);
                                                GetViewport().SetInputAsHandled();
                                                return;
                                            }

                                            if (_lastClicked != null)
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
                                            bool wantSit = clickAction == ClickActionSit && !isSittingOnThisObject;

                                            // FEAT-ECON-02: an object can say what a LEFT click on
                                            // it means, and Pay and Buy are two of the answers --
                                            // which is most of what a vendor is. SLNG knew only Sit
                                            // and Touch, so clicking a vendor sent a touch its
                                            // script ignores: "ich klicke drauf und es passiert
                                            // nichts". The reference viewer dispatches the same
                                            // way (LLToolPie::handleLeftClickPick), and gates each
                                            // on the object actually being able to do it.
                                            if (clickAction == ClickActionPay
                                                && (rawPrim?.TakesMoney == true || rootPrim?.TakesMoney == true))
                                            {
                                                // "pay event goes to object actually clicked on"
                                                OnPayRequested?.Invoke(rawEntity, rawLocalId);
                                                return;
                                            }

                                            if (clickAction == ClickActionBuy)
                                            {
                                                // Buying is about the OBJECT, so the root prim --
                                                // the sale price lives there, not on the face you
                                                // happened to hit.
                                                var meta = entity.GetComponent<MetadataComponent>();
                                                if (meta != null && meta.SaleType != PrimSaleType.NotForSale)
                                                {
                                                    OnBuyRequested?.Invoke(entity, localId);
                                                    return;
                                                }
                                            }

                                            // "Disabled" and "Ignore" mean exactly that: the object
                                            // has asked not to be clicked, and sending a touch
                                            // anyway would be answering a question nobody asked.
                                            if (clickAction is ClickActionDisabled or ClickActionIgnore) return;

                                            // One line per click ON AN OBJECT, naming what the
                                            // object asked for and what was done with it. "Ich
                                            // klicke und nichts passiert" has three different
                                            // causes -- the ray missed, the click action was
                                            // something we ignore, or the touch went out and the
                                            // script stayed silent -- and they are indistinguishable
                                            // from the outside.
                                            Logger.Info($"[Click] left on {rawLocalId} (root {localId}): " +
                                                        $"action={clickAction} takesMoney={rawPrim?.TakesMoney == true || rootPrim?.TakesMoney == true} " +
                                                        $"sale={entity.GetComponent<MetadataComponent>()?.SaleType} " +
                                                        $"-> {(wantSit ? "sit" : "touch")}");

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

                                        // A right-click marks what it is about to act on, in or
                                        // out of an edit session -- asked for in-world, and the
                                        // reference viewer highlights its pie menu's target the
                                        // same way. Outside a session that is the plain click
                                        // highlight, which the next click elsewhere clears.
                                        if (_editSessionOpen)
                                        {
                                            SelectOnly(entity, localId);
                                        }
                                        else
                                        {
                                            if (_lastClicked != null && _lastClicked.Id != entity.Id)
                                            {
                                                _world.DeselectEntity(_lastClicked);
                                            }
                                            _world.SelectEntity(entity);
                                            SelectFamily(entity, localId);
                                            _lastClicked = entity;
                                        }

                                        _contextMenu.ShowMenu(mouseBtn.Position, entity, localId,
                                            RezPointFrom(result));
                                        GetViewport().SetInputAsHandled();
                                        
                                        return;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // A right-click that resolves to nothing at all is the one case where the
                        // viewer looks broken rather than merely unhelpful: no menu appears, and
                        // there is nothing on screen to say why. It means the ray met no collider
                        // -- open sky, or an object whose collision shape has not been built yet.
                        // One line, only for the right button, so "nichts passiert" is answerable
                        // afterwards.
                        if (mouseBtn.ButtonIndex == MouseButton.Right)
                        {
                            Logger.Warn($"[Pick] right-click at {mouseBtn.Position} hit nothing -- no collider under the cursor");
                        }

                        // Clicked on nothing. In an edit session that clears the WHOLE selection,
                        // the way deselectAll() does in the reference viewer -- and Boot closes
                        // the window behind it, because a build floater with an empty selection
                        // has nothing to show. Outside a session there is only ever the one
                        // click highlight to drop.
                        if (mouseBtn.ButtonIndex == MouseButton.Left && !mouseBtn.ShiftPressed)
                        {
                            if (_editSessionOpen)
                            {
                                DeselectAll();
                            }
                            else if (_lastClicked != null)
                            {
                                _world.DeselectEntity(_lastClicked);
                                _lastClicked = null;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Where a prim rezzed from a right-click should sit: the point that was hit,
        /// lifted clear of the surface along its normal.</summary>
        /// <remarks>
        /// MVP4-1. LibreMetaverse's AddPrim hardcodes <c>BypassRaycast = 1</c> with RayStart and
        /// RayEnd both set to the position we pass, so the simulator puts the prim's CENTRE
        /// exactly there and a new prim would be half buried in whatever it was rezzed onto.
        /// The reference viewer instead leaves the ray to the simulator, which rests the prim on
        /// the surface; lifting by half the new prim's height gets to the same place without
        /// needing a packet LibreMetaverse does not expose.
        /// </remarks>
        private static Vector3 RezPointFrom(Godot.Collections.Dictionary hit)
        {
            var point = hit.ContainsKey("position") ? hit["position"].AsVector3() : Vector3.Zero;
            var normal = hit.ContainsKey("normal") ? hit["normal"].AsVector3() : Vector3.Up;
            if (normal.LengthSquared() < 0.0001f) normal = Vector3.Up;
            return point + normal.Normalized() * (NewPrimSize * 0.5f);
        }

        /// <summary>The edge length GridSession.CreatePrim rezzes with, SL's default half-metre
        /// cube. Kept in step by hand; a wrong value here only offsets the drop point.</summary>
        private const float NewPrimSize = 0.5f;

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
