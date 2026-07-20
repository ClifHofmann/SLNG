using Godot;
using SLNG.Core;
using SLNG.Core.ECS;
using SLNG.Core.Components;
using SLNG.Net;

namespace SLNG.App.UI
{
    public partial class ObjectEditWindow : SLNGWindow
    {
        /// <summary>Fired once this window has closed and freed itself, so an owner tracking
        /// one window per edited object (Boot.cs) knows to drop its reference.</summary>
        public System.Action? Closed;

        private GridSession? _session;
        private World? _world;
        private Entity? _currentEntity;
        private uint _currentLocalId;

        // Tracked separately from the label text so a NameResolved reply arriving after the
        // user has already switched objects (or after Creator/Owner/Group changed again) can be
        // matched to the field it belongs to instead of stomping whatever is showing now.
        private System.Guid _currentCreatorId, _currentOwnerId, _currentGroupId;

        // The window's own belief about Physical/Temporary/Phantom, independent of
        // PrimitiveComponent -- see SendObjectFlags/UpdatePrimStateUI for why: the shared
        // component gets overwritten by every incoming ObjectUpdate, including ones that still
        // carry pre-change flags (the simulator's ObjectFlagUpdate hasn't caught up yet), so
        // reading it back as the "unspecified" baseline when toggling a different checkbox can
        // silently resend a just-changed flag's OLD value.
        private bool _knownPhysical, _knownTemporary, _knownPhantom;

        // Set when a flag was just changed locally; a live update contradicting a still-pending
        // flag is ignored (dirty-check) instead of snapping the checkbox back before the
        // simulator's echo has caught up. Cleared only once an incoming update actually confirms
        // it -- no timeout: a fixed grace window (previously 3s) raced with how long OpenSim
        // can actually take to broadcast a full update with the post-change flags, so a late,
        // still-stale full update would "win" once the window lapsed and revert a change that
        // had genuinely succeeded server-side. Genuine rejections aren't silent anymore either
        // (see GridSession.AlertMessageReceived), so there's no need to time out and trust
        // whatever shows up next.
        private bool? _pendingPhysical, _pendingTemporary, _pendingPhantom;

        // See ApplyTransform: true while this window is firing its own optimistic
        // NotifyComponentUpdated calls, so OnComponentUpdated (below) can tell "this is my own
        // write echoing back" apart from a genuine incoming update from the simulator.
        private bool _suppressOwnComponentNotify;

        private LineEdit _posX = null!, _posY = null!, _posZ = null!;
        private LineEdit _rotX = null!, _rotY = null!, _rotZ = null!;
        private LineEdit _scaleX = null!, _scaleY = null!, _scaleZ = null!;

        private LineEdit _nameInput = null!, _descInput = null!;
        private Label _creatorLabel = null!, _ownerLabel = null!, _groupLabel = null!, _isOwnerLabel = null!;
        private CheckBox _lockedCheck = null!, _physicalCheck = null!, _tempCheck = null!, _phantomCheck = null!;
        private CheckBox _permModifyCheck = null!, _permCopyCheck = null!, _permTransferCheck = null!, _permMoveCheck = null!;

        public void Initialize(GridSession session, World world)
        {
            if (_session != null) _session.NameResolved -= OnNameResolved;
            _session = session;
            _session.NameResolved += OnNameResolved;
            if (_world != null)
            {
                _world.ComponentUpdated -= OnComponentUpdated;
                _world.EntityDeselected -= OnEntityDeselected;
            }
            _world = world;
            _world.ComponentUpdated += OnComponentUpdated;
            _world.EntityDeselected += OnEntityDeselected;
        }

        /// <summary>Hides the window when its object stops being the world's selected entity --
        /// e.g. the user clicked a different object, or clicked empty space -- so it doesn't sit
        /// open and stale (and, previously, uncloseable: see <see cref="RequestClose"/>).</summary>
        private void OnEntityDeselected(object? sender, EntityEventArgs e)
        {
            if (_currentEntity != null && e.Entity.Id == _currentEntity.Id) Visible = false;
        }

