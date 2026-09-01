using Godot;
using System;

namespace SLNG.App.UI
{
    public partial class TopMenu : CanvasLayer
    {
        public Action? OnDisconnect;
        public Action? OnExit;
        public Action? OnToggleHud;
        public Action? OnToggleCameraHud;
        public Action<int>? OnCameraMode; // 0=First, 1=Third, 2=Free
        public Action? OnToggleWireframe;
        public Action? OnToggleStats;
        public Action? OnMeasureRenderBaseline;
        public Action? OnOpenPreferences;
        public Action? OnCreateLandmark;
        public Action? OnOpenEnvironment;
        // MVP2-3: reachable from World -> World Map / Minimap, not just the bottom toolbar.
        public Action? OnOpenWorldMap;
        public Action? OnOpenMinimap;
        /// <summary>true = HUD-point attachments only, false = every attachment.</summary>
        public Action<bool>? OnDetachAttachments;
        /// <summary>FEAT-AVATAR-01: "Avatar neu backen" (Ctrl+Alt+R), the manual equivalent of the
        /// real viewer's rebake — recomposites the baked textures from the worn set and re-sends the
        /// appearance. The escape hatch when a wearable change did not visibly take.</summary>
        public Action? OnRebakeAvatar;
        public Action? OnCreateTestSkin;
        public Action? OnBakeTestPattern;

        public override void _Ready()
        {
            Layer = 100; // Always on top

            var panel = new PanelContainer();
            panel.SetAnchorsPreset(Control.LayoutPreset.TopWide);
            
            var styleBox = new StyleBoxFlat
            {
                BgColor = new Color(0.05f, 0.08f, 0.12f, 0.85f),
                BorderWidthBottom = 1,
                BorderColor = new Color(0.15f, 0.6f, 0.9f, 0.3f),
                ContentMarginBottom = 2,
                ContentMarginTop = 2
            };
            panel.AddThemeStyleboxOverride("panel", styleBox);
            AddChild(panel);

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 12);
            margin.AddThemeConstantOverride("margin_right", 12);
            panel.AddChild(margin);
            
            var hbox = new HBoxContainer();
            margin.AddChild(hbox);

            var menuBar = new MenuBar();
            menuBar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            hbox.AddChild(menuBar);

            var versionLabel = new Label
            {
                Text = $"Puris Viewer {Boot.AppVersion}",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            versionLabel.AddThemeColorOverride("font_color", new Color(0.4f, 0.8f, 1f, 0.6f));
            versionLabel.AddThemeFontSizeOverride("font_size", 13);
            hbox.AddChild(versionLabel);

            // App Menu
            var appMenu = new PopupMenu();
            appMenu.Name = L10n.Tr("ui.menu.app");
            appMenu.AddItem(L10n.Tr("ui.menu.preferences"), 2);
            appMenu.AddSeparator();
            appMenu.AddItem(L10n.Tr("ui.menu.disconnect"), 0);
            appMenu.AddItem(L10n.Tr("ui.menu.exit"), 1);
            appMenu.IdPressed += (id) => {
                if (id == 0) OnDisconnect?.Invoke();
                if (id == 1) OnExit?.Invoke();
                if (id == 2) OnOpenPreferences?.Invoke();
            };
            menuBar.AddChild(appMenu);

            // View Menu
            var viewMenu = new PopupMenu();
            viewMenu.Name = L10n.Tr("ui.menu.view");
            viewMenu.AddItem(L10n.Tr("ui.menu.toggle_hud"), 0);
            viewMenu.AddItem(L10n.Tr("ui.menu.camera_controls"), 4);
            viewMenu.AddItem(L10n.Tr("ui.menu.performance_stats"), 5);
            viewMenu.AddSeparator();
            viewMenu.AddItem(L10n.Tr("ui.menu.first_person"), 1);
            viewMenu.AddItem(L10n.Tr("ui.menu.third_person"), 2);
            viewMenu.AddItem(L10n.Tr("ui.menu.free_camera"), 3);
            viewMenu.IdPressed += (id) => {
                if (id == 0) OnToggleHud?.Invoke();
                if (id == 4) OnToggleCameraHud?.Invoke();
                if (id == 5) OnToggleStats?.Invoke();
                if (id >= 1 && id <= 3) OnCameraMode?.Invoke((int)id - 1);
            };
            menuBar.AddChild(viewMenu);

            // World Menu
            var worldMenu = new PopupMenu();
            worldMenu.Name = L10n.Tr("ui.menu.world");
            worldMenu.AddItem(L10n.Tr("ui.menu.create_landmark"), 0);
            worldMenu.AddItem(L10n.Tr("ui.menu.environment"), 3);
            worldMenu.AddItem(L10n.Tr("ui.menu.world_map"), 4);
            worldMenu.AddItem(L10n.Tr("ui.menu.minimap"), 5);
            worldMenu.AddSeparator();
            worldMenu.AddItem(L10n.Tr("ui.menu.rebake_avatar"), 6);
            worldMenu.AddSeparator();
            worldMenu.AddItem(L10n.Tr("ui.menu.detach_all_huds"), 1);
            worldMenu.AddItem(L10n.Tr("ui.menu.detach_all_attachments"), 2);
            worldMenu.IdPressed += (id) => {
                if (id == 0) OnCreateLandmark?.Invoke();
                else if (id == 3) OnOpenEnvironment?.Invoke();
                else if (id == 4) OnOpenWorldMap?.Invoke();
                else if (id == 5) OnOpenMinimap?.Invoke();
                else if (id == 6) OnRebakeAvatar?.Invoke();
                else if (id == 1) OnDetachAttachments?.Invoke(true);
                else if (id == 2) OnDetachAttachments?.Invoke(false);
            };
            menuBar.AddChild(worldMenu);

            // Developer Menu
            var devMenu = new PopupMenu();
            devMenu.Name = L10n.Tr("ui.menu.developer");
            devMenu.AddItem(L10n.Tr("ui.menu.toggle_wireframe"), 0);
            devMenu.AddItem(L10n.Tr("ui.menu.measure_render_baseline"), 1);
            devMenu.AddItem(L10n.Tr("ui.menu.create_test_skin"), 2);
            devMenu.AddItem(L10n.Tr("ui.menu.bake_test_pattern"), 3);
            devMenu.IdPressed += (id) => {
                if (id == 2) OnCreateTestSkin?.Invoke();
                else if (id == 3) OnBakeTestPattern?.Invoke();
                else if (id == 0) OnToggleWireframe?.Invoke();
                else if (id == 1) OnMeasureRenderBaseline?.Invoke();
            };
            menuBar.AddChild(devMenu);
        }
    }
}
