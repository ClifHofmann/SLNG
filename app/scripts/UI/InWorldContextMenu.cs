using Godot;
using SLNG.Core;
using SLNG.Core.Components;
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
        /// <summary>MVP2-1: right-click "Sit" on an object -- distinct from OnTouchClicked's
        /// grab/de-grab pair.</summary>
        public Action<Entity, uint>? OnSitClicked;

        /// <summary>FEAT-UI-23: take a worn item off. Only ever offered for the local agent's
        /// own attachments.</summary>
        public Action<Entity, uint>? OnDetachClicked;

        /// <summary>FEAT-UI-13: right-click "Profile" / "IM" on an avatar. Guid is the target
        /// agent id, string its best-known display name.</summary>
        public Action<Guid, string>? OnAvatarProfileClicked;
        public Action<Guid, string>? OnAvatarImClicked;
        /// <inheritdoc cref="OnAvatarProfileClicked"/>
        public Action<Guid, string>? OnAvatarOfferTeleportClicked;
        /// <summary>Mute/Unmute is a single toggle button whose label already reflects the
        /// CURRENT state (set by the caller via <see cref="ShowAvatarMenu"/>'s <c>isMuted</c>) --
        /// this fires regardless of which way it's currently pointing, so the handler is
        /// responsible for flipping the actual mute state (see <c>GridSession.IsAvatarMuted</c>/
        /// <c>SetAvatarMuted</c>).</summary>
        public Action<Guid, string>? OnAvatarMuteToggleClicked;

        /// <summary>MVP2-1: right-click "Sit Here" on bare ground (see ShowGroundMenu) -- the
        /// world position that was right-clicked, same one OnCreatePrimClicked receives.</summary>
        public Action<Vector3>? OnSitOnGroundClicked;

        /// <summary>Fired for a ground-context "Create" pick: the world position that was
        /// right-clicked (already Godot-space; caller converts via RenderConfig.FromGodot) and
        /// the chosen basic shape.</summary>
        public Action<Vector3, BasicPrimType>? OnCreatePrimClicked;

        private Entity? _currentEntity;
        private uint _currentLocalId;
        private Vector3 _pendingCreatePosition;
        private Guid _currentAvatarId;
        private string _currentAvatarName = "";

        private VBoxContainer _objectButtons = null!;
        private VBoxContainer _avatarButtons = null!;
        private Button _avatarImButton = null!;
        private Button _avatarTeleportButton = null!;
        private Button _avatarMuteButton = null!;
        private VBoxContainer _createRoot = null!;
        private Button _sitButton = null!, _deleteButton = null!, _detachButton = null!;
        private VBoxContainer _groundOnlyButtons = null!;
        private Button _createHeader = null!;
        private VBoxContainer _createShapes = null!;


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

            // FEAT-UI-13: shown by ShowAvatarMenu instead of the object/create sets.
            _avatarButtons = new VBoxContainer { Visible = false };
            root.AddChild(_avatarButtons);
            AddMenuButton(_avatarButtons, "👤 Profile", () => OnAvatarProfileClicked?.Invoke(_currentAvatarId, _currentAvatarName));
            _avatarImButton = AddMenuButton(_avatarButtons, "💬 IM", () => OnAvatarImClicked?.Invoke(_currentAvatarId, _currentAvatarName));
            _avatarTeleportButton = AddMenuButton(_avatarButtons, "🚀 Offer Teleport", () => OnAvatarOfferTeleportClicked?.Invoke(_currentAvatarId, _currentAvatarName));
            _avatarMuteButton = AddMenuButton(_avatarButtons, "🔇 Mute", () => OnAvatarMuteToggleClicked?.Invoke(_currentAvatarId, _currentAvatarName));


            _objectButtons = new VBoxContainer();
            root.AddChild(_objectButtons);
            AddMenuButton(_objectButtons, "✏️ Edit", () => OnEditClicked?.Invoke(_currentEntity!, _currentLocalId));
            AddMenuButton(_objectButtons, "✋ Touch", () => OnTouchClicked?.Invoke(_currentEntity!, _currentLocalId));
            _sitButton = AddMenuButton(_objectButtons, "🪑 Sit", () => OnSitClicked?.Invoke(_currentEntity!, _currentLocalId));
            AddMenuButton(_objectButtons, "🔍 Inspect", () => OnInspectClicked?.Invoke(_currentEntity!, _currentLocalId));
            _deleteButton = AddMenuButton(_objectButtons, "🗑️ Delete", () => OnDeleteClicked?.Invoke(_currentEntity!, _currentLocalId));
            // FEAT-UI-23: shown instead of Sit and Delete once the object turns out to be worn.
            _detachButton = AddMenuButton(_objectButtons, "👜 Detach",
                () => OnDetachClicked?.Invoke(_currentEntity!, _currentLocalId));

            // MVP2-1: ground sit. Its own block, because it is the one entry that only makes
            // sense on bare ground -- the object menu has its own Sit.
            _groundOnlyButtons = new VBoxContainer { Visible = false };
            root.AddChild(_groundOnlyButtons);
            AddMenuButton(_groundOnlyButtons, "🪑 Sit Here", () => OnSitOnGroundClicked?.Invoke(_pendingCreatePosition));

            // MVP4-1: "Create" expands into the basic-shape list rather than dumping all 7 shapes
            // into the menu. Material/torus-etc. fine-tuning happens afterward in the
            // Build/Inspector window like any other object.
            //
            // Shown for an OBJECT as well as for bare ground: the reference viewer lets you rez
            // onto whatever surface you right-clicked, and offering it only on terrain meant you
            // could not build on top of anything you had already built -- reported in-world.
            _createRoot = new VBoxContainer { Visible = false };
            root.AddChild(_createRoot);
            _createRoot.AddChild(new HSeparator());

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
                // Seven more entries appear here, which is exactly when the menu runs off the
                // bottom of the screen.
                CallDeferred(nameof(ClampIntoViewport));
            };
            AddMenuButton(_createShapes, "📦 Box", () => OnCreatePrimClicked?.Invoke(_pendingCreatePosition, BasicPrimType.Box));
            AddMenuButton(_createShapes, "🔵 Sphere", () => OnCreatePrimClicked?.Invoke(_pendingCreatePosition, BasicPrimType.Sphere));
            AddMenuButton(_createShapes, "🥫 Cylinder", () => OnCreatePrimClicked?.Invoke(_pendingCreatePosition, BasicPrimType.Cylinder));
            AddMenuButton(_createShapes, "🔺 Prism", () => OnCreatePrimClicked?.Invoke(_pendingCreatePosition, BasicPrimType.Prism));
            AddMenuButton(_createShapes, "🍩 Torus", () => OnCreatePrimClicked?.Invoke(_pendingCreatePosition, BasicPrimType.Torus));
            AddMenuButton(_createShapes, "🛞 Tube", () => OnCreatePrimClicked?.Invoke(_pendingCreatePosition, BasicPrimType.Tube));
            AddMenuButton(_createShapes, "💍 Ring", () => OnCreatePrimClicked?.Invoke(_pendingCreatePosition, BasicPrimType.Ring));
        }

        /// <summary>Keeps the menu on screen. It opens AT the cursor, so a right-click near the
        /// bottom edge pushed the lower half of it off the viewport -- reported in-world with
        /// the shape list cut in two. Slides it back rather than flipping it above the cursor:
        /// the entries then stay where the eye already is.</summary>
        /// <remarks>
        /// Deferred because the size is only known after the layout pass, and the menu changes
        /// height every time it opens -- the object, avatar and ground sets have different
        /// entries, and "Create" expands. GetCombinedMinimumSize is the height it WILL take, so
        /// the first frame is already right; Size alone would still be the previous menu's.
        /// </remarks>
        private void ClampIntoViewport()
        {
            // Let it shrink first. Assigning Position pins the control's rect -- Godot writes it
            // out as offsets -- and from then on the control keeps its width and height instead
            // of following a minimum that has got smaller. So once "Create" had been expanded
            // even once, every later menu stayed as tall as the longest one had been, with the
            // entries at the top and empty panel below. Measured: collapsed 171 px, expanded
            // 416 px, re-opened collapsed still 416 px against a minimum of 171. Assigning zero
            // does not make it zero; Godot clamps the assignment straight back up to the
            // minimum, which is exactly the size wanted here.
            Size = Vector2.Zero;

            var viewport = GetViewportRect().Size;
            var extent = GetCombinedMinimumSize().Max(Size);
            var position = Position;

            if (position.Y + extent.Y > viewport.Y) position.Y = viewport.Y - extent.Y;
            if (position.X + extent.X > viewport.X) position.X = viewport.X - extent.X;
            // Never off the top or the left: a menu taller than the viewport is better cut off
            // at the bottom, where the user can still reach the first entries.
            Position = new Vector2(Mathf.Max(0f, position.X), Mathf.Max(0f, position.Y));
        }

        private Button AddMenuButton(VBoxContainer container, string text, Action onClick)
        {
            var btn = new Button { Text = text, Flat = true, Alignment = HorizontalAlignment.Left };
            btn.Pressed += () => { onClick(); Hide(); };
            container.AddChild(btn);
            return btn;
        }

        /// <param name="createPosition">Where a prim rezzed from this menu should go: the point
        /// on the object's surface that was right-clicked, already lifted clear of it by the
        /// caller.</param>
        public void ShowMenu(Vector2 position, Entity entity, uint localId, Vector3 createPosition)
        {
            _currentEntity = entity;
            _currentLocalId = localId;
            _pendingCreatePosition = createPosition;

            // FEAT-UI-23: a worn item takes Detach in place of Sit and Delete. You cannot sit on
            // something you are wearing, and Delete on a worn item is not what the reference
            // viewer offers there either -- it offers Detach.
            bool isWorn = entity.GetComponent<AttachmentComponent>() != null;
            _detachButton.Visible = isWorn;
            _sitButton.Visible = !isWorn;
            _deleteButton.Visible = !isWorn;

            _objectButtons.Visible = true;
            _avatarButtons.Visible = false;
            _groundOnlyButtons.Visible = false;
            _createRoot.Visible = true;
            _createShapes.Visible = false;
            _createHeader.Text = CreateHeaderCollapsed;
            Position = position;
            Visible = true;
            MoveToFront();
            CallDeferred(nameof(ClampIntoViewport));
        }

        /// <summary>FEAT-UI-13: right-clicked an avatar -- offers Profile / IM / Offer Teleport /
        /// Mute instead of the object Edit/Touch/Inspect set. <paramref name="isSelf"/> hides
        /// everything but Profile (you can't IM/teleport-offer/mute yourself). <paramref
        /// name="isMuted"/> only matters when <paramref name="isSelf"/> is false -- it picks
        /// which way the Mute/Unmute toggle currently reads; the caller (whoever tracks the mute
        /// list, e.g. <c>GridSession.IsAvatarMuted</c>) is the source of truth for it, not this
        /// menu.</summary>
        public void ShowAvatarMenu(Vector2 position, Guid agentId, string name, bool isSelf, bool isMuted = false)
        {
            _currentAvatarId = agentId;
            _currentAvatarName = name ?? "";
            _objectButtons.Visible = false;
            _createRoot.Visible = false;
            _groundOnlyButtons.Visible = false;
            _avatarButtons.Visible = true;
            _avatarImButton.Visible = !isSelf;
            _avatarTeleportButton.Visible = !isSelf;
            _avatarMuteButton.Visible = !isSelf;
            _avatarMuteButton.Text = isMuted ? "🔊 Unmute" : "🔇 Mute";

            Position = position;
            Visible = true;
            MoveToFront();
            CallDeferred(nameof(ClampIntoViewport));
        }

        /// <summary>Right-click on terrain (or anything else without an entity to edit) --
        /// shows the "Create" shape picker instead of Edit/Touch/Inspect/Delete. Always starts
        /// collapsed so repeated right-clicks behave predictably.</summary>
        public void ShowGroundMenu(Vector2 screenPosition, Vector3 worldPosition)
        {
            _pendingCreatePosition = worldPosition;
            _objectButtons.Visible = false;
            _avatarButtons.Visible = false;
            _groundOnlyButtons.Visible = true;
            _createRoot.Visible = true;
            _createShapes.Visible = false;
            _createHeader.Text = CreateHeaderCollapsed;
            Position = screenPosition;
            Visible = true;
            MoveToFront();
            CallDeferred(nameof(ClampIntoViewport));
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
