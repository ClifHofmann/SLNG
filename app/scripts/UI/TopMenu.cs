using Godot;
using System;

namespace SLNG.App.UI
{
    public partial class TopMenu : CanvasLayer
    {
        public Action? OnDisconnect;
        public Action? OnExit;
        public Action? OnToggleHud;
        public Action<int>? OnCameraMode; // 0=First, 1=Third, 2=Free
        public Action? OnToggleWireframe;

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
                Text = $"SLNG {Boot.AppVersion}",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            versionLabel.AddThemeColorOverride("font_color", new Color(0.4f, 0.8f, 1f, 0.6f));
            versionLabel.AddThemeFontSizeOverride("font_size", 13);
            hbox.AddChild(versionLabel);

            // App Menu
            var appMenu = new PopupMenu();
            appMenu.Name = "App";
            appMenu.AddItem("Disconnect", 0);
            appMenu.AddItem("Exit", 1);
            appMenu.IdPressed += (id) => {
                if (id == 0) OnDisconnect?.Invoke();
                if (id == 1) OnExit?.Invoke();
            };
            menuBar.AddChild(appMenu);

            // View Menu
            var viewMenu = new PopupMenu();
            viewMenu.Name = "View";
            viewMenu.AddItem("Toggle HUD", 0);
            viewMenu.AddSeparator();
            viewMenu.AddItem("First Person", 1);
            viewMenu.AddItem("Third Person", 2);
            viewMenu.AddItem("Free Camera", 3);
            viewMenu.IdPressed += (id) => {
                if (id == 0) OnToggleHud?.Invoke();
                if (id >= 1 && id <= 3) OnCameraMode?.Invoke((int)id - 1);
            };
            menuBar.AddChild(viewMenu);

            // Developer Menu
            var devMenu = new PopupMenu();
            devMenu.Name = "Developer";
            devMenu.AddItem("Toggle Wireframe", 0);
            devMenu.IdPressed += (id) => {
                if (id == 0) OnToggleWireframe?.Invoke();
            };
            menuBar.AddChild(devMenu);
        }
    }
}
