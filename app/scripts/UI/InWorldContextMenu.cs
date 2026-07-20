using Godot;
using SLNG.Core;
using SLNG.Core.ECS;
using System;

namespace SLNG.App.UI
{
    public partial class InWorldContextMenu : PanelContainer
    {
        private VBoxContainer _btnContainer = null!;
        public Action<Entity, uint>? OnEditClicked;
        public Action<Entity, uint>? OnTouchClicked;
        public Action<Entity, uint>? OnDeleteClicked;
        public Action<Entity, uint>? OnInspectClicked;

        private Entity? _currentEntity;
        private uint _currentLocalId;

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

            _btnContainer = new VBoxContainer();
            margin.AddChild(_btnContainer);

            AddMenuButton("✏️ Edit", () => OnEditClicked?.Invoke(_currentEntity!, _currentLocalId));
            AddMenuButton("✋ Touch", () => OnTouchClicked?.Invoke(_currentEntity!, _currentLocalId));
            AddMenuButton("🔍 Inspect", () => OnInspectClicked?.Invoke(_currentEntity!, _currentLocalId));
            AddMenuButton("🗑️ Delete", () => OnDeleteClicked?.Invoke(_currentEntity!, _currentLocalId));
        }

        private void AddMenuButton(string text, Action onClick)
        {
            var btn = new Button { Text = text, Flat = true, Alignment = HorizontalAlignment.Left };
            btn.Pressed += () => { onClick(); Hide(); };
            _btnContainer.AddChild(btn);
        }

        public void ShowMenu(Vector2 position, Entity entity, uint localId)
        {
            _currentEntity = entity;
            _currentLocalId = localId;
            Position = position;
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
