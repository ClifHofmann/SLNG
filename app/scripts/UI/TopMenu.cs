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
        public Action? OnTogglePlayTypingAnimation;
        public Action? OnToggleHeadFollowsCamera;
        public Action? OnOpenActiveAnimations;
        /// <summary>FEAT-UI-24: Invoked when the user clicks the location readout to copy the SLURL.</summary>
        public Action<string>? OnCopySlurl;
        /// <summary>FEAT-UI-24: Invoked when the user toggles the top-bar FPS display.</summary>
        public Action<bool>? OnToggleShowFps;
        /// <summary>Invoked when the user toggles the focus marker display from the View menu.</summary>
        public Action<bool>? OnToggleShowFocusMarker;

        private Button _locationBtn = null!;
        private Button _copySlurlBtn = null!;
        private Button _fpsBtn = null!;
        private VSeparator _fpsSep = null!;
        private string? _currentRegion;
        private string? _currentParcel;
        private int _currentX, _currentY, _currentZ;
        private bool _showFps = true;
        private bool _showFocusMarker = false;

        private MenuButton _profileMenuBtn = null!;
        private VSeparator _profileSep = null!;
        private PopupMenu? _graphicsProfileMenu;

        // MVP3-3: Firestorm-parity media control cluster. _audioMuteBtn has no audio source to
        // affect yet -- see MediaSettings' own doc comment -- kept purely so the two-icon look
        // doesn't need a second pass once one exists.
        private Button _mediaAutoLoadBtn = null!;
        private Button _audioMuteBtn = null!;
        private VSeparator _mediaSep = null!;
        private GraphicsSettings? _graphicsSettings;
        private Action? _applyGraphicsSettings;
        private Action? _onGraphicsSettingsChanged;

        private PopupMenu? _viewMenu;
        private PopupMenu? _avatarMenu;
        private PopupMenu? _holdPoseMenu;
        private PopupMenu? _freezeMenu;
        private bool _freezeSelfChecked;
        private bool _freezeAllChecked;

        private int _lastFps = -1;

        private Label _balanceLabel = null!;
        private VSeparator _balanceSep = null!;

        /// <summary>FEAT-ECON-01: the L$ balance readout.</summary>
        /// <param name="balance">The simulator's figure.</param>
        /// <param name="known">False until the simulator has answered at all. It stays hidden
        /// until then rather than showing L$ 0 -- to someone with thousands that is not a blank
        /// readout, it is a wrong one.</param>
        public void UpdateBalance(int balance, bool known)
        {
            _balanceLabel.Visible = known;
            _balanceSep.Visible = known;
            if (!known) return;

            // Thousands separators, in the user's own locale: a five-figure balance is otherwise
            // a wall of digits. "L$" leads, as in the reference viewer.
            _balanceLabel.Text = $"L$ {balance:N0}";
        }

        /// <summary>FEAT-UI-24: Updates the location readout in the top bar.</summary>
        public void UpdateLocation(string? regionName, string? parcelName, int x, int y, int z)
        {
            if (string.IsNullOrEmpty(regionName))
            {
                ClearLocation();
                return;
            }
            if (_currentRegion == regionName && _currentParcel == parcelName &&
                _currentX == x && _currentY == y && _currentZ == z && _locationBtn.Visible)
            {
                return;
            }
            _currentRegion = regionName;
            _currentParcel = parcelName;
            _currentX = x;
            _currentY = y;
            _currentZ = z;

            if (!string.IsNullOrWhiteSpace(parcelName) && !string.Equals(parcelName, regionName, StringComparison.OrdinalIgnoreCase))
            {
                _locationBtn.Text = $"📍 {regionName} / {parcelName} ({x}, {y}, {z})";
            }
            else
            {
                _locationBtn.Text = $"📍 {regionName} ({x}, {y}, {z})";
            }
            _locationBtn.Visible = true;
            _copySlurlBtn.Visible = true;
        }

        /// <summary>FEAT-UI-24: Overload for backward compatibility without parcel name.</summary>
        public void UpdateLocation(string? regionName, int x, int y, int z) => UpdateLocation(regionName, null, x, y, z);

        /// <summary>FEAT-UI-24: Hides the location readout.</summary>
        public void ClearLocation()
        {
            _currentRegion = null;
            _currentParcel = null;
            _locationBtn.Visible = false;
            _copySlurlBtn.Visible = false;
        }

        /// <summary>FEAT-UI-24: Sets whether the FPS display in the top bar is enabled.</summary>
        public void SetShowFps(bool show)
        {
            _showFps = show;
            _lastFps = -1;
            if (_viewMenu != null)
            {
                int idx = _viewMenu.GetItemIndex(6);
                if (idx >= 0) _viewMenu.SetItemChecked(idx, show);
            }
            if (!show)
            {
                _fpsBtn.Visible = false;
                _fpsSep.Visible = false;
            }
        }

        /// <summary>Sets whether the focus marker display in the View menu is checked.</summary>
        public void SetShowFocusMarker(bool show)
        {
            _showFocusMarker = show;
            if (_viewMenu != null)
            {
                int idx = _viewMenu.GetItemIndex(7);
                if (idx >= 0) _viewMenu.SetItemChecked(idx, show);
            }
        }

        /// <summary>FEAT-UI-24: Updates the FPS readout in the top bar.</summary>
        public void UpdateFps(int fps)
        {
            if (!_showFps)
            {
                _fpsBtn.Visible = false;
                _fpsSep.Visible = false;
                _lastFps = -1;
                return;
            }
            if (fps == _lastFps && _fpsBtn.Visible)
            {
                return;
            }
            _lastFps = fps;
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

        private void CopyCurrentLocationSlurl()
        {
            if (!string.IsNullOrEmpty(_currentRegion))
            {
                string slurl = $"secondlife://{Uri.EscapeDataString(_currentRegion)}/{_currentX}/{_currentY}/{_currentZ}";
                DisplayServer.ClipboardSet(slurl);
                OnCopySlurl?.Invoke(slurl);
            }
        }

        /// <summary>Updates the checked state of the Always Run menu item.</summary>
        public void SetAlwaysRunUI(bool run)
        {
            if (_avatarMenu == null) return;
            int idx = _avatarMenu.GetItemIndex(10);
            if (idx >= 0) _avatarMenu.SetItemChecked(idx, run);
        }

        /// <summary>FEAT-ANIM-10: Updates the checked state of the Play Typing Animation menu item.</summary>
        public void SetPlayTypingAnimationUI(bool on)
        {
            if (_avatarMenu == null) return;
            int idx = _avatarMenu.GetItemIndex(12);
            if (idx >= 0) _avatarMenu.SetItemChecked(idx, on);
        }

        /// <summary>FEAT-ANIM-10: Updates the checked state of the Head Follows Camera menu item.</summary>
        public void SetHeadFollowsCameraUI(bool on)
        {
            if (_avatarMenu == null) return;
            int idx = _avatarMenu.GetItemIndex(13);
            if (idx >= 0) _avatarMenu.SetItemChecked(idx, on);
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
            hbox.AddThemeConstantOverride("separation", 6);
            margin.AddChild(hbox);

            var menuBar = new MenuBar();
            menuBar.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
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
            _locationBtn.Pressed += CopyCurrentLocationSlurl;
            hbox.AddChild(_locationBtn);

            _copySlurlBtn = new Button
            {
                Text = "📋",
                Flat = true,
                Visible = false,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = L10n.Tr("ui.topmenu.copy_slurl_tooltip"),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
            };
            _copySlurlBtn.AddThemeFontSizeOverride("font_size", 12);
            _copySlurlBtn.AddThemeColorOverride("font_color", new Color(0.7f, 0.85f, 1.0f, 0.8f));
            _copySlurlBtn.AddThemeColorOverride("font_hover_color", new Color(1.0f, 1.0f, 1.0f, 1.0f));
            _copySlurlBtn.Pressed += CopyCurrentLocationSlurl;
            hbox.AddChild(_copySlurlBtn);

            var spacer = new Control
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            hbox.AddChild(spacer);

            _profileMenuBtn = new MenuButton
            {
                Text = "🖥",
                Flat = true,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = L10n.Tr("ui.topmenu.graphics_profile_tooltip"),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                CustomMinimumSize = new Vector2(24, 22)
            };
            _profileMenuBtn.AddThemeFontSizeOverride("font_size", 14);
            _profileMenuBtn.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f, 0.95f));
            _profileMenuBtn.AddThemeColorOverride("font_hover_color", new Color(1.0f, 1.0f, 1.0f, 1.0f));
            _profileMenuBtn.GetPopup().IdPressed += id => OnProfileMenuItemSelected((int)id);
            _profileMenuBtn.GetPopup().AboutToPopup += RefreshGraphicsProfilesUI;
            hbox.AddChild(_profileMenuBtn);

            _profileSep = new VSeparator();
            _profileSep.AddThemeConstantOverride("separation", 6);
            hbox.AddChild(_profileSep);

            // MVP3-3: media auto-load toggle (Firestorm's own media/audio icon cluster). The
            // resident's kill switch for MOAP's IP-disclosure risk -- see MediaSettings.
            _mediaAutoLoadBtn = new Button
            {
                Flat = true,
                FocusMode = Control.FocusModeEnum.None,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                CustomMinimumSize = new Vector2(24, 22)
            };
            _mediaAutoLoadBtn.AddThemeFontSizeOverride("font_size", 14);
            _mediaAutoLoadBtn.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f, 0.95f));
            _mediaAutoLoadBtn.AddThemeColorOverride("font_hover_color", new Color(1.0f, 1.0f, 1.0f, 1.0f));
            _mediaAutoLoadBtn.Pressed += () =>
            {
                MediaSettings.SetAutoLoadEnabled(!MediaSettings.AutoLoadEnabled);
                RefreshMediaButtons();
            };
            hbox.AddChild(_mediaAutoLoadBtn);

            // Placeholder: no audio source exists yet (see MediaSettings), added now purely so
            // the two-icon cluster matches Firestorm's without a second UI pass once one does.
            _audioMuteBtn = new Button
            {
                Flat = true,
                FocusMode = Control.FocusModeEnum.None,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                CustomMinimumSize = new Vector2(24, 22)
            };
            _audioMuteBtn.AddThemeFontSizeOverride("font_size", 14);
            _audioMuteBtn.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f, 0.95f));
            _audioMuteBtn.AddThemeColorOverride("font_hover_color", new Color(1.0f, 1.0f, 1.0f, 1.0f));
            _audioMuteBtn.Pressed += () =>
            {
                MediaSettings.SetAudioMuted(!MediaSettings.AudioMuted);
                RefreshMediaButtons();
            };
            hbox.AddChild(_audioMuteBtn);
            RefreshMediaButtons();
            MediaSettings.Changed += RefreshMediaButtons;

            _mediaSep = new VSeparator();
            _mediaSep.AddThemeConstantOverride("separation", 6);
            hbox.AddChild(_mediaSep);

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

            // FEAT-ECON-01: left of the version, right of the FPS readout -- the corner the
            // reference viewer keeps money in.
            _balanceLabel = new Label
            {
                Visible = false,
                VerticalAlignment = VerticalAlignment.Center,
                TooltipText = L10n.Tr("ui.topmenu.balance_tooltip"),
            };
            _balanceLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.45f, 0.95f));
            _balanceLabel.AddThemeFontSizeOverride("font_size", 13);
            hbox.AddChild(_balanceLabel);

            _balanceSep = new VSeparator { Visible = false };
            _balanceSep.AddThemeConstantOverride("separation", 6);
            hbox.AddChild(_balanceSep);

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
            _viewMenu = viewMenu;
            viewMenu.Name = L10n.Tr("ui.menu.view");
            viewMenu.AddItem(L10n.Tr("ui.menu.toggle_hud"), 0);
            viewMenu.AddItem(L10n.Tr("ui.menu.camera_controls"), 4);
            viewMenu.AddItem(L10n.Tr("ui.menu.performance_stats"), 5);
            viewMenu.AddCheckItem(L10n.Tr("ui.menu.show_fps_in_top_bar"), 6);
            int fpsCheckIdx = viewMenu.GetItemIndex(6);
            if (fpsCheckIdx >= 0) viewMenu.SetItemChecked(fpsCheckIdx, _showFps);
            viewMenu.AddCheckItem(L10n.Tr("ui.menu.show_focus_marker"), 7);
            int focusCheckIdx = viewMenu.GetItemIndex(7);
            if (focusCheckIdx >= 0) viewMenu.SetItemChecked(focusCheckIdx, _showFocusMarker);
            _graphicsProfileMenu = new PopupMenu();
            _graphicsProfileMenu.Name = "GraphicsProfileMenu";
            _graphicsProfileMenu.IdPressed += id => OnProfileMenuItemSelected((int)id);
            _graphicsProfileMenu.AboutToPopup += RefreshGraphicsProfilesUI;
            viewMenu.AddChild(_graphicsProfileMenu);
            viewMenu.AddSubmenuNodeItem(L10n.Tr("ui.menu.graphics_profiles"), _graphicsProfileMenu, 8);
            viewMenu.AddSeparator();
            viewMenu.AddItem(L10n.Tr("ui.menu.first_person"), 1);
            viewMenu.AddItem(L10n.Tr("ui.menu.third_person"), 2);
            viewMenu.AddItem(L10n.Tr("ui.menu.free_camera"), 3);
            viewMenu.IdPressed += (id) => {
                if (id == 0) OnToggleHud?.Invoke();
                if (id == 4) OnToggleCameraHud?.Invoke();
                if (id == 5) OnToggleStats?.Invoke();
                if (id == 6)
                {
                    SetShowFps(!_showFps);
                    OnToggleShowFps?.Invoke(_showFps);
                }
                if (id == 7)
                {
                    SetShowFocusMarker(!_showFocusMarker);
                    OnToggleShowFocusMarker?.Invoke(_showFocusMarker);
                }
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
            avatarMenu.AddCheckItem(L10n.Tr("ui.menu.play_typing_animation"), 12);
            avatarMenu.AddCheckItem(L10n.Tr("ui.menu.head_follows_camera"), 13);
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
                else if (id == 12) OnTogglePlayTypingAnimation?.Invoke();
                else if (id == 13) OnToggleHeadFollowsCamera?.Invoke();
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

        public void InitializeGraphicsProfiles(GraphicsSettings settings, Action applySettings, Action? onSettingsChanged = null)
        {
            if (_graphicsSettings != null)
            {
                _graphicsSettings.Changed -= RefreshGraphicsProfilesUI;
            }
            _graphicsSettings = settings;
            _applyGraphicsSettings = applySettings;
            _onGraphicsSettingsChanged = onSettingsChanged;
            _graphicsSettings.Changed += RefreshGraphicsProfilesUI;
            RefreshGraphicsProfilesUI();
        }

        public override void _ExitTree()
        {
            if (_graphicsSettings != null)
            {
                _graphicsSettings.Changed -= RefreshGraphicsProfilesUI;
            }
            MediaSettings.Changed -= RefreshMediaButtons;
            base._ExitTree();
        }

        /// <summary>MVP3-3: reflects MediaSettings' current state onto the two icon buttons.</summary>
        private void RefreshMediaButtons()
        {
            _mediaAutoLoadBtn.Text = MediaSettings.AutoLoadEnabled ? "▶" : "⏸";
            _mediaAutoLoadBtn.TooltipText = L10n.Tr(MediaSettings.AutoLoadEnabled
                ? "ui.topmenu.media_autoload_on_tooltip" : "ui.topmenu.media_autoload_off_tooltip");

            _audioMuteBtn.Text = MediaSettings.AudioMuted ? "🔇" : "🔊";
            _audioMuteBtn.TooltipText = L10n.Tr(MediaSettings.AudioMuted
                ? "ui.topmenu.audio_muted_tooltip" : "ui.topmenu.audio_unmuted_tooltip");
        }

        public void RefreshGraphicsProfilesUI()
        {
            if (_graphicsSettings == null) return;

            string activeName;
            if (!string.IsNullOrEmpty(_graphicsSettings.CurrentProfileName))
            {
                activeName = _graphicsSettings.CurrentProfileName;
            }
            else
            {
                var preset = _graphicsSettings.DetectPreset();
                activeName = preset switch
                {
                    GraphicsPreset.Low => L10n.Tr("ui.preferences.preset_low"),
                    GraphicsPreset.Medium => L10n.Tr("ui.preferences.preset_medium"),
                    GraphicsPreset.High => L10n.Tr("ui.preferences.preset_high"),
                    GraphicsPreset.Ultra => L10n.Tr("ui.preferences.preset_ultra"),
                    _ => L10n.Tr("ui.preferences.preset_custom")
                };
            }

            if (_profileMenuBtn != null && GodotObject.IsInstanceValid(_profileMenuBtn))
            {
                _profileMenuBtn.Text = "🖥";
                _profileMenuBtn.TooltipText = $"{L10n.Tr("ui.topmenu.graphics_profile_tooltip")}: {activeName}";
                PopulateProfileMenu(_profileMenuBtn.GetPopup());
            }

            if (_graphicsProfileMenu != null && GodotObject.IsInstanceValid(_graphicsProfileMenu))
            {
                PopulateProfileMenu(_graphicsProfileMenu);
            }
        }

        private void PopulateProfileMenu(PopupMenu menu)
        {
            menu.Clear();
            if (_graphicsSettings == null) return;

            var preset = _graphicsSettings.DetectPreset();
            string? curProfile = _graphicsSettings.CurrentProfileName;

            // Presets
            menu.AddRadioCheckItem(L10n.Tr("ui.preferences.preset_low"), 0);
            menu.SetItemChecked(0, curProfile == null && preset == GraphicsPreset.Low);

            menu.AddRadioCheckItem(L10n.Tr("ui.preferences.preset_medium"), 1);
            menu.SetItemChecked(1, curProfile == null && preset == GraphicsPreset.Medium);

            menu.AddRadioCheckItem(L10n.Tr("ui.preferences.preset_high"), 2);
            menu.SetItemChecked(2, curProfile == null && preset == GraphicsPreset.High);

            menu.AddRadioCheckItem(L10n.Tr("ui.preferences.preset_ultra"), 3);
            menu.SetItemChecked(3, curProfile == null && preset == GraphicsPreset.Ultra);

            menu.AddSeparator();

            // Custom profiles
            var customNames = GraphicsSettings.GetProfileNames();
            if (customNames.Length > 0)
            {
                for (int i = 0; i < customNames.Length; i++)
                {
                    int id = 100 + i;
                    menu.AddRadioCheckItem(customNames[i], id);
                    int idx = menu.GetItemIndex(id);
                    if (idx >= 0)
                    {
                        menu.SetItemChecked(idx, string.Equals(curProfile, customNames[i], StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
            else
            {
                menu.AddItem(L10n.Tr("ui.menu.no_custom_profiles"), 999);
                int noProfIdx = menu.GetItemIndex(999);
                if (noProfIdx >= 0) menu.SetItemDisabled(noProfIdx, true);
            }

            menu.AddSeparator();
            menu.AddItem(L10n.Tr("ui.preferences.tab_graphics") + "...", 900);
        }

        private void OnProfileMenuItemSelected(int id)
        {
            if (_graphicsSettings == null) return;
            if (id >= 0 && id <= 3)
            {
                var preset = (GraphicsPreset)id;
                _graphicsSettings.ApplyPreset(preset);
                _applyGraphicsSettings?.Invoke();
                _onGraphicsSettingsChanged?.Invoke();
                RefreshGraphicsProfilesUI();
            }
            else if (id >= 100 && id < 900)
            {
                var names = GraphicsSettings.GetProfileNames();
                int idx = id - 100;
                if (idx >= 0 && idx < names.Length)
                {
                    string name = names[idx];
                    if (_graphicsSettings.LoadProfile(name))
                    {
                        _applyGraphicsSettings?.Invoke();
                        _onGraphicsSettingsChanged?.Invoke();
                        RefreshGraphicsProfilesUI();
                    }
                }
            }
            else if (id == 900)
            {
                OnOpenPreferences?.Invoke();
            }
        }
    }
}