        public override void _Ready()
        {
            base._Ready(); // set up SLNGWindow styling
            OnCloseRequested = RequestClose;
            Title = "Build / Inspector";
            Visible = false;
            CustomMinimumSize = new Vector2(320, 400);

            var tabContainer = new TabContainer();
            ContentContainer.AddChild(tabContainer);

            // General Tab
            var generalTab = new MarginContainer { Name = "General" };
            var generalVBox = new VBoxContainer();
            generalTab.AddChild(generalVBox);
            tabContainer.AddChild(generalTab);
            
            _nameInput = new LineEdit { PlaceholderText = "Name", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _descInput = new LineEdit { PlaceholderText = "Description", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _creatorLabel = new Label { Text = "Loading..." };
            _ownerLabel = new Label { Text = "Loading..." };
            _groupLabel = new Label { Text = "Loading..." };

            var genGrid = new GridContainer { Columns = 2 };
            genGrid.AddChild(new Label { Text = "Name:" }); genGrid.AddChild(_nameInput);
            genGrid.AddChild(new Label { Text = "Description:" }); genGrid.AddChild(_descInput);
            genGrid.AddChild(new Label { Text = "Creator:" }); genGrid.AddChild(_creatorLabel);
            genGrid.AddChild(new Label { Text = "Owner:" }); genGrid.AddChild(_ownerLabel);
            genGrid.AddChild(new Label { Text = "Group:" }); genGrid.AddChild(_groupLabel);
            generalVBox.AddChild(genGrid);

            // Read-only indicators (Disabled, not user-togglable here): show why an edit might
            // silently fail server-side, e.g. OpenSim's SceneGraph.UpdatePrimFlags requires
            // Modify -- transform edits only need the separate Move permission (the Locked
            // checkbox in the Object tab), so a no-modify object can move but reject flags.
            _permModifyCheck = new CheckBox { Text = "Modify", Disabled = true };
            _permCopyCheck = new CheckBox { Text = "Copy", Disabled = true };
            _permTransferCheck = new CheckBox { Text = "Transfer", Disabled = true };
            _permMoveCheck = new CheckBox { Text = "Move", Disabled = true };
            var permHBox = new HBoxContainer();
            permHBox.AddChild(_permModifyCheck);
            permHBox.AddChild(_permCopyCheck);
            permHBox.AddChild(_permTransferCheck);
            permHBox.AddChild(_permMoveCheck);
            _isOwnerLabel = new Label { Text = "Owner Permissions (unknown whether these are yours):" };
            generalVBox.AddChild(_isOwnerLabel);
            generalVBox.AddChild(permHBox);

            // Object (Transform) Tab
            var objectTab = new MarginContainer { Name = "Object" };
            var transformVBox = new VBoxContainer();
            objectTab.AddChild(transformVBox);
            tabContainer.AddChild(objectTab);

            var stateGrid = new GridContainer { Columns = 2 };
            _lockedCheck = new CheckBox { Text = "Locked" };
            _physicalCheck = new CheckBox { Text = "Physical" };
            _tempCheck = new CheckBox { Text = "Temporary" };
            _phantomCheck = new CheckBox { Text = "Phantom" };
            _lockedCheck.Toggled += OnLockedToggled;
            _physicalCheck.Toggled += pressed => SendObjectFlags(physical: pressed);
            _tempCheck.Toggled += pressed => SendObjectFlags(temporary: pressed);
            _phantomCheck.Toggled += pressed => SendObjectFlags(phantom: pressed);
            stateGrid.AddChild(_lockedCheck); stateGrid.AddChild(_physicalCheck);
            stateGrid.AddChild(_tempCheck); stateGrid.AddChild(_phantomCheck);
            transformVBox.AddChild(stateGrid);

            transformVBox.AddChild(new Label { Text = "Position (X, Y, Z)" });
            var posHBox = new HBoxContainer();
            _posX = new LineEdit { CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _posY = new LineEdit { CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _posZ = new LineEdit { CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            posHBox.AddChild(_posX); posHBox.AddChild(_posY); posHBox.AddChild(_posZ);
            transformVBox.AddChild(posHBox);

            transformVBox.AddChild(new Label { Text = "Rotation (Degrees)" });
            var rotHBox = new HBoxContainer();
            _rotX = new LineEdit { CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _rotY = new LineEdit { CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _rotZ = new LineEdit { CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            rotHBox.AddChild(_rotX); rotHBox.AddChild(_rotY); rotHBox.AddChild(_rotZ);
            transformVBox.AddChild(rotHBox);

            transformVBox.AddChild(new Label { Text = "Size (Meters)" });
            var scaleHBox = new HBoxContainer();
            _scaleX = new LineEdit { CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _scaleY = new LineEdit { CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _scaleZ = new LineEdit { CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            scaleHBox.AddChild(_scaleX); scaleHBox.AddChild(_scaleY); scaleHBox.AddChild(_scaleZ);
            transformVBox.AddChild(scaleHBox);

            var applyBtn = new Button { Text = "Apply", CustomMinimumSize = new Vector2(0, 30) };
            applyBtn.Pressed += ApplyTransform;
            transformVBox.AddChild(applyBtn);

            // Features Tab
            var featuresTab = new MarginContainer { Name = "Features" };
            var featVBox = new VBoxContainer();
            featuresTab.AddChild(featVBox);
            tabContainer.AddChild(featuresTab);
            
            featVBox.AddChild(new CheckBox { Text = "Light" });
            var lightGrid = new GridContainer { Columns = 2 };
            lightGrid.AddChild(new Label { Text = "Intensity:" }); lightGrid.AddChild(new LineEdit { CustomMinimumSize = new Vector2(60, 0) });
            lightGrid.AddChild(new Label { Text = "Radius:" }); lightGrid.AddChild(new LineEdit { CustomMinimumSize = new Vector2(60, 0) });
            featVBox.AddChild(lightGrid);

            // Texture Tab
            var textureTab = new MarginContainer { Name = "Texture" };
            var texVBox = new VBoxContainer();
            textureTab.AddChild(texVBox);
            tabContainer.AddChild(textureTab);
            
            var texTabs = new TabContainer();
            texVBox.AddChild(texTabs);
            var pbrTab = new MarginContainer { Name = "PBR" };
            texTabs.AddChild(pbrTab);
            pbrTab.AddChild(new Label { Text = "Base Color, Normal, ORM, Emissive maps...\n(Will be implemented later)" });
            var blinnTab = new MarginContainer { Name = "Blinn-Phong" };
            texTabs.AddChild(blinnTab);
            blinnTab.AddChild(new Label { Text = "Diffuse, Specular maps..." });

            // Content Tab
            var contentTab = new MarginContainer { Name = "Content" };
            contentTab.AddChild(new Label { Text = "Inventory inside object...\n(Loading functionality coming in M6)" });
            tabContainer.AddChild(contentTab);
        }

        public void EditObject(Entity entity, uint localId, World? world)
        {
            var transform = entity.GetComponent<TransformComponent>();
            
            // Always resolve to the Root object of the linkset unless we want to edit a specific part
            if (transform != null && transform.ParentLocalId != 0 && _session != null && world != null)
            {
                var parent = world.GetEntity(_session.CurrentRegionHandle, transform.ParentLocalId);
                if (parent != null)
                {
                    entity = parent;
                    localId = transform.ParentLocalId;
                    transform = entity.GetComponent<TransformComponent>();
                }
            }
            
            // Request properties by the object's real simulator UUID (MetadataComponent.Id,
            // seeded from ObjectUpdateEvent) -- entity.Id is only an internal ECS identity and
            // the sim won't recognize it, so properties (Description, Creator, etc.) never came
            // back when this used entity.Id directly.
            var metaForRequest = entity.GetComponent<MetadataComponent>();
            if (metaForRequest != null && metaForRequest.Id != System.Guid.Empty)
            {
                _session?.RequestObjectProperties(metaForRequest.Id);
            }

            _currentEntity = entity;
            _currentLocalId = localId;

            if (transform != null)
            {
                _posX.Text = transform.Position.X.ToString("F3");
                _posY.Text = transform.Position.Y.ToString("F3");
                _posZ.Text = transform.Position.Z.ToString("F3");

                var q = transform.Rotation;
                var gQuat = new Godot.Quaternion(q.X, q.Y, q.Z, q.W);
                // A freshly-rezzed object's first ObjectUpdate can carry a degenerate all-zero
                // rotation before the sim echoes a real one -- GetEuler() throws hard
                // (InvalidOperationException: "Quaternion is not normalized") on that instead of
                // just returning zero, so guard it explicitly rather than crash opening Edit.
                var eulerDeg = (gQuat.LengthSquared() > 0.0001f ? gQuat.Normalized() : Godot.Quaternion.Identity).GetEuler() * (180f / Mathf.Pi);
                _rotX.Text = eulerDeg.X.ToString("F1");
                _rotY.Text = eulerDeg.Y.ToString("F1");
                _rotZ.Text = eulerDeg.Z.ToString("F1");
            }
            
            // Opening (possibly a different) object drops any pending flag-change state from
            // whatever was previously loaded here.
            _pendingPhysical = _pendingTemporary = _pendingPhantom = null;

            var primitive = entity.GetComponent<PrimitiveComponent>();
            if (primitive != null)
            {
                _scaleX.Text = primitive.Scale.X.ToString("F3");
                _scaleY.Text = primitive.Scale.Y.ToString("F3");
                _scaleZ.Text = primitive.Scale.Z.ToString("F3");
                _knownPhysical = primitive.IsPhysical;
                _knownTemporary = primitive.IsTemporary;
                _knownPhantom = primitive.IsPhantom;
                _physicalCheck.SetPressedNoSignal(primitive.IsPhysical);
                _tempCheck.SetPressedNoSignal(primitive.IsTemporary);
                _phantomCheck.SetPressedNoSignal(primitive.IsPhantom);
            }

            var meta = entity.GetComponent<MetadataComponent>();
            string titleName = "Object";
            if (meta != null)
            {
                _nameInput.Text = meta.Name;
                _descInput.Text = meta.Description;
                _lockedCheck.SetPressedNoSignal(meta.Locked);
                _currentCreatorId = meta.CreatorId;
                _currentOwnerId = meta.OwnerId;
                _currentGroupId = meta.GroupId;
                ResolveNameLabel(_creatorLabel, meta.CreatorId, isGroup: false, emptyPlaceholder: "(unknown)");
                ResolveNameLabel(_ownerLabel, meta.OwnerId, isGroup: false, emptyPlaceholder: "(unknown)");
                ResolveNameLabel(_groupLabel, meta.GroupId, isGroup: true, emptyPlaceholder: "(none)");
                UpdatePermissionCheckboxes(meta.OwnerId, meta.OwnerCanModify, meta.OwnerCanCopy, meta.OwnerCanTransfer, !meta.Locked);
                titleName = string.IsNullOrEmpty(meta.Name) ? "Object" : meta.Name;
            }
            else
            {
                _nameInput.Text = "Object";
                _descInput.Text = "(No Description)";
                _lockedCheck.SetPressedNoSignal(false);
                _currentCreatorId = _currentOwnerId = _currentGroupId = System.Guid.Empty;
                _creatorLabel.Text = "Loading...";
                _ownerLabel.Text = "Loading...";
                _groupLabel.Text = "Loading...";
                UpdatePermissionCheckboxes(System.Guid.Empty, false, false, false, false);
            }

            Title = $"Edit: {titleName}";
            Visible = true;
            MoveToFront();
            CallDeferred(MethodName.CenterWindow);
        }

        private void ApplyTransform()
        {
            if (_currentEntity == null || _session == null) return;

            if (float.TryParse(_posX.Text, out float px) &&
                float.TryParse(_posY.Text, out float py) &&
                float.TryParse(_posZ.Text, out float pz) &&
                float.TryParse(_rotX.Text, out float rx) &&
                float.TryParse(_rotY.Text, out float ry) &&
                float.TryParse(_rotZ.Text, out float rz) &&
                float.TryParse(_scaleX.Text, out float sx) &&
                float.TryParse(_scaleY.Text, out float sy) &&
                float.TryParse(_scaleZ.Text, out float sz))
            {
                var pos = new System.Numerics.Vector3(px, py, pz);
                var scale = new System.Numerics.Vector3(sx, sy, sz);
                
                var eulerRad = new Godot.Vector3(rx, ry, rz) * (Mathf.Pi / 180f);
                var gQuat = Godot.Basis.FromEuler(eulerRad).GetRotationQuaternion();
                var rot = new System.Numerics.Quaternion(gQuat.X, gQuat.Y, gQuat.Z, gQuat.W);

                _session.UpdateObjectTransform(_currentLocalId, pos, rot, scale);

                // Optimistic local update -- must fire NotifyComponentUpdated or nothing renders
                // until an unrelated event happens to force a resync (e.g. a later flag toggle
                // triggering OpenSim's full-object-update broadcast): ObjectRenderer only repaints
                // a MeshInstance's transform/scale in response to this event, not by polling.
                // Suppressed while we fire these ourselves: NotifyComponentUpdated is a plain
                // synchronous event, and ObjectEditWindow.OnComponentUpdated also listens to it --
                // without the guard, our own PrimitiveComponent notify (fired here just for the
                // Scale change) would be misread as a fresh Physical/Temporary/Phantom confirmation
                // and trivially "confirm itself", clearing the dirty-check guard in SendObjectFlags
                // before the real server echo (still possibly stale) has a chance to be filtered.
                // ObjectRenderer is a separate listener and is NOT affected by this -- it still
                // repaints normally, which is the whole point of firing this at all.
                _suppressOwnComponentNotify = true;
                var comp = _currentEntity.GetComponent<TransformComponent>();
                if (comp != null)
                {
                    comp.Position = pos;
                    comp.Rotation = rot;
                    _world?.NotifyComponentUpdated(_currentEntity, comp);
                }
                var primComp = _currentEntity.GetComponent<PrimitiveComponent>();
                if (primComp != null)
                {
                    primComp.Scale = scale;
                    _world?.NotifyComponentUpdated(_currentEntity, primComp);
                }

                var meta = _currentEntity.GetComponent<MetadataComponent>();
                if (meta != null)
                {
                    meta.Name = _nameInput.Text;
                    meta.Description = _descInput.Text;
                    _world?.NotifyComponentUpdated(_currentEntity, meta);
                }
                _suppressOwnComponentNotify = false;
            }
        }

        /// <summary>Close-button handler. Must always hide the window and always deselect
        /// *this window's* object -- previously this deferred to Boot.cs calling
        /// World.DeselectEntity(), which no-ops once the object is no longer the world's
        /// selected entity (e.g. the user already clicked a different object, or clicked empty
        /// space) and never hid the window either, so Close silently did nothing.</summary>
        private void RequestClose()
        {
            Visible = false;

            if (_session != null && _currentLocalId != 0)
            {
                _session.DeselectObject(_currentLocalId);
            }
            // World selection is a set now (multiple windows can each have their own object
            // selected at once) -- deselect only this window's entity, not "the" selection.
            if (_world != null && _currentEntity != null && _world.IsSelected(_currentEntity))
            {
                _world.DeselectEntity(_currentEntity);
            }

            _currentEntity = null;
            _currentLocalId = 0;
            Closed?.Invoke();
            QueueFree();
        }

        private void OnLockedToggled(bool pressed)
        {
            if (_currentLocalId == 0 || _session == null) return;
            _session.SetObjectLocked(_currentLocalId, pressed);

            var meta = _currentEntity?.GetComponent<MetadataComponent>();
            if (meta != null) meta.Locked = pressed;

            // Immediate feedback, no round-trip needed: Editable is local Control state, not
            // world/ECS data, so there's no self-confirm race to guard against here.
            bool canMove = !pressed;
            _posX.Editable = _posY.Editable = _posZ.Editable = canMove;
            _rotX.Editable = _rotY.Editable = _rotZ.Editable = canMove;
        }

        /// <summary>ObjectFlagUpdate replaces Physical/Temporary/Phantom/CastShadows together in
        /// one message, so every call must carry the object's full current state -- pass only
        /// the one flag that actually changed; the other two (plus CastShadows, which has no
        /// checkbox here) are read back from <see cref="_knownPhysical"/>/etc, NOT from
        /// PrimitiveComponent -- that gets overwritten by every incoming ObjectUpdate, including
        /// ones still echoing pre-change flags, so it can't be trusted as the toggle baseline.</summary>
        private void SendObjectFlags(bool? physical = null, bool? temporary = null, bool? phantom = null)
        {
            if (_currentLocalId == 0 || _session == null) return;
            var prim = _currentEntity?.GetComponent<PrimitiveComponent>();
            if (prim == null) return;

            bool p = physical ?? _knownPhysical;
            bool t = temporary ?? _knownTemporary;
            bool ph = phantom ?? _knownPhantom;
            _session.SetObjectFlags(_currentLocalId, p, t, ph, prim.CastsShadows);

            _knownPhysical = p;
            _knownTemporary = t;
            _knownPhantom = ph;
            prim.IsPhysical = p;
            prim.IsTemporary = t;
            prim.IsPhantom = ph;
            // Deliberately NOT calling _world.NotifyComponentUpdated here (unlike ApplyTransform
            // below): our own write would trivially "confirm" itself the instant it round-trips
            // through OnComponentUpdated -> UpdatePrimStateUI, clearing the pending dirty-check
            // guard below before the real server echo -- possibly still stale -- has a chance to
            // arrive and be filtered. The checkbox itself doesn't need this: Godot already shows
            // the user's own click without our help. Only the phantom-collision-layer visual
            // (ObjectRenderer) is delayed to the real echo as a result -- an acceptable trade-off.

            // Dirty-check: remember what we just asked for so a stale/late echo (see field
            // comment above) can't flip the checkbox back until it's actually confirmed.
            if (physical.HasValue) _pendingPhysical = p;
            if (temporary.HasValue) _pendingTemporary = t;
            if (phantom.HasValue) _pendingPhantom = ph;
        }

        /// <summary>Set by the owner (Boot.cs) before the first EditObject call so multiple
        /// simultaneously-open windows cascade instead of spawning exactly on top of each other.</summary>
        public int CascadeIndex { get; set; }

        private void CenterWindow()
        {
            var viewportSize = GetViewportRect().Size;
            var windowSize = Size;
            if (windowSize.X < CustomMinimumSize.X) windowSize = CustomMinimumSize;

            var cascadeOffset = new Godot.Vector2(CascadeIndex * 28, CascadeIndex * 28);
            Position = new Godot.Vector2(
                Mathf.Max(0, (viewportSize.X - windowSize.X) / 2),
                Mathf.Max(0, (viewportSize.Y - windowSize.Y) / 2)
            ) + cascadeOffset;
        }

        private void OnComponentUpdated(object? sender, ComponentEventArgs e)
        {
            if (_suppressOwnComponentNotify) return;
            if (_currentEntity == null || e.Entity.Id != _currentEntity.Id) return;

            if (e.Component is MetadataComponent meta)
            {
                CallDeferred(MethodName.UpdateMetadataUI, meta.Name, meta.Description,
                    meta.CreatorId.ToString(), meta.OwnerId.ToString(), meta.GroupId.ToString(), meta.Locked,
                    meta.OwnerCanModify, meta.OwnerCanCopy, meta.OwnerCanTransfer);
            }
            else if (e.Component is PrimitiveComponent prim)
            {
                CallDeferred(MethodName.UpdatePrimStateUI, prim.IsPhysical, prim.IsTemporary, prim.IsPhantom);
            }
        }

        private void UpdatePrimStateUI(bool physical, bool temporary, bool phantom)
        {
            // While a flag change is pending, only accept a live value that actually confirms
            // it -- otherwise a stale/late echo would un-toggle what the user just set before the
            // simulator's real confirmation has arrived. No timeout: see the field comment on
            // _pendingPhysical for why a fixed grace window caused more reverts than it prevented.
            if (_pendingPhysical is null || _pendingPhysical == physical)
            {
                _physicalCheck.SetPressedNoSignal(physical);
                _knownPhysical = physical;
                _pendingPhysical = null;
            }
            if (_pendingTemporary is null || _pendingTemporary == temporary)
            {
                _tempCheck.SetPressedNoSignal(temporary);
                _knownTemporary = temporary;
                _pendingTemporary = null;
            }
            if (_pendingPhantom is null || _pendingPhantom == phantom)
            {
                _phantomCheck.SetPressedNoSignal(phantom);
                _knownPhantom = phantom;
                _pendingPhantom = null;
            }
        }

        private void UpdateMetadataUI(string name, string desc, string creatorIdStr, string ownerIdStr, string groupIdStr, bool locked,
            bool ownerCanModify, bool ownerCanCopy, bool ownerCanTransfer)
        {
            if (!_nameInput.HasFocus()) _nameInput.Text = name;
            if (!_descInput.HasFocus()) _descInput.Text = desc;
            _lockedCheck.SetPressedNoSignal(locked);

            _currentCreatorId = System.Guid.Parse(creatorIdStr);
            _currentOwnerId = System.Guid.Parse(ownerIdStr);
            _currentGroupId = System.Guid.Parse(groupIdStr);
            ResolveNameLabel(_creatorLabel, _currentCreatorId, isGroup: false, emptyPlaceholder: "(unknown)");
            ResolveNameLabel(_ownerLabel, _currentOwnerId, isGroup: false, emptyPlaceholder: "(unknown)");
            ResolveNameLabel(_groupLabel, _currentGroupId, isGroup: true, emptyPlaceholder: "(none)");
            UpdatePermissionCheckboxes(_currentOwnerId, ownerCanModify, ownerCanCopy, ownerCanTransfer, !locked);

            Title = $"Edit: {(string.IsNullOrEmpty(name) ? "Object" : name)}";
        }

        /// <summary>Read-only Owner-permission indicators (Modify/Copy/Transfer/Move) -- "Move" is
        /// !Locked (see MetadataComponent.Locked), same underlying OwnerMask bit under a name
        /// that's already tracked elsewhere.
        ///
        /// IMPORTANT: these are always the OBJECT OWNER's permission bits, not necessarily YOURS.
        /// OpenSim's CanEditObject checks the CONNECTED AGENT's effective permissions, which only
        /// equal OwnerMask when you actually are the owner -- for a group- or everyone-editable
        /// object they'd come from GroupMask/EveryoneMask instead, which we don't resolve here.
        /// "Modify: Yes" while you are NOT the owner tells you the owner can modify their own
        /// object; it says nothing about whether your own edits will be accepted.</summary>
        private void UpdatePermissionCheckboxes(System.Guid ownerId, bool canModify, bool canCopy, bool canTransfer, bool canMove)
        {
            _permModifyCheck.SetPressedNoSignal(canModify);
            _permCopyCheck.SetPressedNoSignal(canCopy);
            _permTransferCheck.SetPressedNoSignal(canTransfer);
            _permMoveCheck.SetPressedNoSignal(canMove);

            bool isOwner = _session != null && ownerId != System.Guid.Empty
                && string.Equals(_session.AgentId, ownerId.ToString(), System.StringComparison.OrdinalIgnoreCase);
            _isOwnerLabel.Text = isOwner ? "Owner Permissions (you are the owner):" : "Owner Permissions (NOT yours -- shown for reference only):";

            // Match the real SL viewer's llpanelobject.cpp: Position/Rotation fields are
            // literally disabled without Move permission (canMove == !Locked), not merely sent
            // and left to the server -- OpenSim's own position-update path (ClientChangeObject)
            // checks Modify first and only falls back to a Move-only check when Modify is absent,
            // so for a full-perm (Modify-granted) Locked object the server accepts the move
            // anyway; only a client-side disable actually enforces "Locked" here.
            _posX.Editable = canMove;
            _posY.Editable = canMove;
            _posZ.Editable = canMove;
            _rotX.Editable = canMove;
            _rotY.Editable = canMove;
            _rotZ.Editable = canMove;
        }

        /// <summary>Shows a user/group's display name for <paramref name="id"/>, using the
        /// GridSession name cache where possible and kicking off an async lookup otherwise (the
        /// name arrives later via <see cref="OnNameResolved"/>). Stashes the id on the label via
        /// SetMeta so a future click-to-profile handler can read it back without re-plumbing.</summary>
        private void ResolveNameLabel(Label label, System.Guid id, bool isGroup, string emptyPlaceholder)
        {
            label.SetMeta("Id", id.ToString());

            if (id == System.Guid.Empty)
            {
                label.Text = emptyPlaceholder;
                return;
            }

            if (_session != null && _session.TryGetCachedName(id, out var name))
            {
                label.Text = name;
                return;
            }

            label.Text = "Loading...";
            if (isGroup) _session?.RequestGroupName(id);
            else _session?.RequestAvatarName(id);
        }

        private void OnNameResolved(object? sender, NameResolvedEvent e)
        {
            if (e.Id == _currentCreatorId || e.Id == _currentOwnerId || e.Id == _currentGroupId)
            {
                CallDeferred(MethodName.ApplyResolvedName, e.Id.ToString(), e.Name);
            }
        }

        private void ApplyResolvedName(string idStr, string name)
        {
            var id = System.Guid.Parse(idStr);
            if (id == _currentCreatorId) _creatorLabel.Text = name;
            if (id == _currentOwnerId) _ownerLabel.Text = name;
            if (id == _currentGroupId) _groupLabel.Text = name;
        }
    }
}
