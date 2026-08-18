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
        public Action? OnMeasureRenderBaseline;

        /// <summary>Diagnostic sculpt-V nudge. The argument is the step in TEXTURE units; 0 means
        /// reset. Lives in the menu rather than on a function key because the useful workflow is
        /// clicking repeatedly while watching the object, and because a key nobody remembers is a
        /// key nobody uses.</summary>
        public Action<float>? OnNudgeSculptV;
        public Action? OnOpenPreferences;
        public Action? OnCreateLandmark;

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
                ContentMarginBottom = 4,
                ContentMarginTop = 4
            };
            panel.AddThemeStyleboxOverride("panel", styleBox);
            AddChild(panel);

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 16);
            margin.AddThemeConstantOverride("margin_right", 16);
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
            viewMenu.AddSeparator();
            viewMenu.AddItem(L10n.Tr("ui.menu.first_person"), 1);
            viewMenu.AddItem(L10n.Tr("ui.menu.third_person"), 2);
            viewMenu.AddItem(L10n.Tr("ui.menu.free_camera"), 3);
            viewMenu.IdPressed += (id) => {
                if (id == 0) OnToggleHud?.Invoke();
                if (id == 4) OnToggleCameraHud?.Invoke();
                if (id >= 1 && id <= 3) OnCameraMode?.Invoke((int)id - 1);
            };
            menuBar.AddChild(viewMenu);

            // World Menu
            var worldMenu = new PopupMenu();
            worldMenu.Name = L10n.Tr("ui.menu.world");
            worldMenu.AddItem(L10n.Tr("ui.menu.create_landmark"), 0);
            worldMenu.IdPressed += (id) => {
                if (id == 0) OnCreateLandmark?.Invoke();
            };
            menuBar.AddChild(worldMenu);

            // Developer Menu
            var devMenu = new PopupMenu();
            devMenu.Name = L10n.Tr("ui.menu.developer");
            devMenu.AddItem(L10n.Tr("ui.menu.toggle_wireframe"), 0);
            devMenu.AddItem(L10n.Tr("ui.menu.measure_render_baseline"), 1);
            devMenu.AddSeparator();
            // One grid row of a typical 129-row sculpt, and a x16 coarse step for finding the
            // ballpark before homing in.
            devMenu.AddItem(L10n.Tr("ui.menu.sculpt_v_nudge_up"), 2);
            devMenu.AddItem(L10n.Tr("ui.menu.sculpt_v_nudge_down"), 3);
            devMenu.AddItem(L10n.Tr("ui.menu.sculpt_v_nudge_up_coarse"), 4);
            devMenu.AddItem(L10n.Tr("ui.menu.sculpt_v_nudge_down_coarse"), 5);
            devMenu.AddItem(L10n.Tr("ui.menu.sculpt_v_nudge_reset"), 6);
            devMenu.IdPressed += (id) => {
                const float step = 1.0f / 128.0f;
                if (id == 0) OnToggleWireframe?.Invoke();
                else if (id == 1) OnMeasureRenderBaseline?.Invoke();
                else if (id == 2) OnNudgeSculptV?.Invoke(step);
                else if (id == 3) OnNudgeSculptV?.Invoke(-step);
                else if (id == 4) OnNudgeSculptV?.Invoke(step * 16f);
                else if (id == 5) OnNudgeSculptV?.Invoke(-step * 16f);
                else if (id == 6) OnNudgeSculptV?.Invoke(0f);
            };
            menuBar.AddChild(devMenu);
        }
    }
}
