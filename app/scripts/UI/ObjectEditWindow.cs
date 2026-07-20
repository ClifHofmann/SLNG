using Godot;
using SLNG.Core;
using SLNG.Core.ECS;
using SLNG.Core.Components;
using SLNG.Net;

namespace SLNG.App.UI
{
    public partial class ObjectEditWindow : SLNGWindow
    {
        private GridSession _session = null!;
        private Entity? _currentEntity;
        private uint _currentLocalId;

        private LineEdit _posX = null!, _posY = null!, _posZ = null!;
        private LineEdit _rotX = null!, _rotY = null!, _rotZ = null!;
        private LineEdit _scaleX = null!, _scaleY = null!, _scaleZ = null!;

        public void Initialize(GridSession session)
        {
            _session = session;
        }

        public override void _Ready()
        {
            base._Ready(); // set up SLNGWindow styling
            Title = "Build / Inspector";
            Visible = false;
            CustomMinimumSize = new Vector2(320, 400);

            var tabContainer = new TabContainer();
            ContentContainer.AddChild(tabContainer);

            // General Tab (Placeholder)
            var generalTab = new MarginContainer { Name = "General" };
            generalTab.AddChild(new Label { Text = "Object details will appear here." });
            tabContainer.AddChild(generalTab);

            // Object (Transform) Tab
            var objectTab = new MarginContainer { Name = "Object" };
            var transformVBox = new VBoxContainer();
            objectTab.AddChild(transformVBox);
            tabContainer.AddChild(objectTab);

            transformVBox.AddChild(new Label { Text = "Position (X, Y, Z)" });
            var posHBox = new HBoxContainer();
            _posX = new LineEdit { CustomMinimumSize = new Vector2(60, 0) };
            _posY = new LineEdit { CustomMinimumSize = new Vector2(60, 0) };
            _posZ = new LineEdit { CustomMinimumSize = new Vector2(60, 0) };
            posHBox.AddChild(_posX); posHBox.AddChild(_posY); posHBox.AddChild(_posZ);
            transformVBox.AddChild(posHBox);

            transformVBox.AddChild(new Label { Text = "Rotation (Pitch, Roll, Yaw)" });
            var rotHBox = new HBoxContainer();
            _rotX = new LineEdit { CustomMinimumSize = new Vector2(60, 0) };
            _rotY = new LineEdit { CustomMinimumSize = new Vector2(60, 0) };
            _rotZ = new LineEdit { CustomMinimumSize = new Vector2(60, 0) };
            rotHBox.AddChild(_rotX); rotHBox.AddChild(_rotY); rotHBox.AddChild(_rotZ);
            transformVBox.AddChild(rotHBox);

            transformVBox.AddChild(new Label { Text = "Size (X, Y, Z)" });
            var scaleHBox = new HBoxContainer();
            _scaleX = new LineEdit { CustomMinimumSize = new Vector2(60, 0) };
            _scaleY = new LineEdit { CustomMinimumSize = new Vector2(60, 0) };
            _scaleZ = new LineEdit { CustomMinimumSize = new Vector2(60, 0) };
            scaleHBox.AddChild(_scaleX); scaleHBox.AddChild(_scaleY); scaleHBox.AddChild(_scaleZ);
            transformVBox.AddChild(scaleHBox);

            var applyBtn = new Button { Text = "Apply Changes", CustomMinimumSize = new Vector2(0, 30) };
            applyBtn.Pressed += ApplyTransform;
            transformVBox.AddChild(applyBtn);

            // Features Tab (Placeholder)
            var featuresTab = new MarginContainer { Name = "Features" };
            featuresTab.AddChild(new Label { Text = "Prim parameters..." });
            tabContainer.AddChild(featuresTab);

            // Texture Tab (Placeholder)
            var textureTab = new MarginContainer { Name = "Texture" };
            textureTab.AddChild(new Label { Text = "Materials and textures..." });
            tabContainer.AddChild(textureTab);

            // Content Tab (Placeholder)
            var contentTab = new MarginContainer { Name = "Content" };
            contentTab.AddChild(new Label { Text = "Inventory inside object..." });
            tabContainer.AddChild(contentTab);
        }

        public void EditObject(Entity entity, uint localId)
        {
            _currentEntity = entity;
            _currentLocalId = localId;

            var transform = entity.GetComponent<TransformComponent>();
            if (transform != null)
            {
                _posX.Text = transform.Position.X.ToString("F3");
                _posY.Text = transform.Position.Y.ToString("F3");
                _posZ.Text = transform.Position.Z.ToString("F3");

                // Note: Conversion from Quaternion to Euler angles ideally done here.
                // For simplicity in this demo, leaving raw quat or 0s if conversion is complex,
                // but usually rotation is shown as Euler in degrees.
                // In a real SL client, rotation is sent as a Quaternion.
                _rotX.Text = "0"; _rotY.Text = "0"; _rotZ.Text = "0"; // Placeholder
            }
            
            var primitive = entity.GetComponent<PrimitiveComponent>();
            if (primitive != null)
            {
                _scaleX.Text = primitive.Scale.X.ToString("F3");
                _scaleY.Text = primitive.Scale.Y.ToString("F3");
                _scaleZ.Text = primitive.Scale.Z.ToString("F3");
            }

            Title = $"Build / Inspector (LocalID: {localId})";
            Visible = true;
            MoveToFront();
            CallDeferred(MethodName.CenterWindow);
        }

        private void ApplyTransform()
        {
            if (_session == null || _currentEntity == null) return;

            if (float.TryParse(_posX.Text, out float px) &&
                float.TryParse(_posY.Text, out float py) &&
                float.TryParse(_posZ.Text, out float pz) &&
                float.TryParse(_scaleX.Text, out float sx) &&
                float.TryParse(_scaleY.Text, out float sy) &&
                float.TryParse(_scaleZ.Text, out float sz))
            {
                var pos = new System.Numerics.Vector3(px, py, pz);
                var scale = new System.Numerics.Vector3(sx, sy, sz);
                // Simple placeholder for rotation until proper Euler->Quat is added
                var rot = System.Numerics.Quaternion.Identity; 

                _session.UpdateObjectTransform(_currentLocalId, pos, rot, scale);
                
                // Optimistically update the Godot components locally if desired:
                var comp = _currentEntity.GetComponent<TransformComponent>();
                if (comp != null)
                {
                    comp.Position = pos;
                }
                var primComp = _currentEntity.GetComponent<PrimitiveComponent>();
                if (primComp != null)
                {
                    primComp.Scale = scale;
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
    }
}
