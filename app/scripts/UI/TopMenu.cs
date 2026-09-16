using Godot;
using System;
using SLNG.Core.Avatars;

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
        /// <summary>FEAT-SL-01: opens the About window the TPV Policy §1.g requires.</summary>
        public Action? OnOpenAbout;
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
        /// <summary>FEAT-AVATAR-03: opens the Hover Height window.</summary>
        public Action? OnOpenHoverHeight;
        /// <summary>FEAT-AVATAR-02 / FEAT-ANIM-04: halts all active animations on self avatar.</summary>
        public Action? OnStopAnimations;
        /// <summary>FEAT-AVATAR-02 / FEAT-ANIM-04: undeforms self avatar and resets bone rest transforms.</summary>
        public Action? OnResetSkeleton;
        /// <summary>FEAT-ANIM-04: resynchronizes active looping animations for self avatar.</summary>
        public Action? OnResyncAnimations;
        /// <summary>FEAT-ANIM-04: resynchronizes active looping animations for all avatars in region.</summary>
        public Action? OnResyncAllAnimations;
        /// <summary>FEAT-ANIM-07: sets avatar hold mode (None, BindPose, PoseStand).</summary>
        public Action<AvatarHoldMode>? OnHoldPoseChanged;
        /// <summary>FEAT-ANIM-09: toggles freezing playback of self avatar.</summary>
        public Action<bool>? OnFreezeSelfChanged;
        /// <summary>FEAT-ANIM-09: toggles freezing playback across all avatars.</summary>
        public Action<bool>? OnFreezeAllChanged;
        /// <summary>FEAT-ANIM-09: steps playback by delta frames (e.g. +1 or -1).</summary>
        public Action<int>? OnStepFrameRequested;
        public Action? OnCreateTestSkin;
        public Action? OnBakeTestPattern;
        public Action? OnToggleAlwaysRun;
        public Action? OnOpenActiveAnimations;
        /// <summary>FEAT-UI-24: Invoked when the user clicks the location readout to copy the SLURL.</summary>
        public Action<string>? OnCopySlurl;

        private Button _locationBtn = null!;
        private VSeparator _locationSep = null!;
        private Button _fpsBtn = null!;
        private VSeparator _fpsSep = null!;
        private string? _currentRegion;
        private int _currentX, _currentY, _currentZ;

        private PopupMenu? _avatarMenu;
        private PopupMenu? _holdPoseMenu;
        private PopupMenu? _freezeMenu;
        private bool _freezeSelfChecked;
        private bool _freezeAllChecked;

        /// <summary>FEAT-UI-24: Updates the location readout in the top bar.</summary>
        public void UpdateLocation(string? regionName, int x, int y, int z)
        {
            if (string.IsNullOrEmpty(regionName))
            {
                ClearLocation();
                return;
            }
            _currentRegion = regionName;
            _currentX = x;
            _currentY = y;
            _currentZ = z;
            _locationBtn.Text = $"📍 {regionName} ({x}, {y}, {z})";
            _locationBtn.Visible = true;
            _locationSep.Visible = true;
        }

        /// <summary>FEAT-UI-24: Hides the location readout.</summary>
        public void ClearLocation()
        {
            _currentRegion = null;
            _locationBtn.Visible = false;
            _locationSep.Visible = false;
        }

        /// <summary>FEAT-UI-24: Updates the FPS readout in the top bar.</summary>
        public void UpdateFps(int fps)
        {
            _fpsBtn.Text = $"{fps} FPS";
            _fpsBtn.Visible = true;
            _fpsSep.Visible = true;
            if (fps >= 50)
                _fpsBtn.AddThemeColorOverride("font_color", new Color(0.5f, 0.95f, 0.6f, 0.85f));
            else if (fps >= 25)
                _fpsBtn.AddThemeColorOverride("font_color", new Color(1.0f, 0.82f, 0.35f, 0.85f));
            else
                _fpsBtn.AddThemeColorOverride("font_color", new Color(1.0f, 0.45f, 0.4f, 0.85f));
        }

        /// <summary>Updates the checked state of the Always Run menu item.</summary>
        public void SetAlwaysRunUI(bool run)
        {
            if (_avatarMenu == null) return;
            int idx = _avatarMenu.GetItemIndex(10);
            if (idx >= 0) _avatarMenu.SetItemChecked(idx, run);
        }

        /// <summary>Updates the checked state of the Hold Pose submenu items.</summary>
        public void SetHoldModeUI(AvatarHoldMode mode)
        {
            if (_holdPoseMenu == null) return;
            for (int i = 0; i < 3; i++)
            {
                _holdPoseMenu.SetItemChecked(i, i == (int)mode);
            }
        }

        /// <summary>FEAT-ANIM-09: Updates the checked state of the Freeze submenu items.</summary>
        public void SetFreezeUI(bool selfFrozen, bool allFrozen)
        {
            _freezeSelfChecked = selfFrozen;
            _freezeAllChecked = allFrozen;
            if (_freezeMenu == null) return;
            _freezeMenu.SetItemChecked(0, selfFrozen);
            _freezeMenu.SetItemChecked(1, allFrozen);
        }

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
            hbox.AddThemeConstantOverride("separation", 10);
            margin.AddChild(hbox);

            var menuBar = new MenuBar();
            menuBar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            hbox.AddChild(menuBar);

            _locationBtn = new Button
            {
                Flat = true,
                Visible = false,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = L10n.Tr("ui.topmenu.copy_slurl_tooltip"),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
            };
            _locationBtn.AddThemeFontSizeOverride("font_size", 13);
            _locationBtn.AddThemeColorOverride("font_color", new Color(0.85f, 0.92f, 1.0f, 0.9f));
            _locationBtn.AddThemeColorOverride("font_hover_color", new Color(1.0f, 1.0f, 1.0f, 1.0f));
            _locationBtn.AddThemeColorOverride("font_pressed_color", new Color(0.4f, 0.8f, 1.0f, 1.0f));
            _locationBtn.Pressed += () =>
            {
                if (!string.IsNullOrEmpty(_currentRegion))
                {
                    string slurl = $"secondlife://{Uri.EscapeDataString(_currentRegion)}/{_currentX}/{_currentY}/{_currentZ}";
                    DisplayServer.ClipboardSet(slurl);
                    OnCopySlurl?.Invoke(slurl);
                }
            };
            hbox.AddChild(_locationBtn);

            _locationSep = new VSeparator { Visible = false };
            _locationSep.AddThemeConstantOverride("separation", 6);
            hbox.AddChild(_locationSep);

            _fpsBtn = new Button
            {
                Flat = true,
                Visible = false,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = L10n.Tr("ui.topmenu.fps_tooltip"),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
            };
            _fpsBtn.AddThemeFontSizeOverride("font_size", 13);
            _fpsBtn.AddThemeColorOverride("font_color", new Color(0.5f, 0.95f, 0.6f, 0.85f));
            _fpsBtn.AddThemeColorOverride("font_hover_color", new Color(0.7f, 1.0f, 0.8f, 1.0f));
            _fpsBtn.Pressed += () => OnToggleStats?.Invoke();
            hbox.AddChild(_fpsBtn);

            _fpsSep = new VSeparator { Visible = false };
            _fpsSep.AddThemeConstantOverride("separation", 6);
            hbox.AddChild(_fpsSep);

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
            appMenu.AddItem(L10n.Tr("ui.menu.about"), 3);
            appMenu.AddSeparator();
            appMenu.AddItem(L10n.Tr("ui.menu.disconnect"), 0);
            appMenu.AddItem(L10n.Tr("ui.menu.exit"), 1);
            appMenu.IdPressed += (id) => {
                if (id == 0) OnDisconnect?.Invoke();
                if (id == 1) OnExit?.Invoke();
                if (id == 2) OnOpenPreferences?.Invoke();
                if (id == 3) OnOpenAbout?.Invoke();
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
            worldMenu.IdPressed += (id) => {
                if (id == 0) OnCreateLandmark?.Invoke();
                else if (id == 3) OnOpenEnvironment?.Invoke();
                else if (id == 4) OnOpenWorldMap?.Invoke();
                else if (id == 5) OnOpenMinimap?.Invoke();
            };
            menuBar.AddChild(worldMenu);

            // Avatar Menu (FEAT-AVATAR-03) -- shell for FEAT-AVATAR-02's troubleshooting tools too.
            // Rebake and Detach-All moved here from World, where they used to be the only avatar-
            // related entries scattered among unrelated World actions.
            var avatarMenu = new PopupMenu();
            _avatarMenu = avatarMenu;
            avatarMenu.Name = L10n.Tr("ui.menu.avatar");
            avatarMenu.AddItem(L10n.Tr("ui.menu.rebake_avatar"), 0);
            avatarMenu.AddItem(L10n.Tr("ui.menu.hover_height"), 3);
            avatarMenu.AddSeparator();
            avatarMenu.AddCheckItem(L10n.Tr("ui.menu.always_run"), 10);
            avatarMenu.AddSeparator();
            avatarMenu.AddItem(L10n.Tr("ui.menu.stop_animations"), 4);
            avatarMenu.AddItem(L10n.Tr("ui.menu.active_animations"), 11);
            avatarMenu.AddItem(L10n.Tr("ui.menu.reset_skeleton"), 5);
            avatarMenu.AddItem(L10n.Tr("ui.menu.resync_animations"), 6);
            avatarMenu.AddItem(L10n.Tr("ui.menu.resync_all_animations"), 7);
            
            // FEAT-ANIM-07: Hold pose selector (Off / T-Pose / Pose Stand)
            _holdPoseMenu = new PopupMenu();
            _holdPoseMenu.Name = "HoldPoseMenu";
            _holdPoseMenu.AddRadioCheckItem(L10n.Tr("ui.menu.hold_pose_none"), 0);
            _holdPoseMenu.AddRadioCheckItem(L10n.Tr("ui.menu.hold_pose_tpose"), 1);
            _holdPoseMenu.AddRadioCheckItem(L10n.Tr("ui.menu.hold_pose_stand"), 2);
            _holdPoseMenu.SetItemChecked(0, true);
            _holdPoseMenu.IdPressed += (id) => {
                for (int i = 0; i < 3; i++)
                {
                    _holdPoseMenu.SetItemChecked(i, i == id);
                }
                OnHoldPoseChanged?.Invoke((AvatarHoldMode)id);
            };
            avatarMenu.AddChild(_holdPoseMenu);
            avatarMenu.AddSubmenuNodeItem(L10n.Tr("ui.menu.hold_pose"), _holdPoseMenu, 8);

            // FEAT-ANIM-09: Freeze animation submenu
            _freezeMenu = new PopupMenu();
            _freezeMenu.Name = "FreezeMenu";
            _freezeMenu.AddCheckItem(L10n.Tr("ui.menu.freeze_self"), 0);
            _freezeMenu.AddCheckItem(L10n.Tr("ui.menu.freeze_all"), 1);
            _freezeMenu.AddSeparator();
            _freezeMenu.AddItem(L10n.Tr("ui.menu.step_backward"), 2);
            _freezeMenu.AddItem(L10n.Tr("ui.menu.step_forward"), 3);
            _freezeMenu.IdPressed += (id) => {
                if (id == 0)
                {
                    _freezeSelfChecked = !_freezeSelfChecked;
                    _freezeMenu.SetItemChecked(0, _freezeSelfChecked);
                    OnFreezeSelfChanged?.Invoke(_freezeSelfChecked);
                }
                else if (id == 1)
                {
                    _freezeAllChecked = !_freezeAllChecked;
                    _freezeMenu.SetItemChecked(1, _freezeAllChecked);
                    OnFreezeAllChanged?.Invoke(_freezeAllChecked);
                }
                else if (id == 2)
                {
                    OnStepFrameRequested?.Invoke(-1);
                }
                else if (id == 3)
                {
                    OnStepFrameRequested?.Invoke(1);
                }
            };
            avatarMenu.AddChild(_freezeMenu);
            avatarMenu.AddSubmenuNodeItem(L10n.Tr("ui.menu.freeze"), _freezeMenu, 9);

            avatarMenu.AddSeparator();
            avatarMenu.AddItem(L10n.Tr("ui.menu.detach_all_huds"), 1);
            avatarMenu.AddItem(L10n.Tr("ui.menu.detach_all_attachments"), 2);
            avatarMenu.IdPressed += (id) => {
                if (id == 0) OnRebakeAvatar?.Invoke();
                else if (id == 1) OnDetachAttachments?.Invoke(true);
                else if (id == 2) OnDetachAttachments?.Invoke(false);
                else if (id == 3) OnOpenHoverHeight?.Invoke();
                else if (id == 4) OnStopAnimations?.Invoke();
                else if (id == 5) OnResetSkeleton?.Invoke();
                else if (id == 6) OnResyncAnimations?.Invoke();
                else if (id == 7) OnResyncAllAnimations?.Invoke();
                else if (id == 10) OnToggleAlwaysRun?.Invoke();
                else if (id == 11) OnOpenActiveAnimations?.Invoke();
            };
            menuBar.AddChild(avatarMenu);

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
