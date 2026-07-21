using Godot;
using SLNG.Core;
using SLNG.Core.ECS;
using System;

namespace SLNG.App.UI
{
    public partial class InWorldContextMenu : PanelContainer
    {
        public Action<Entity, uint>? OnEditClicked;
        public Action<Entity, uint>? OnTouchClicked;
        public Action<Entity, uint>? OnDeleteClicked;
        public Action<Entity, uint>? OnInspectClicked;

        /// <summary>Fired for a ground-context "Create" pick: the world position that was
        /// right-clicked (already Godot-space; caller converts via RenderConfig.FromGodot) and
        /// the chosen basic shape.</summary>
        public Action<Vector3, BasicPrimType>? OnCreatePrimClicked;

        private Entity? _currentEntity;
        private uint _currentLocalId;
        private Vector3 _pendingGroundPosition;

        private VBoxContainer _objectButtons = null!;
        private VBoxContainer _createRoot = null!;
        private Button _createHeader = null!;
        private VBoxContainer _createShapes = null!;
        private Button _editPartsToggle = null!;

        private const string EditPartsOffText = "🔗 Edit Parts: Off";
        private const string EditPartsOnText = "🔗 Edit Parts: On";

        private const string CreateHeaderCollapsed = "📦 Create ▶";
        private const string CreateHeaderExpanded = "📦 Create ▼";

        public override void _Ready()
        {
            TopLevel = true;
            Visible = false;

            var styleBox = new StyleBoxFlat
            {
                BgColor = new Color(0, 0, 0, 0.7f),
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8,
                BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
                BorderColor = new Color(1, 1, 1, 0.15f),
                ShadowColor = new Color(0, 0, 0, 0.5f),
                ShadowSize = 8,
                ShadowOffset = new Vector2(0, 4)
            };
            AddThemeStyleboxOverride("panel", styleBox);

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 8);
            margin.AddThemeConstantOverride("margin_right", 8);
            margin.AddThemeConstantOverride("margin_top", 8);
            margin.AddThemeConstantOverride("margin_bottom", 8);
            AddChild(margin);

            var root = new VBoxContainer();
            margin.AddChild(root);

            _objectButtons = new VBoxContainer();
            root.AddChild(_objectButtons);
            AddMenuButton(_objectButtons, "✏️ Edit", () => OnEditClicked?.Invoke(_currentEntity!, _currentLocalId));
            AddMenuButton(_objectButtons, "✋ Touch", () => OnTouchClicked?.Invoke(_currentEntity!, _currentLocalId));
            AddMenuButton(_objectButtons, "🔍 Inspect", () => OnInspectClicked?.Invoke(_currentEntity!, _currentLocalId));
            AddMenuButton(_objectButtons, "🗑️ Delete", () => OnDeleteClicked?.Invoke(_currentEntity!, _currentLocalId));
            _objectButtons.AddChild(new HSeparator());
            // FEAT-UI-06: toggles SelectionSettings.EditLinkedParts. Not a click-to-close action
            // like the buttons above -- stays visible so the user can flip it, check the result,
            // and flip it back without re-navigating the menu each time.
            _editPartsToggle = new Button { Text = EditPartsOffText, Flat = true, Alignment = HorizontalAlignment.Left };
            _editPartsToggle.Pressed += () =>
            {
                SelectionSettings.EditLinkedParts = !SelectionSettings.EditLinkedParts;
                _editPartsToggle.Text = SelectionSettings.EditLinkedParts ? EditPartsOnText : EditPartsOffText;
            };
            _objectButtons.AddChild(_editPartsToggle);

            // Right-clicking empty ground shows this set instead (see ShowGroundMenu): a single
            // "Create" entry that expands into the basic-shape list, rather than dumping all 7
            // shapes directly into the menu. Material/torus-etc. fine-tuning happens afterward in
            // the Build/Inspector window like any other object.
            _createRoot = new VBoxContainer { Visible = false };
            root.AddChild(_createRoot);

            _createHeader = new Button { Text = CreateHeaderCollapsed, Flat = true, Alignment = HorizontalAlignment.Left };
            _createRoot.AddChild(_createHeader);

            var indent = new MarginContainer();
            indent.AddThemeConstantOverride("margin_left", 16);
            _createRoot.AddChild(indent);

            _createShapes = new VBoxContainer { Visible = false };
            indent.AddChild(_createShapes);

            _createHeader.Pressed += () =>
            {
                _createShapes.Visible = !_createShapes.Visible;
                _createHeader.Text = _createShapes.Visible ? CreateHeaderExpanded : CreateHeaderCollapsed;
            };
            AddMenuButton(_createShapes, "📦 Box", () => OnCreatePrimClicked?.Invoke(_pendingGroundPosition, BasicPrimType.Box));
            AddMenuButton(_createShapes, "🔵 Sphere", () => OnCreatePrimClicked?.Invoke(_pendingGroundPosition, BasicPrimType.Sphere));
            AddMenuButton(_createShapes, "🥫 Cylinder", () => OnCreatePrimClicked?.Invoke(_pendingGroundPosition, BasicPrimType.Cylinder));
            AddMenuButton(_createShapes, "🔺 Prism", () => OnCreatePrimClicked?.Invoke(_pendingGroundPosition, BasicPrimType.Prism));
            AddMenuButton(_createShapes, "🍩 Torus", () => OnCreatePrimClicked?.Invoke(_pendingGroundPosition, BasicPrimType.Torus));
            AddMenuButton(_createShapes, "🛞 Tube", () => OnCreatePrimClicked?.Invoke(_pendingGroundPosition, BasicPrimType.Tube));
            AddMenuButton(_createShapes, "💍 Ring", () => OnCreatePrimClicked?.Invoke(_pendingGroundPosition, BasicPrimType.Ring));
        }

        private void AddMenuButton(VBoxContainer container, string text, Action onClick)
        {
            var btn = new Button { Text = text, Flat = true, Alignment = HorizontalAlignment.Left };
            btn.Pressed += () => { onClick(); Hide(); };
            container.AddChild(btn);
        }

        public void ShowMenu(Vector2 position, Entity entity, uint localId)
        {
            _currentEntity = entity;
            _currentLocalId = localId;
            _objectButtons.Visible = true;
            _createRoot.Visible = false;
            _editPartsToggle.Text = SelectionSettings.EditLinkedParts ? EditPartsOnText : EditPartsOffText;
            Position = position;
            Visible = true;
            MoveToFront();
        }

        /// <summary>Right-click on terrain (or anything else without an entity to edit) --
        /// shows the "Create" shape picker instead of Edit/Touch/Inspect/Delete. Always starts
        /// collapsed so repeated right-clicks behave predictably.</summary>
        public void ShowGroundMenu(Vector2 screenPosition, Vector3 worldPosition)
        {
            _pendingGroundPosition = worldPosition;
            _objectButtons.Visible = false;
            _createRoot.Visible = true;
            _createShapes.Visible = false;
            _createHeader.Text = CreateHeaderCollapsed;
            Position = screenPosition;
            Visible = true;
            MoveToFront();
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (Visible && @event is InputEventMouseButton mouseBtn && mouseBtn.Pressed)
            {
                var localMousePos = GetLocalMousePosition();
                if (localMousePos.X < 0 || localMousePos.Y < 0 || localMousePos.X > Size.X || localMousePos.Y > Size.Y)
                {
                    Visible = false;
                }
            }
        }
    }
}
