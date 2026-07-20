using Godot;
using SLNG.Core;
using SLNG.Core.ECS;
using SLNG.Core.Components;
using SLNG.Net;

namespace SLNG.App.UI
{
    public partial class ObjectEditWindow : SLNGWindow
    {
        private GridSession? _session;
        private World? _world;
        private Entity? _currentEntity;
        private uint _currentLocalId;

        private LineEdit _posX = null!, _posY = null!, _posZ = null!;
        private LineEdit _rotX = null!, _rotY = null!, _rotZ = null!;
        private LineEdit _scaleX = null!, _scaleY = null!, _scaleZ = null!;

        private LineEdit _nameInput = null!, _descInput = null!;
        private Label _creatorLabel = null!, _ownerLabel = null!, _groupLabel = null!;
        private CheckBox _lockedCheck = null!, _physicalCheck = null!, _tempCheck = null!, _phantomCheck = null!;

        public void Initialize(GridSession session, World world)
        {
            _session = session;
            if (_world != null) _world.ComponentUpdated -= OnComponentUpdated;
            _world = world;
            _world.ComponentUpdated += OnComponentUpdated;
        }

        public override void _Ready()
        {
            base._Ready(); // set up SLNGWindow styling
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
            
            _session?.RequestObjectProperties(entity.Id);

            _currentEntity = entity;
            _currentLocalId = localId;

            if (transform != null)
            {
                _posX.Text = transform.Position.X.ToString("F3");
                _posY.Text = transform.Position.Y.ToString("F3");
                _posZ.Text = transform.Position.Z.ToString("F3");

                var q = transform.Rotation;
                var gQuat = new Godot.Quaternion(q.X, q.Y, q.Z, q.W);
                var eulerDeg = gQuat.GetEuler() * (180f / Mathf.Pi);
                _rotX.Text = eulerDeg.X.ToString("F1");
                _rotY.Text = eulerDeg.Y.ToString("F1");
                _rotZ.Text = eulerDeg.Z.ToString("F1");
            }
            
            var primitive = entity.GetComponent<PrimitiveComponent>();
            if (primitive != null)
            {
                _scaleX.Text = primitive.Scale.X.ToString("F3");
                _scaleY.Text = primitive.Scale.Y.ToString("F3");
                _scaleZ.Text = primitive.Scale.Z.ToString("F3");
            }

            var meta = entity.GetComponent<MetadataComponent>();
            string titleName = "Object";
            if (meta != null)
            {
                _nameInput.Text = meta.Name;
                _descInput.Text = meta.Description;
                _creatorLabel.Text = meta.CreatorId == System.Guid.Empty ? "Loading..." : meta.CreatorId.ToString();
                _ownerLabel.Text = meta.OwnerId == System.Guid.Empty ? "Loading..." : meta.OwnerId.ToString();
                _groupLabel.Text = meta.GroupId == System.Guid.Empty ? "Loading..." : meta.GroupId.ToString();
                titleName = string.IsNullOrEmpty(meta.Name) ? "Object" : meta.Name;
            }
            else
            {
                _nameInput.Text = "Object";
                _descInput.Text = "(No Description)";
                _creatorLabel.Text = "Loading...";
                _ownerLabel.Text = "Loading...";
                _groupLabel.Text = "Loading...";
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
                
                // Optimistically update the Godot components locally if desired:
                var comp = _currentEntity.GetComponent<TransformComponent>();
                if (comp != null)
                {
                    comp.Position = pos;
                    comp.Rotation = rot;
                }
                var primComp = _currentEntity.GetComponent<PrimitiveComponent>();
                if (primComp != null)
                {
                    primComp.Scale = scale;
                }
                
                var meta = _currentEntity.GetComponent<MetadataComponent>();
                if (meta != null)
                {
                    meta.Name = _nameInput.Text;
                    meta.Description = _descInput.Text;
                }
            }
        }

        private void CenterWindow()
        {
            var viewportSize = GetViewportRect().Size;
            var windowSize = Size;
            if (windowSize.X < CustomMinimumSize.X) windowSize = CustomMinimumSize;
            
            Position = new Godot.Vector2(
                Mathf.Max(0, (viewportSize.X - windowSize.X) / 2),
                Mathf.Max(0, (viewportSize.Y - windowSize.Y) / 2)
            );
        }

        private void OnComponentUpdated(object? sender, ComponentEventArgs e)
        {
            if (_currentEntity != null && e.Entity.Id == _currentEntity.Id && e.Component is MetadataComponent meta)
            {
                CallDeferred(MethodName.UpdateMetadataUI, meta.Name, meta.Description, meta.CreatorId.ToString(), meta.OwnerId.ToString(), meta.GroupId.ToString());
            }
        }

        private void UpdateMetadataUI(string name, string desc, string creator, string owner, string group)
        {
            if (!_nameInput.HasFocus()) _nameInput.Text = name;
            if (!_descInput.HasFocus()) _descInput.Text = desc;
            _creatorLabel.Text = creator;
            _ownerLabel.Text = owner;
            _groupLabel.Text = group;
            Title = $"Edit: {(string.IsNullOrEmpty(name) ? "Object" : name)}";
        }
    }
}
