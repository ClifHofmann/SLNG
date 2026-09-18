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

        // Same dirty-check role as _knownPhysical/_pendingPhysical above, for the Light
        // ExtraParams block (Features tab). Each sub-field gets its OWN pending guard, not one
        // shared flag -- SetObjectLight sends the whole block together, but a routine full
        // ObjectUpdate can arrive between the send and the simulator's real echo still carrying
        // the OLD color/intensity/radius/falloff while Enabled happens to already match (it
        // didn't change), so gating everything on _pendingLightEnabled alone let that stale
        // update sail through and snap the color picker back the instant a color was applied.
        // Wire-format color is byte-quantized (~1/255 max error), so matching uses a tolerance,
        // not exact float equality -- see ApproxEqual.
        private bool _knownLightEnabled;
        private System.Numerics.Vector3 _knownLightColor = System.Numerics.Vector3.One;
        private float _knownLightIntensity = 1.0f, _knownLightRadius = 10.0f, _knownLightFalloff = 1.0f;
        private bool? _pendingLightEnabled;
        private System.Numerics.Vector3? _pendingLightColor;
        private float? _pendingLightIntensity, _pendingLightRadius, _pendingLightFalloff;

        // Same dirty-check role, for the classic Material dropdown (Stone/Metal/.../Rubber).
        private PrimMaterial _knownMaterial = PrimMaterial.Wood;
        private PrimMaterial? _pendingMaterial;
        private byte _knownClickAction;
        private byte? _pendingClickAction;

        // Physics Shape Type + Gravity/Friction/Density/Bounciness (Features tab "Physics"
        // section) share ObjectFlagUpdate's wire message with Physical/Temporary/Phantom -- every
        // SetObjectFlags call resends ALL of it. _hasKnownPhysics guards against sending our own
        // guessed defaults the FIRST time a flag checkbox is toggled before the server's real
        // PhysicsPropertiesEvent has arrived (it's fetched asynchronously via the EventQueue CAP,
        // triggered by ObjectSelectionController's SelectObject call, not bundled with
        // ObjectUpdate) -- until then, flag-only toggles keep sending the (byte)255 "leave extra
        // physics data alone" sentinel, exactly like before this feature existed.
        private bool _hasKnownPhysics;
        private PrimPhysicsShapeType _knownPhysicsShapeType = PrimPhysicsShapeType.Prim;
        private float _knownPhysicsGravity = 1.0f, _knownPhysicsFriction = 0.6f, _knownPhysicsDensity = 1000f, _knownPhysicsRestitution = 0.5f;
        private PrimPhysicsShapeType? _pendingPhysicsShapeType;
        private float? _pendingPhysicsGravity, _pendingPhysicsFriction, _pendingPhysicsDensity, _pendingPhysicsRestitution;

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
        private Button _copyAssetUuidBtn = null!;

        // TPV policy compliance (AGENTS.md Non-negotiable #1): recomputed by
        // UpdatePermissionCheckboxes on every properties update, never just at button-press time
        // -- true only when the local agent both OWNS this object AND has full permissions
        // (Modify+Copy+Transfer) on it, mirroring the real viewer's own gate for exposing a raw
        // asset UUID. OwnerCanModify/OwnerCanCopy/OwnerCanTransfer are the OBJECT OWNER's bits,
        // not necessarily the local agent's own (see UpdatePermissionCheckboxes' doc comment) --
        // requiring isOwner here is what makes them the same thing.
        private bool _canCopyAssetUuid;

        private CheckBox _lightCheck = null!;
        private ColorPickerButton _lightColorPicker = null!;
        private LineEdit _lightIntensityInput = null!, _lightRadiusInput = null!, _lightFalloffInput = null!;

        // Index order matches PrimMaterial's declaration order exactly (Stone=0..Rubber=6), so
        // the OptionButton's selected index can cast directly to PrimMaterial with no lookup.
        private OptionButton _materialOption = null!;

        // Index order matches PrimPhysicsShapeType's declaration order (Prim=0, None=1,
        // ConvexHull=2), same direct-cast reasoning as _materialOption.
        private OptionButton _physicsShapeOption = null!;
        private OptionButton _clickActionOption = null!;
        private LineEdit _physicsGravityInput = null!, _physicsFrictionInput = null!, _physicsDensityInput = null!, _physicsRestitutionInput = null!;

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
            Title = L10n.Tr("ui.build.title");
            Visible = false;
            CustomMinimumSize = new Vector2(320, 400);

            // FEAT-UI-04: which in-world handles are shown. Above the tabs rather than inside
            // one, because it governs the 3D manipulator, not any single tab's fields -- and
            // showing move, rotate and scale handles all at once is unusable, which is why the
            // reference viewer makes you pick too.
            var toolRow = new HBoxContainer { Name = "GizmoTools" };
            toolRow.AddThemeConstantOverride("separation", 4);
            ContentContainer.AddChild(toolRow);

            _moveToolButton = new Button
            {
                Text = L10n.Tr("ui.build.tool_move"),
                ToggleMode = true,
                ButtonPressed = true,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            _rotateToolButton = new Button
            {
                Text = L10n.Tr("ui.build.tool_rotate"),
                ToggleMode = true,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            toolRow.AddChild(_moveToolButton);
            toolRow.AddChild(_rotateToolButton);

            // Hand-rolled radio behaviour rather than a ButtonGroup: a ButtonGroup lets the
            // pressed button be un-pressed by clicking it again, which would leave no tool
            // selected and no handles at all.
            _moveToolButton.Pressed += () => SelectTool(GizmoTool.Move);
            _rotateToolButton.Pressed += () => SelectTool(GizmoTool.Rotate);

            var tabContainer = new TabContainer();
            ContentContainer.AddChild(tabContainer);

            // General Tab
            var generalTab = new MarginContainer { Name = L10n.Tr("ui.build.tab_general") };
            var generalVBox = new VBoxContainer();
            generalTab.AddChild(generalVBox);
            tabContainer.AddChild(generalTab);
            
            _nameInput = new LineEdit { PlaceholderText = "Name", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _descInput = new LineEdit { PlaceholderText = "Description", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _creatorLabel = new Label { Text = "Loading..." };
            _ownerLabel = new Label { Text = "Loading..." };
            _groupLabel = new Label { Text = "Loading..." };

            _clickActionOption = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _clickActionOption.AddItem(L10n.Tr("ui.build.click_action_touch"), 0);
            _clickActionOption.AddItem(L10n.Tr("ui.build.click_action_sit"), 1);
            _clickActionOption.AddItem(L10n.Tr("ui.build.click_action_buy"), 2);
            _clickActionOption.AddItem(L10n.Tr("ui.build.click_action_pay"), 3);
            _clickActionOption.AddItem(L10n.Tr("ui.build.click_action_open"), 4);
            _clickActionOption.AddItem(L10n.Tr("ui.build.click_action_play"), 5);
            _clickActionOption.AddItem(L10n.Tr("ui.build.click_action_open_media"), 6);
            _clickActionOption.ItemSelected += idx => SendObjectClickAction((byte)idx);

            var genGrid = new GridContainer { Columns = 2 };
            genGrid.AddChild(new Label { Text = L10n.Tr("ui.build.name") }); genGrid.AddChild(_nameInput);
            genGrid.AddChild(new Label { Text = L10n.Tr("ui.build.description") }); genGrid.AddChild(_descInput);
            genGrid.AddChild(new Label { Text = L10n.Tr("ui.build.creator") }); genGrid.AddChild(_creatorLabel);
            genGrid.AddChild(new Label { Text = L10n.Tr("ui.build.owner") }); genGrid.AddChild(_ownerLabel);
            genGrid.AddChild(new Label { Text = L10n.Tr("ui.build.group") }); genGrid.AddChild(_groupLabel);
            genGrid.AddChild(new Label { Text = L10n.Tr("ui.build.click_action") }); genGrid.AddChild(_clickActionOption);
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

            // Only ever enabled for content the local agent owns AND has full permissions on --
            // see _canCopyAssetUuid's doc comment. Disabled by default until the first real
            // properties update confirms that.
            _copyAssetUuidBtn = new Button { Text = "📋 Copy Asset UUID", Disabled = true, CustomMinimumSize = new Vector2(0, 30) };
            _copyAssetUuidBtn.Pressed += OnCopyAssetUuidPressed;
            generalVBox.AddChild(_copyAssetUuidBtn);

            // Object (Transform) Tab
            var objectTab = new MarginContainer { Name = L10n.Tr("ui.build.tab_object") };
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
            var featuresTab = new MarginContainer { Name = L10n.Tr("ui.build.tab_features") };
            var featVBox = new VBoxContainer();
            featuresTab.AddChild(featVBox);
            tabContainer.AddChild(featuresTab);
            
            _lightCheck = new CheckBox { Text = "Light" };
            _lightCheck.Toggled += _ => SendObjectLight();
            featVBox.AddChild(_lightCheck);

            _lightColorPicker = new ColorPickerButton { Text = "Color", Color = Colors.White, CustomMinimumSize = new Vector2(0, 30) };
            _lightIntensityInput = new LineEdit { CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _lightRadiusInput = new LineEdit { CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _lightFalloffInput = new LineEdit { CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };

            var lightGrid = new GridContainer { Columns = 2 };
            lightGrid.AddChild(new Label { Text = "Color:" }); lightGrid.AddChild(_lightColorPicker);
            lightGrid.AddChild(new Label { Text = "Intensity:" }); lightGrid.AddChild(_lightIntensityInput);
            lightGrid.AddChild(new Label { Text = "Radius:" }); lightGrid.AddChild(_lightRadiusInput);
            lightGrid.AddChild(new Label { Text = "Falloff:" }); lightGrid.AddChild(_lightFalloffInput);
            featVBox.AddChild(lightGrid);

            var lightApplyBtn = new Button { Text = "Apply Light", CustomMinimumSize = new Vector2(0, 30) };
            lightApplyBtn.Pressed += SendObjectLight;
            featVBox.AddChild(lightApplyBtn);

            featVBox.AddChild(new HSeparator());

            _materialOption = new OptionButton();
            foreach (var m in new[] { "Stone", "Metal", "Glass", "Wood", "Flesh", "Plastic", "Rubber" })
                _materialOption.AddItem(m);
            _materialOption.ItemSelected += idx => SendObjectMaterial((PrimMaterial)idx);
            var materialGrid = new GridContainer { Columns = 2 };
            materialGrid.AddChild(new Label { Text = "Material:" }); materialGrid.AddChild(_materialOption);
            featVBox.AddChild(materialGrid);

            featVBox.AddChild(new HSeparator());
            featVBox.AddChild(new Label { Text = "Physics" });

            _physicsShapeOption = new OptionButton();
            foreach (var s in new[] { "Prim", "None", "Convex Hull" })
                _physicsShapeOption.AddItem(s);
            _physicsGravityInput = new LineEdit { CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _physicsFrictionInput = new LineEdit { CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _physicsDensityInput = new LineEdit { CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _physicsRestitutionInput = new LineEdit { CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };

            var physicsGrid = new GridContainer { Columns = 2 };
            physicsGrid.AddChild(new Label { Text = "Shape Type:" }); physicsGrid.AddChild(_physicsShapeOption);
            physicsGrid.AddChild(new Label { Text = "Gravity:" }); physicsGrid.AddChild(_physicsGravityInput);
            physicsGrid.AddChild(new Label { Text = "Friction:" }); physicsGrid.AddChild(_physicsFrictionInput);
            physicsGrid.AddChild(new Label { Text = "Density:" }); physicsGrid.AddChild(_physicsDensityInput);
            physicsGrid.AddChild(new Label { Text = "Bounciness:" }); physicsGrid.AddChild(_physicsRestitutionInput);
            featVBox.AddChild(physicsGrid);

            var physicsApplyBtn = new Button { Text = "Apply Physics", CustomMinimumSize = new Vector2(0, 30) };
            physicsApplyBtn.Pressed += SendObjectPhysics;
            featVBox.AddChild(physicsApplyBtn);

            // Texture Tab
            var textureTab = new MarginContainer { Name = L10n.Tr("ui.build.tab_texture") };
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
            var contentTab = new MarginContainer { Name = L10n.Tr("ui.build.tab_content") };
            contentTab.AddChild(new Label { Text = "Inventory inside object...\n(Loading functionality coming in M6)" });
            tabContainer.AddChild(contentTab);
        }

        public void EditObject(Entity entity, uint localId, World? world)
        {
            var transform = entity.GetComponent<TransformComponent>();

            // Resolve to the Root object of the linkset, unless Edit Linked Parts is on
            // (FEAT-UI-06) -- in which case the caller (ObjectSelectionController) already left
            // the specifically-clicked part as entity/localId and this must not override that.
            if (!SelectionSettings.EditLinkedParts && transform != null && transform.ParentLocalId != 0 && _session != null && world != null)
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
            _pendingLightEnabled = null;
            _pendingLightColor = null;
            _pendingLightIntensity = _pendingLightRadius = _pendingLightFalloff = null;
            _pendingMaterial = null;
            _pendingClickAction = null;
            _pendingPhysicsShapeType = null;
            _pendingPhysicsGravity = _pendingPhysicsFriction = _pendingPhysicsDensity = _pendingPhysicsRestitution = null;
            // Physics data is per-object and fetched asynchronously (see the field comment on
            // _hasKnownPhysics) -- opening a different object means we no longer know ITS real
            // physics state until a fresh PhysicsPropertiesEvent arrives.
            _hasKnownPhysics = false;

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

                _knownClickAction = primitive.ClickAction;
                int clickIdx = primitive.ClickAction <= 6 ? primitive.ClickAction : 0;
                _clickActionOption.Selected = clickIdx;

                _knownLightEnabled = primitive.LightEnabled;
                _knownLightColor = primitive.LightColor;
                _knownLightIntensity = primitive.LightIntensity;
                _knownLightRadius = primitive.LightRadius;
                _knownLightFalloff = primitive.LightFalloff;
                _lightCheck.SetPressedNoSignal(primitive.LightEnabled);
                _lightColorPicker.Color = new Color(primitive.LightColor.X, primitive.LightColor.Y, primitive.LightColor.Z);
                _lightIntensityInput.Text = primitive.LightIntensity.ToString("F2");
                _lightRadiusInput.Text = primitive.LightRadius.ToString("F2");
                _lightFalloffInput.Text = primitive.LightFalloff.ToString("F2");

                _knownMaterial = primitive.Material;
                _materialOption.Selected = (int)primitive.Material;

                // Placeholder display only -- PrimitiveComponent's physics fields are just its
                // own constructor defaults until the real PhysicsPropertiesEvent lands (see
                // _hasKnownPhysics); UpdatePrimStateUI corrects this once it arrives.
                _knownPhysicsShapeType = primitive.PhysicsShapeType;
                _knownPhysicsGravity = primitive.PhysicsGravity;
                _knownPhysicsFriction = primitive.PhysicsFriction;
                _knownPhysicsDensity = primitive.PhysicsDensity;
                _knownPhysicsRestitution = primitive.PhysicsRestitution;
                _physicsShapeOption.Selected = (int)primitive.PhysicsShapeType;
                _physicsGravityInput.Text = primitive.PhysicsGravity.ToString("F2");
                _physicsFrictionInput.Text = primitive.PhysicsFriction.ToString("F2");
                _physicsDensityInput.Text = primitive.PhysicsDensity.ToString("F2");
                _physicsRestitutionInput.Text = primitive.PhysicsRestitution.ToString("F2");
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

            Title = L10n.TrFormat("ui.build.edit_title", titleName);
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
            // Physics shape/material share this same wire message -- resend the last known-good
            // values, or the (byte)255 "leave untouched" sentinel if we've never actually learned
            // this object's real physics data yet (see _hasKnownPhysics), so a plain flag toggle
            // can't silently reset physics settings the user (or object) already had.
            var shapeType = _hasKnownPhysics ? _knownPhysicsShapeType : (PrimPhysicsShapeType)255;
            _session.SetObjectFlags(_currentLocalId, p, t, ph, prim.CastsShadows,
                shapeType, _knownPhysicsDensity, _knownPhysicsFriction, _knownPhysicsRestitution, _knownPhysicsGravity);

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

        /// <summary>Sends the Light checkbox + color/intensity/radius/falloff fields as one
        /// ExtraParams block (SetObjectLight always sends the whole block, not a delta) -- shared
        /// by the Light checkbox toggle and the "Apply Light" button. Same self-confirm and
        /// dirty-check reasoning as SendObjectFlags: no NotifyComponentUpdated call here, and a
        /// pending flag guards UpdatePrimStateUI against a stale echo reverting the change.</summary>
        private void SendObjectLight()
        {
            if (_currentLocalId == 0 || _session == null) return;

            bool enabled = _lightCheck.ButtonPressed;
            var c = _lightColorPicker.Color;
            var color = new System.Numerics.Vector3(c.R, c.G, c.B);
            if (!float.TryParse(_lightIntensityInput.Text, out float intensity)) intensity = _knownLightIntensity;
            if (!float.TryParse(_lightRadiusInput.Text, out float radius)) radius = _knownLightRadius;
            if (!float.TryParse(_lightFalloffInput.Text, out float falloff)) falloff = _knownLightFalloff;

            // SL's wire protocol has no separate "light enabled" bit -- ObjectManager.SetLight
            // sends ParamInUse = Intensity != 0, so Intensity IS the enabled signal. An object
            // that was never lit correctly shows Intensity 0.00 here (its real prior server
            // state); checking "Light" without also raising Intensity off zero would silently
            // send OpenSim a disable request, indistinguishable from doing nothing -- exactly
            // the "change doesn't save" symptom reported. Seed sane defaults whenever enabling
            // from a zeroed-out field so the checkbox alone is enough to produce a visible light.
            if (enabled && intensity <= 0f) { intensity = 1.0f; _lightIntensityInput.Text = intensity.ToString("F2"); }
            if (enabled && radius <= 0f) { radius = 10.0f; _lightRadiusInput.Text = radius.ToString("F2"); }
            if (enabled && falloff <= 0f) { falloff = 1.0f; _lightFalloffInput.Text = falloff.ToString("F2"); }

            GD.Print($"[ObjectEditWindow] SendObjectLight -> localId={_currentLocalId} enabled={enabled} color={color} intensity={intensity} radius={radius} falloff={falloff}");
            _session.SetObjectLight(_currentLocalId, enabled, color, intensity, radius, falloff);

            var prim = _currentEntity?.GetComponent<PrimitiveComponent>();
            if (prim != null)
            {
                prim.LightEnabled = enabled;
                prim.LightColor = color;
                prim.LightIntensity = intensity;
                prim.LightRadius = radius;
                prim.LightFalloff = falloff;
            }

            _knownLightEnabled = enabled;
            _knownLightColor = color;
            _knownLightIntensity = intensity;
            _knownLightRadius = radius;
            _knownLightFalloff = falloff;

            _pendingLightEnabled = enabled;
            _pendingLightColor = color;
            _pendingLightIntensity = intensity;
            _pendingLightRadius = radius;
            _pendingLightFalloff = falloff;
        }

        private static bool ApproxEqual(float a, float b, float eps = 0.05f) => System.Math.Abs(a - b) <= eps;
        private static bool ApproxEqual(System.Numerics.Vector3 a, System.Numerics.Vector3 b, float eps = 0.02f) =>
            ApproxEqual(a.X, b.X, eps) && ApproxEqual(a.Y, b.Y, eps) && ApproxEqual(a.Z, b.Z, eps);

        private void SendObjectMaterial(PrimMaterial material)
        {
            if (_currentLocalId == 0 || _session == null) return;

            _session.SetObjectMaterial(_currentLocalId, material);

            var prim = _currentEntity?.GetComponent<PrimitiveComponent>();
            if (prim != null) prim.Material = material;

            _knownMaterial = material;
            _pendingMaterial = material;
        }

        private void SendObjectClickAction(byte clickAction)
        {
            if (_currentLocalId == 0 || _session == null) return;

            _session.SetObjectClickAction(_currentLocalId, clickAction);

            var prim = _currentEntity?.GetComponent<PrimitiveComponent>();
            if (prim != null) prim.ClickAction = clickAction;

            _knownClickAction = clickAction;
            _pendingClickAction = clickAction;
        }

        /// <summary>Sends Physics Shape Type + Gravity/Friction/Density/Bounciness via the same
        /// ObjectFlagUpdate message Physical/Temporary/Phantom use -- carries the current known
        /// flag state through unchanged, same bundling reasoning as SendObjectFlags.</summary>
        private void SendObjectPhysics()
        {
            if (_currentLocalId == 0 || _session == null) return;
            var prim = _currentEntity?.GetComponent<PrimitiveComponent>();
            if (prim == null) return;

            var shapeType = (PrimPhysicsShapeType)_physicsShapeOption.Selected;
            if (!float.TryParse(_physicsGravityInput.Text, out float gravity)) gravity = _knownPhysicsGravity;
            if (!float.TryParse(_physicsFrictionInput.Text, out float friction)) friction = _knownPhysicsFriction;
            if (!float.TryParse(_physicsDensityInput.Text, out float density)) density = _knownPhysicsDensity;
            if (!float.TryParse(_physicsRestitutionInput.Text, out float restitution)) restitution = _knownPhysicsRestitution;

            _session.SetObjectFlags(_currentLocalId, _knownPhysical, _knownTemporary, _knownPhantom, prim.CastsShadows,
                shapeType, density, friction, restitution, gravity);

            prim.PhysicsShapeType = shapeType;
            prim.PhysicsGravity = gravity;
            prim.PhysicsFriction = friction;
            prim.PhysicsDensity = density;
            prim.PhysicsRestitution = restitution;

            _knownPhysicsShapeType = shapeType;
            _knownPhysicsGravity = gravity;
            _knownPhysicsFriction = friction;
            _knownPhysicsDensity = density;
            _knownPhysicsRestitution = restitution;
            _hasKnownPhysics = true;

            _pendingPhysicsShapeType = shapeType;
            _pendingPhysicsGravity = gravity;
            _pendingPhysicsFriction = friction;
            _pendingPhysicsDensity = density;
            _pendingPhysicsRestitution = restitution;
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

        /// <summary>FEAT-UI-04: which handle set the in-world gizmo shows. Scale is a later
        /// pass of the same spec; the enum is here so adding it does not change this shape.</summary>
        public enum GizmoTool { Move, Rotate }

        private Button _moveToolButton = null!;
        private Button _rotateToolButton = null!;
        private GizmoTool _tool = GizmoTool.Move;

        /// <summary>Raised when the user picks a different manipulator. Boot forwards it to the
        /// gizmo -- this window does not know the 3D scene exists.</summary>
        public event System.Action<GizmoTool>? ToolChanged;

        public GizmoTool CurrentTool => _tool;

        private void SelectTool(GizmoTool tool)
        {
            _tool = tool;
            _moveToolButton.SetPressedNoSignal(tool == GizmoTool.Move);
            _rotateToolButton.SetPressedNoSignal(tool == GizmoTool.Rotate);
            ToolChanged?.Invoke(tool);
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
            else if (e.Component is TransformComponent tc)
            {
                // FEAT-UI-04: follow a gizmo drag (and any sim-side move) in the numeric fields.
                // Skipped while a field has focus, so a live update cannot overwrite digits the
                // user is in the middle of typing -- the same courtesy UpdateMetadataUI extends
                // to the name and description fields.
                CallDeferred(MethodName.UpdatePositionUI, tc.Position.X, tc.Position.Y, tc.Position.Z);
            }
            else if (e.Component is PrimitiveComponent prim)
            {
                CallDeferred(MethodName.UpdatePrimStateUI, prim.IsPhysical, prim.IsTemporary, prim.IsPhantom,
                    prim.LightEnabled, prim.LightColor.X, prim.LightColor.Y, prim.LightColor.Z,
                    prim.LightIntensity, prim.LightRadius, prim.LightFalloff, (int)prim.Material,
                    (int)prim.PhysicsShapeType, prim.PhysicsGravity, prim.PhysicsFriction, prim.PhysicsDensity, prim.PhysicsRestitution,
                    prim.HasPhysicsProperties, (int)prim.ClickAction);
            }
        }

        /// <summary>FEAT-UI-04: refresh just the Position fields from a live transform change.
        /// Separate from the full populate path because that one also rewrites rotation, scale,
        /// name and description, which a drag has not touched.</summary>
        private void UpdatePositionUI(float x, float y, float z)
        {
            if (!_posX.HasFocus()) _posX.Text = x.ToString("F3");
            if (!_posY.HasFocus()) _posY.Text = y.ToString("F3");
            if (!_posZ.HasFocus()) _posZ.Text = z.ToString("F3");
        }

        private void UpdatePrimStateUI(bool physical, bool temporary, bool phantom,
            bool lightEnabled, float lightR, float lightG, float lightB, float lightIntensity, float lightRadius, float lightFalloff,
            int materialInt, int physicsShapeInt, float physicsGravity, float physicsFriction, float physicsDensity, float physicsRestitution,
            bool hasPhysicsProperties, int clickActionInt)
        {
            var material = (PrimMaterial)materialInt;
            var physicsShape = (PrimPhysicsShapeType)physicsShapeInt;
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
            if (_pendingLightEnabled is null || _pendingLightEnabled == lightEnabled)
            {
                _lightCheck.SetPressedNoSignal(lightEnabled);
                _knownLightEnabled = lightEnabled;
                _pendingLightEnabled = null;
            }

            var incomingColor = new System.Numerics.Vector3(lightR, lightG, lightB);
            if (_pendingLightColor is null || ApproxEqual(_pendingLightColor.Value, incomingColor))
            {
                _lightColorPicker.Color = new Color(lightR, lightG, lightB);
                _knownLightColor = incomingColor;
                _pendingLightColor = null;
            }
            if (_pendingLightIntensity is null || ApproxEqual(_pendingLightIntensity.Value, lightIntensity))
            {
                _lightIntensityInput.Text = lightIntensity.ToString("F2");
                _knownLightIntensity = lightIntensity;
                _pendingLightIntensity = null;
            }
            if (_pendingLightRadius is null || ApproxEqual(_pendingLightRadius.Value, lightRadius))
            {
                _lightRadiusInput.Text = lightRadius.ToString("F2");
                _knownLightRadius = lightRadius;
                _pendingLightRadius = null;
            }
            if (_pendingLightFalloff is null || ApproxEqual(_pendingLightFalloff.Value, lightFalloff))
            {
                _lightFalloffInput.Text = lightFalloff.ToString("F2");
                _knownLightFalloff = lightFalloff;
                _pendingLightFalloff = null;
            }
            if (_pendingMaterial is null || _pendingMaterial == material)
            {
                _materialOption.Selected = (int)material;
                _knownMaterial = material;
                _pendingMaterial = null;
            }

            byte clickAction = (byte)(clickActionInt <= 6 ? clickActionInt : 0);
            if (_pendingClickAction is null || _pendingClickAction == clickAction)
            {
                _clickActionOption.Selected = clickAction;
                _knownClickAction = clickAction;
                _pendingClickAction = null;
            }

            // hasPhysicsProperties distinguishes "this notify carries a real, server-confirmed
            // physics update" from "this notify fired for an unrelated PrimitiveComponent change
            // (position, texture, ...) that happens to share the same event" -- PrimitiveComponent
            // always HAS PhysicsX fields (constructor defaults), but they're only trustworthy once
            // this is true (see PrimitiveComponent.HasPhysicsProperties). Without this gate, the
            // very first ordinary object update after opening Edit would wrongly mark placeholder
            // defaults as "known," and the next flag toggle would overwrite the object's real
            // physics settings with those fake defaults instead of preserving them.
            if (!hasPhysicsProperties) return;

            _hasKnownPhysics = true;
            if (_pendingPhysicsShapeType is null || _pendingPhysicsShapeType == physicsShape)
            {
                _physicsShapeOption.Selected = (int)physicsShape;
                _knownPhysicsShapeType = physicsShape;
                _pendingPhysicsShapeType = null;
            }
            if (_pendingPhysicsGravity is null || ApproxEqual(_pendingPhysicsGravity.Value, physicsGravity))
            {
                _physicsGravityInput.Text = physicsGravity.ToString("F2");
                _knownPhysicsGravity = physicsGravity;
                _pendingPhysicsGravity = null;
            }
            if (_pendingPhysicsFriction is null || ApproxEqual(_pendingPhysicsFriction.Value, physicsFriction))
            {
                _physicsFrictionInput.Text = physicsFriction.ToString("F2");
                _knownPhysicsFriction = physicsFriction;
                _pendingPhysicsFriction = null;
            }
            if (_pendingPhysicsDensity is null || ApproxEqual(_pendingPhysicsDensity.Value, physicsDensity))
            {
                _physicsDensityInput.Text = physicsDensity.ToString("F2");
                _knownPhysicsDensity = physicsDensity;
                _pendingPhysicsDensity = null;
            }
            if (_pendingPhysicsRestitution is null || ApproxEqual(_pendingPhysicsRestitution.Value, physicsRestitution))
            {
                _physicsRestitutionInput.Text = physicsRestitution.ToString("F2");
                _knownPhysicsRestitution = physicsRestitution;
                _pendingPhysicsRestitution = null;
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

            Title = L10n.TrFormat("ui.build.edit_title", string.IsNullOrEmpty(name) ? "Object" : name);
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

            // FEAT-SEC-04: the simulator's own per-agent answer, not a guess. Comparing ownerId
            // to the agent id -- what this did -- is wrong for a group-owned object, where the
            // owner id IS the group's, and it could never speak to group- or everyone-editable
            // objects at all. EditPermission reads the flags the sim already evaluated for us.
            bool isOwner = EditPermission.IsOwner(_world!, _currentEntity);
            bool youMayModify = EditPermission.CanModify(_world!, _currentEntity);
            bool youMayMove = EditPermission.CanMove(_world!, _currentEntity);

            _isOwnerLabel.Text = isOwner
                ? "Owner Permissions (you are the owner):"
                : youMayModify
                    ? "Owner Permissions (not yours, but you may edit this):"
                    : "Owner Permissions (NOT yours -- shown for reference only):";

            // See _canCopyAssetUuid's doc comment: only true for full-permission content the
            // local agent actually owns, never for someone else's restricted content.
            _canCopyAssetUuid = isOwner && canModify && canCopy && canTransfer;
            _copyAssetUuidBtn.Disabled = !_canCopyAssetUuid;
            _copyAssetUuidBtn.TooltipText = _canCopyAssetUuid
                ? "Copy this object's mesh/sculpt/texture asset UUID to the clipboard."
                : "Requires full permissions (Modify + Copy + Transfer) on an object you own.";

            // Match the real SL viewer's llpanelobject.cpp: Position/Rotation fields are
            // literally disabled without Move permission (canMove == !Locked), not merely sent
            // and left to the server -- OpenSim's own position-update path (ClientChangeObject)
            // checks Modify first and only falls back to a Move-only check when Modify is absent,
            // so for a full-perm (Modify-granted) Locked object the server accepts the move
            // anyway; only a client-side disable actually enforces "Locked" here.
            //
            // FEAT-SEC-04: gated on the AGENT's Move bit, not the owner's. `canMove` here is
            // !meta.Locked -- the owner's Move permission -- which answers a different question
            // and happened to coincide only while you were the owner. A transform on someone
            // else's object needs FLAGS_OBJECT_MOVE for you, which is what youMayMove is.
            _posX.Editable = youMayMove;
            _posY.Editable = youMayMove;
            _posZ.Editable = youMayMove;
            _rotX.Editable = youMayMove;
            _rotY.Editable = youMayMove;
            _rotZ.Editable = youMayMove;

            // FEAT-SEC-04 follow-up: everything else on this panel is a MODIFY operation, and
            // until now only Position/Rotation were gated -- so on someone else's object you
            // could still type a new scale, rename it, relight it or change its physics shape.
            // The sim rejects those, but silently: the field keeps the value you typed and the
            // object does not change, which reads as a viewer bug rather than a refusal.
            //
            // Split exactly as llpanelobject.cpp:refresh() does it: `enable_scale =
            // enable_modify`, `enable_rotate = enable_move`. Scale is Modify, not Move -- the two
            // only coincide while you are the owner.
            _scaleX.Editable = youMayModify;
            _scaleY.Editable = youMayModify;
            _scaleZ.Editable = youMayModify;
            _nameInput.Editable = youMayModify;
            _descInput.Editable = youMayModify;

            _lockedCheck.Disabled = !youMayModify;
            _physicalCheck.Disabled = !youMayModify;
            _tempCheck.Disabled = !youMayModify;
            _phantomCheck.Disabled = !youMayModify;

            _lightCheck.Disabled = !youMayModify;
            _lightColorPicker.Disabled = !youMayModify;
            _lightIntensityInput.Editable = youMayModify;
            _lightRadiusInput.Editable = youMayModify;
            _lightFalloffInput.Editable = youMayModify;

            _materialOption.Disabled = !youMayModify;
            _physicsShapeOption.Disabled = !youMayModify;
            _clickActionOption.Disabled = !youMayModify;
            _physicsGravityInput.Editable = youMayModify;
            _physicsFrictionInput.Editable = youMayModify;
            _physicsDensityInput.Editable = youMayModify;
            _physicsRestitutionInput.Editable = youMayModify;

            // These four are documented read-only INDICATORS (see this method's summary) and have
            // no Toggled handler at all -- so clicking one used to flip the tick and change
            // nothing, which reads as "I just edited the permissions" and is a lie. Made
            // genuinely inert rather than Disabled, because Disabled greys the tick out and these
            // exist to be read. Not gated on ownership: they are never editable by anyone here.
            foreach (var indicator in new[] { _permModifyCheck, _permCopyCheck, _permTransferCheck, _permMoveCheck })
            {
                indicator.MouseFilter = Control.MouseFilterEnum.Ignore;
                indicator.FocusMode = Control.FocusModeEnum.None;
            }
        }

        /// <summary>Copies this object's underlying content asset UUID (mesh, sculpt map, or
        /// default-face texture, in that priority order) to the OS clipboard. Re-checks
        /// _canCopyAssetUuid rather than trusting the button's own Disabled state, since a
        /// properties update could race the click. TPV policy compliance (AGENTS.md
        /// Non-negotiable #1): never exposes an asset id for content the local agent doesn't both
        /// own and hold full permissions on -- see _canCopyAssetUuid's doc comment.</summary>
        private void OnCopyAssetUuidPressed()
        {
            if (!_canCopyAssetUuid || _currentEntity == null) return;

            var prim = _currentEntity.GetComponent<PrimitiveComponent>();
            if (prim == null) return;

            System.Guid assetId =
                prim.IsMesh && prim.MeshId != System.Guid.Empty ? prim.MeshId :
                prim.IsSculpt && prim.SculptId != System.Guid.Empty ? prim.SculptId :
                prim.TextureId;

            if (assetId == System.Guid.Empty) return;

            DisplayServer.ClipboardSet(assetId.ToString());

            // Brief inline confirmation -- this window has no toast/notification mechanism, so
            // flash the button's own label instead of adding one just for this.
            var original = _copyAssetUuidBtn.Text;
            _copyAssetUuidBtn.Text = "Copied!";
            GetTree().CreateTimer(1.2).Timeout += () =>
            {
                if (IsInstanceValid(_copyAssetUuidBtn)) _copyAssetUuidBtn.Text = original;
            };
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
