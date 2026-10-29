using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SLNG.App.UI.Prefs;
using SLNG.Core.Input;
using SLNG.Core.Settings;

namespace SLNG.App;

/// <summary>
/// FEAT-UI-79 checks: the row builder, the window shell and the wiring of the real window. The builder checks run on a
/// window of their own with <see cref="PrefsState.ConfigPath"/> pointed at a temp file, so nothing here touches the
/// maintainer's real <c>preferences.cfg</c> (the "user data untouched" check runs last and fails the run if it does).
/// </summary>
public static partial class SelfTest
{
    /// <summary>The seven categories, in the order of the plan (docs/specs/FEAT-UI-79-preferences-rework.md).</summary>
    private static readonly string[] PreferenceCategoryIds =
        { "general", "graphics", "camera_movement", "chat_im", "keyboard", "network_cache", "help_info" };

    /// <summary>The row builder and the window shell: ids, order, Advanced fold-out and its remembered state, badges,
    /// the remembered category, and that none of it writes anything just by being built or shown.</summary>
    private static Check CheckPreferencesBuilder(SceneTree tree)
    {
        const string Name = "preferences builder";
        string savedPath = PrefsState.ConfigPath;
        string temp = Path.Combine(Path.GetTempPath(), "slng-selftest-prefs-" + Guid.NewGuid().ToString("N") + ".cfg");
        PrefsState.ConfigPath = temp;
        var windows = new List<UI.PreferencesWindow>();
        try
        {
            var problems = new List<string>();
            void Expect(bool ok, string what) { if (!ok) problems.Add(what); }

            UI.PreferencesWindow NewWindow()
            {
                var w = new UI.PreferencesWindow();
                tree.Root.AddChild(w);
                windows.Add(w);
                return w;
            }
            static void Layout(Control c) { if (c is Container) c.Notification((int)Container.NotificationSortChildren); foreach (var k in c.GetChildren()) if (k is Control kc) Layout(kc); }
            static IEnumerable<Node> Descendants(Node n)
            {
                foreach (var k in n.GetChildren())
                {
                    yield return k;
                    foreach (var d in Descendants(k)) yield return d;
                }
            }

            // --- build a category with every kind of row -----------------------------------------------------------
            var window = NewWindow();
            var alpha = window.AddCategory("alpha", "ui.preferences.cat_general");
            window.AddCategory("beta", "ui.preferences.cat_graphics");
            var one = alpha.AddSection("one", "ui.preferences.sec_general_interface");

            int toggles = 0, sliderCalls = 0, picks = 0;
            double lastSlider = -1;
            bool lastToggle = true;
            var check = one.AddCheck("alpha.one.check", "ui.preferences.always_run", true, on => { toggles++; lastToggle = on; },
                "ui.preferences.always_run_hint");
            var slider = one.AddSlider("alpha.one.slider", "ui.preferences.camera_orbit_speed", 0.1, 3.0, 0.05, 1.0,
                v => $"{Math.Round(v * 100)}%", v => { sliderCalls++; lastSlider = v; });
            var adv = one.AddCheck("alpha.one.adv", "ui.preferences.head_follows_camera", false, _ => { }, advanced: true)
                .WithApply(PrefsApply.Restart).WithKeywords("RenderFoo", " ").DependsOn(check).WithTooltip("ui.preferences.always_run_hint");
            var pick = one.AddOption("alpha.one.option", "ui.preferences.camera_fov", new[] { "A", "B" }, 1, _ => picks++,
                optionKeys: new[] { "ui.preferences.preset_low", "ui.preferences.preset_high" });
            var literal = one.AddOption("alpha.one.literal", "ui.preferences.camera_fov", new[] { "1x", "2x" }, 0, _ => picks++);
            var withControl = one.AddControl("alpha.one.control", "ui.preferences.camera_fov", new Button { Text = "go" });
            var block = one.AddBlock("alpha.one.block", new Label { Text = "block" }, null, false, "ui.preferences.always_run");
            var note = one.AddNote("alpha.one.note", "ui.preferences.always_run_hint");
            var advNote = one.AddNote("alpha.one.advnote", "ui.preferences.always_run_hint", advanced: true);

            Expect(!File.Exists(temp), "building rows wrote the preferences file");
            Expect(toggles == 0 && sliderCalls == 0 && picks == 0, "a control raised its callback while being built");
            Expect(window.AllRows.Select(r => r.Id).SequenceEqual(new[]
                { "alpha.one.check", "alpha.one.slider", "alpha.one.adv", "alpha.one.option", "alpha.one.literal", "alpha.one.control",
                  "alpha.one.block", "alpha.one.note", "alpha.one.advnote" }),
                "AllRows are not the rows in the order they were added");
            Expect(alpha.Rows.All(r => r.CategoryId == "alpha" && r.SectionId == "one"), "a row does not know its category and section");
            Expect(window.Categories.Select(c => c.Id).SequenceEqual(new[] { "alpha", "beta" }), "Categories are not in the order they were added");

            // Advanced fold-out: one per section, collapsed, below every main row.
            var fold = one.AdvancedFold;
            Expect(fold != null && fold.Folded, "the Advanced fold-out is missing or not collapsed by default");
            Expect(fold != null && fold.Title == UI.L10n.Tr("ui.preferences.advanced"), "the Advanced fold-out has no title");
            if (fold != null)
            {
                Expect(note.GetIndex() < fold.GetIndex() && pick.GetIndex() < fold.GetIndex(), "a main row added after an advanced one landed below the fold-out");
                Expect(adv.GetParent()?.GetParent() == fold && advNote.IsAdvanced && adv.IsAdvanced && !check.IsAdvanced, "advanced rows are not inside the fold-out");
            }

            // Metadata.
            Expect(adv.Apply == PrefsApply.Restart && adv.Controller == check && adv.Keywords.SequenceEqual(new[] { "RenderFoo" }),
                "WithApply / DependsOn / WithKeywords were not recorded");
            Expect(adv.TooltipKey == "ui.preferences.always_run_hint" && adv.Control!.TooltipText == UI.L10n.Tr("ui.preferences.always_run_hint"),
                "WithTooltip did not reach the control");
            Expect(Descendants(adv).OfType<Label>().Any(l => l.Text == UI.L10n.Tr("ui.preferences.apply_restart")), "the Restart badge is not shown");
            Expect(!Descendants(check).OfType<Label>().Any(l => l.Text == UI.L10n.Tr("ui.preferences.apply_restart")), "a row without WithApply shows a badge");
            Expect(pick.OptionKeys.Count == 2 && pick.LiteralTexts.Count == 0, "an option row with keys recorded literal texts");
            Expect(literal.OptionKeys.Count == 0 && literal.LiteralTexts.SequenceEqual(new[] { "1x", "2x" }), "an option row without keys did not record its literal texts");
            Expect(check.LabelKey == "ui.preferences.always_run" && check.HintKey == "ui.preferences.always_run_hint", "label / hint keys not recorded");
            Expect(block.SearchKeys.SequenceEqual(new[] { "ui.preferences.always_run" }) && block.Control == null && note.Control == null, "block / note metadata wrong");
            Expect(withControl.Control is Button, "AddControl lost its control");

            // Controls: the one checkbox type, and they report changes once.
            Expect(!Descendants(window).OfType<CheckButton>().Any(), "a CheckButton is in the Preferences window");
            check.ControlAs<CheckBox>()!.ButtonPressed = false;
            Expect(toggles == 1 && !lastToggle, "toggling a checkbox row did not call back exactly once");
            slider.ControlAs<HSlider>()!.Value = 0.5;
            Expect(sliderCalls == 1 && Math.Abs(lastSlider - 0.5) < 1e-6, "moving a slider row did not call back exactly once");
            Expect(Descendants(slider).OfType<Label>().Any(l => l.Text == "50%"), "the slider row's value label did not follow the slider");

            // Disabling a dependent row.
            adv.SetEnabled(false);
            Expect(adv.ControlAs<CheckBox>()!.Disabled, "SetEnabled(false) did not disable the checkbox");
            adv.SetEnabled(true);
            Expect(!adv.ControlAs<CheckBox>()!.Disabled, "SetEnabled(true) did not enable the checkbox");

            // --- the Advanced state is remembered, and written only when it changes --------------------------------
            fold!.Expand();
            Expect(PrefsState.IsAdvancedOpen("alpha", "one"), "opening the fold-out was not remembered");
            Expect(File.Exists(temp), "opening the fold-out did not write the preferences file");
            fold.Fold();
            Expect(!PrefsState.IsAdvancedOpen("alpha", "one"), "closing the fold-out was not remembered");

            // A real click on the title (the property setter does not raise FoldingChanged, only Fold() / Expand() and a
            // click do, and the click is what the person does). The section sits alone at a known place, because the
            // wrapped hint texts of a longer one only reach their height after a frame has passed.
            var host = new Control { Size = new Vector2(600, 400) };
            tree.Root.AddChild(host);
            try
            {
                var clickCategory = new PrefsCategory("click", "ui.preferences.cat_general") { Position = new Vector2(40, 40), Size = new Vector2(500, 300) };
                host.AddChild(clickCategory);
                var clickSection = clickCategory.AddSection("one", null);
                clickSection.AddCheck("click.one.adv", "ui.preferences.always_run", false, _ => { }, advanced: true);
                var clickFold = clickSection.AdvancedFold!;
                for (int pass = 0; pass < 3; pass++) { Layout(host); }
                var at = clickFold.GetGlobalRect().Position + new Vector2(24, 8);
                void Click()
                {
                    foreach (bool pressed in new[] { true, false })
                    {
                        tree.Root.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = at, GlobalPosition = at }, true);
                    }
                }
                Click();
                Expect(!clickFold.Folded && PrefsState.IsAdvancedOpen("click", "one"), $"a click on the Advanced title did not open it and remember that (fold {clickFold.GetGlobalRect()})");
                Click();
                Expect(clickFold.Folded && !PrefsState.IsAdvancedOpen("click", "one"), "a second click on the Advanced title did not close it and remember that");
            }
            finally { host.GetParent()?.RemoveChild(host); host.QueueFree(); }
            PrefsState.SetAdvancedOpen("alpha", "three", true);
            var three = alpha.AddSection("three", null);
            three.AddCheck("alpha.three.adv", "ui.preferences.always_run", false, _ => { }, advanced: true);
            Expect(three.AdvancedFold is { Folded: false }, "a remembered open fold-out was built collapsed");
            string beforeBuild = File.ReadAllText(temp);
            alpha.AddSection("four", null).AddCheck("alpha.four.adv", "ui.preferences.always_run", false, _ => { }, advanced: true);
            Expect(File.ReadAllText(temp) == beforeBuild, "building a section rewrote the preferences file");

            // --- the remembered category ---------------------------------------------------------------------------
            Expect(window.SelectedCategoryId == "alpha", "the first category is not selected to begin with");
            window.SelectCategory("nope");
            Expect(window.SelectedCategoryId == "alpha" && PrefsState.LastCategory == null, "an unknown category changed the selection");
            window.SelectCategory("beta");
            Expect(window.SelectedCategoryId == "beta" && PrefsState.LastCategory == "beta", "SelectCategory did not select and remember");
            Expect(window.Categories.Select(c => ((ScrollContainer)c.GetParent().GetParent()).Visible).SequenceEqual(new[] { false, true }),
                "only the selected category's page should be visible");

            var again = NewWindow();
            again.AddCategory("alpha", "ui.preferences.cat_general");
            again.AddCategory("beta", "ui.preferences.cat_graphics");
            Expect(again.SelectedCategoryId == "alpha", "a fresh window does not start on its first category");
            string beforeShow = File.ReadAllText(temp);
            again.Visible = true;
            Expect(again.SelectedCategoryId == "beta", "the remembered category was not restored when the window was first shown");
            Expect(File.ReadAllText(temp) == beforeShow, "showing the window wrote the preferences file");

            PrefsState.SetLastCategory("gone");
            var fallback = NewWindow();
            fallback.AddCategory("alpha", "ui.preferences.cat_general");
            fallback.AddCategory("beta", "ui.preferences.cat_graphics");
            fallback.Visible = true;
            Expect(fallback.SelectedCategoryId == "alpha", "a remembered category that no longer exists did not fall back to the first");

            // --- the real Camera & movement builders ---------------------------------------------------------------
            var camWindow = NewWindow();
            var cam = camWindow.AddCategory("camera_movement", "ui.preferences.cat_camera_movement");
            var camSection = cam.AddSection("camera", "ui.preferences.sec_camera_movement_camera");
            var moveSection = cam.AddSection("movement", "ui.preferences.sec_camera_movement_movement");
            var avatarSection = cam.AddSection("avatar", "ui.preferences.sec_camera_movement_avatar");
            new UI.CameraPreferencesPage().Build(camSection, new UI.CameraSettings());
            new UI.AnimationPreferencesPage().Build(moveSection, avatarSection, new UI.AnimationSettings());
            string[] camIds =
            {
                "camera_movement.camera.fov", "camera_movement.camera.rear_distance", "camera_movement.camera.focus_height",
                "camera_movement.camera.disable_constraints", "camera_movement.camera.reset", "camera_movement.camera.speed_note",
                "camera_movement.camera.orbit_speed", "camera_movement.camera.pan_speed", "camera_movement.camera.zoom_speed",
                "camera_movement.movement.always_run", "camera_movement.avatar.head_follows_camera",
                "camera_movement.avatar.typing_animation", "camera_movement.avatar.seat_pose_overrides_ao",
            };
            Expect(cam.Rows.Select(r => r.Id).SequenceEqual(camIds), $"Camera & movement rows are {string.Join(",", cam.Rows.Select(r => r.Id))}");
            Expect(cam.Sections.Select(s => s.Id).SequenceEqual(new[] { "camera", "movement", "avatar" }), "Camera & movement sections are wrong");
            Expect(cam.Rows.Where(r => r.IsAdvanced).Select(r => r.Id).SequenceEqual(camIds.Skip(5).Take(4)), "the pad speeds are not exactly the Advanced rows");
            Expect(cam.Rows.First(r => r.Id.EndsWith(".fov")).Keywords.Contains("CameraAngle")
                   && cam.Rows.First(r => r.Id.EndsWith(".disable_constraints")).Keywords.Contains("DisableCameraConstraints")
                   && cam.Rows.First(r => r.Id.EndsWith(".typing_animation")).Keywords.Contains("PlayTypingAnim"), "setting-name keywords missing");
            Expect(cam.Sections[0].AdvancedFold is { Folded: true }, "the camera section's Advanced is not collapsed");
            Expect(cam.Sections[1].AdvancedFold == null && cam.Sections[2].AdvancedFold == null, "a section without advanced rows has a fold-out");

            // Every key the builder was given has a text in both languages.
            var keys = new List<string> { "ui.preferences.advanced", "ui.preferences.apply_restart", "ui.preferences.apply_next_login", "ui.preferences.apply_reopen" };
            keys.Add(cam.TitleKey);
            keys.AddRange(cam.Sections.Select(s => s.TitleKey).OfType<string>());
            foreach (var r in cam.Rows)
            {
                keys.AddRange(new[] { r.LabelKey, r.HintKey, r.TooltipKey }.OfType<string>());
                keys.AddRange(r.SearchKeys);
                keys.AddRange(r.OptionKeys);
            }
            keys.AddRange(PreferenceCategoryIds.Select(id => "ui.preferences.cat_" + id));
            foreach (var locale in new[] { "en-US", "de-DE" })
            {
                using var file = Godot.FileAccess.Open($"res://i18n/{locale}.json", Godot.FileAccess.ModeFlags.Read);
                if (file == null) { problems.Add($"cannot open {locale}"); continue; }
                var manager = new SLNG.Core.Services.LocalizationManager { FallbackLocale = locale, CurrentLocale = locale };
                manager.LoadLocaleFromJson(locale, file.GetAsText());
                var dict = manager.GetDictionaryForLocale(locale);
                var missing = keys.Distinct().Where(k => !dict.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).ToList();
                if (missing.Count > 0) problems.Add($"{locale} missing {missing.Count}: {string.Join(", ", missing.Take(6))}");
            }

            return problems.Count == 0
                ? new Check(Name, true, $"{window.AllRows.Count()} rows of every kind, Advanced collapsed and remembered, category remembered, {camIds.Length} Camera & movement rows, nothing written by building or showing")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex) { return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            PrefsState.ConfigPath = savedPath;
            foreach (var w in windows)
            {
                if (GodotObject.IsInstanceValid(w)) { w.GetParent()?.RemoveChild(w); w.QueueFree(); }
            }
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>The sections of every category, in the order of the plan's table (docs/specs/FEAT-UI-79-preferences-rework.md).</summary>
    private static readonly (string Category, string[] Sections)[] PreferenceSectionPlan =
    {
        ("general", new[] { "interface", "bottom_bar", "names", "content_rating", "building" }),
        ("graphics", new[] { "quality", "view_detail", "shadows", "light_effects", "depth_of_field", "performance" }),
        ("camera_movement", new[] { "camera", "movement", "avatar" }),
        ("chat_im", new[] { "conversations", "friends_online", "away", "chat_logs" }),
        ("keyboard", new[] { "all" }),
        ("network_cache", new[] { "network", "cache" }),
        ("help_info", new[] { "debug_log", "licences" }),
    };

    /// <summary>Every row the Preferences window is meant to hold, by id -- the inventory "preferences complete" holds the
    /// window to. A new setting is added here in the same commit that adds its row (and to the plan's table); a row that is
    /// not listed, or a listed one that is missing, fails the run. Ids are <c>category.section.name</c>.</summary>
    private static readonly string[] ExpectedPreferenceRows =
    {
        // General
        "general.interface.language", "general.interface.ui_scale_auto", "general.interface.ui_scale_detected", "general.interface.ui_scale",
        "general.bottom_bar.intro", "general.bottom_bar.items",
        "general.names.show_display_names", "general.names.show_legacy_names", "general.names.show_group_titles", "general.names.hide_own_group_title",
        "general.content_rating.blurb", "general.content_rating.rating", "general.content_rating.unsupported", "general.content_rating.status",
        "general.building.build_grid", "general.building.rotation_snap",
        // Graphics
        "graphics.quality.preset", "graphics.quality.profiles", "graphics.quality.profile_save", "graphics.quality.profile_status",
        "graphics.view_detail.draw_distance", "graphics.view_detail.object_detail", "graphics.view_detail.max_full_avatars", "graphics.view_detail.small_cull",
        "graphics.shadows.shadows", "graphics.shadows.shadow_resolution", "graphics.shadows.shadow_splits", "graphics.shadows.shadow_softness",
        "graphics.shadows.shadow_distance", "graphics.shadows.shadow_opacity", "graphics.shadows.small_object_shadows",
        "graphics.light_effects.ssao", "graphics.light_effects.glow", "graphics.light_effects.mirrors", "graphics.light_effects.ssil",
        "graphics.light_effects.reflection_probe", "graphics.light_effects.probe_ambient", "graphics.light_effects.ssr", "graphics.light_effects.hero_probe",
        "graphics.depth_of_field.enable", "graphics.depth_of_field.auto_focus", "graphics.depth_of_field.focus_distance", "graphics.depth_of_field.focus_range",
        "graphics.depth_of_field.falloff", "graphics.depth_of_field.blur", "graphics.depth_of_field.near_blur", "graphics.depth_of_field.show_marker",
        "graphics.depth_of_field.reset",
        "graphics.performance.fps_limit", "graphics.performance.vsync", "graphics.performance.msaa", "graphics.performance.texture_memory_auto",
        "graphics.performance.texture_memory", "graphics.performance.texture_memory_usage", "graphics.performance.render_thread",
        "graphics.performance.render_thread_pending", "graphics.performance.render_thread_fellback", "graphics.performance.compress_textures", "graphics.performance.low_vram_caps",
        "graphics.performance.low_vram_state", "graphics.performance.outfit_merge",
        // Camera & movement
        "camera_movement.camera.fov", "camera_movement.camera.rear_distance", "camera_movement.camera.focus_height",
        "camera_movement.camera.disable_constraints", "camera_movement.camera.reset", "camera_movement.camera.speed_note",
        "camera_movement.camera.orbit_speed", "camera_movement.camera.pan_speed", "camera_movement.camera.zoom_speed",
        "camera_movement.movement.always_run",
        "camera_movement.avatar.head_follows_camera", "camera_movement.avatar.typing_animation", "camera_movement.avatar.seat_pose_overrides_ao",
        // Chat & IM
        "chat_im.conversations.conference_as_im",
        "chat_im.friends_online.friend_presence_toasts", "chat_im.friends_online.friend_presence_in_local_chat",
        "chat_im.away.timeout", "chat_im.away.away_on_focus_loss", "chat_im.away.send_away", "chat_im.away.away_text", "chat_im.away.busy_text",
        "chat_im.away.autorespond", "chat_im.away.autorespond_text", "chat_im.away.autorespond_non_friends", "chat_im.away.non_friends_text",
        "chat_im.away.buttons",
        "chat_im.chat_logs.intro", "chat_im.chat_logs.one_viewer", "chat_im.chat_logs.account", "chat_im.chat_logs.folder",
        "chat_im.chat_logs.in_effect", "chat_im.chat_logs.relog_notice", "chat_im.chat_logs.im_names", "chat_im.chat_logs.import",
        // Keyboard
        "keyboard.all.keyboard_page",
        // Network & cache
        "network_cache.network.max_bandwidth", "network_cache.network.check_favorite_regions",
        "network_cache.cache.size", "network_cache.cache.cache_size_limit", "network_cache.cache.decoded_cache_enabled",
        // Help & info
        "help_info.debug_log.enable", "help_info.debug_log.what", "help_info.debug_log.privacy", "help_info.debug_log.limits",
        "help_info.debug_log.folder", "help_info.debug_log.buttons", "help_info.debug_log.status",
        "help_info.licences.notices",
    };

    private static IEnumerable<Node> DescendantNodes(Node n)
    {
        foreach (var k in n.GetChildren())
        {
            yield return k;
            foreach (var d in DescendantNodes(k)) yield return d;
        }
    }

    /// <summary>Every L10n key a row hands the search (and the label, hint and tooltip), with what kind it is.</summary>
    private static IEnumerable<(string Kind, string Key)> PreferenceRowKeys(PrefsRow row)
    {
        if (row.LabelKey != null) yield return ("label", row.LabelKey);
        if (row.HintKey != null) yield return ("hint", row.HintKey);
        if (row.TooltipKey != null) yield return ("tooltip", row.TooltipKey);
        foreach (string key in row.OptionKeys) yield return ("option", key);
        foreach (string key in row.SearchKeys) yield return ("search", key);
    }

    private static UI.PreferencesWindow? FindPreferencesWindow(SceneTree tree)
        => tree.Root.FindChild("PreferencesWindow", true, false) as UI.PreferencesWindow;

    /// <summary>The window the running client built in SetupHud: seven categories in the plan's order with exactly the
    /// plan's sections, row ids that are unique and shaped <c>category.section.name</c>, the badges and Advanced fold-outs
    /// the plan asks for, one checkbox type, and no old page embedded whole -- bar the Keyboard table (and the Licences
    /// notice, should it ever be one).</summary>
    private static Check CheckPreferencesWindowWiring(SceneTree tree)
    {
        const string Name = "preferences window wiring";
        try
        {
            var window = FindPreferencesWindow(tree);
            if (window == null) return new Check(Name, false, "the client has no PreferencesWindow");

            var problems = new List<string>();
            var ids = window.Categories.Select(c => c.Id).ToList();
            if (!ids.SequenceEqual(PreferenceCategoryIds)) problems.Add("categories are " + string.Join(",", ids));
            foreach (var category in window.Categories)
            {
                if (category.Sections.Count == 0) problems.Add($"{category.Id} has no section");
                var plan = PreferenceSectionPlan.FirstOrDefault(p => p.Category == category.Id).Sections;
                var actual = category.Sections.Select(x => x.Id).ToArray();
                if (plan == null || !actual.SequenceEqual(plan))
                    problems.Add($"{category.Id} sections are {string.Join(",", actual)} (plan: {(plan == null ? "none" : string.Join(",", plan))})");

                foreach (var section in category.Sections)
                {
                    if (section.Rows.Count == 0) problems.Add($"{category.Id}.{section.Id} has no row");
                    foreach (var row in section.Rows)
                    {
                        // A block that is a whole old *PreferencesPage Control: only the planned exceptions may still be one.
                        bool wholePage = row.GetChildren().Any(k => k is Control && k.GetType().Name.EndsWith("PreferencesPage", StringComparison.Ordinal));
                        bool allowed = category.Id == "keyboard" || (category.Id == "help_info" && section.Id == "licences");
                        if (wholePage && !allowed) problems.Add($"{row.Id} still embeds a whole old page");
                    }
                }
                foreach (var row in category.Rows)
                {
                    if (!row.Id.StartsWith(category.Id + ".", StringComparison.Ordinal) || row.Id.Split('.').Length != 3)
                        problems.Add($"row id {row.Id} is not category.section.name");
                    if (row.CategoryId != category.Id) problems.Add($"row {row.Id} thinks it is in {row.CategoryId}");
                }
            }
            var dupes = window.AllRows.GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupes.Count > 0) problems.Add("row ids used twice: " + string.Join(", ", dupes));
            if (DescendantNodes(window).OfType<CheckButton>().Any()) problems.Add("a CheckButton is in the Preferences window (CheckBox is the one checkbox type)");

            // Spot checks on what the plan says about when a change applies, where the fold-outs are, and what depends on what.
            PrefsRow? Row(string id) => window.AllRows.FirstOrDefault(r => r.Id == id);
            if (Row("general.interface.language")?.Apply != PrefsApply.ReopenWindows) problems.Add("the language row has no 'reopen windows' badge");
            if (Row("chat_im.chat_logs.folder")?.Apply != PrefsApply.NextLogin) problems.Add("the chat log folder row has no 'next login' badge");
            if (Row("graphics.performance.render_thread")?.Apply != PrefsApply.Restart) problems.Add("the render thread row has no 'restart' badge");
            foreach (string id in new[] { "chat_im.chat_logs", "network_cache.cache", "camera_movement.camera", "graphics.view_detail", "graphics.shadows",
                                          "graphics.light_effects", "graphics.performance" })
            {
                // Collapsed unless the person left it open: the window is built from the real preferences.cfg, so a fold-out that
                // is open there is open here, and that is the remembered state doing its job.
                var section = window.Categories.SelectMany(c => c.Sections).FirstOrDefault(x => $"{x.CategoryId}.{x.Id}" == id);
                if (section?.AdvancedFold == null || section.AdvancedFold.Folded == PrefsState.IsAdvancedOpen(section.CategoryId, section.Id))
                    problems.Add($"{id} has no Advanced fold-out that follows the remembered state");
            }
            foreach (var section in window.Categories.SelectMany(c => c.Sections))
            {
                string id = $"{section.CategoryId}.{section.Id}";
                bool hasAdvancedRows = section.Rows.Any(r => r.IsAdvanced);
                if (hasAdvancedRows != (section.AdvancedFold != null)) problems.Add($"{id}: Advanced fold-out and advanced rows disagree");
            }
            void Depends(string row, string controller)
            {
                if (Row(row)?.Controller?.Id != controller) problems.Add($"{row} does not declare DependsOn({controller})");
            }
            Depends("graphics.light_effects.hero_probe", "graphics.light_effects.mirrors");
            Depends("general.interface.ui_scale", "general.interface.ui_scale_auto");
            Depends("graphics.performance.texture_memory", "graphics.performance.texture_memory_auto");
            foreach (string name in new[] { "shadow_resolution", "shadow_splits", "shadow_softness", "shadow_distance", "shadow_opacity", "small_object_shadows" })
                Depends("graphics.shadows." + name, "graphics.shadows.shadows");
            foreach (string name in new[] { "auto_focus", "focus_distance", "focus_range", "falloff", "blur", "near_blur", "show_marker", "reset" })
                Depends("graphics.depth_of_field." + name, "graphics.depth_of_field.enable");

            return problems.Count == 0
                ? new Check(Name, true, $"{ids.Count} categories with the plan's {PreferenceSectionPlan.Sum(p => p.Sections.Length)} sections, {window.AllRows.Count()} registered rows, ids unique, only the Keyboard table is a block of an old page, no CheckButton")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex) { return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>The Preferences window holds every row of the inventory exactly once and nothing else, in exactly seven
    /// categories, and every text key a row hands the search resolves in en-US AND de-DE (no fallback). What holds later
    /// changes to the plan: a setting must be added here, in the plan's table and in both language files together.</summary>
    private static Check CheckPreferencesComplete(SceneTree tree)
    {
        const string Name = "preferences complete";
        try
        {
            var window = FindPreferencesWindow(tree);
            if (window == null) return new Check(Name, false, "the client has no PreferencesWindow");

            var problems = new List<string>();
            if (window.Categories.Count != PreferenceCategoryIds.Length)
                problems.Add($"{window.Categories.Count} categories, the plan has {PreferenceCategoryIds.Length}: {string.Join(",", window.Categories.Select(c => c.Id))}");

            foreach (var twice in ExpectedPreferenceRows.GroupBy(i => i).Where(g => g.Count() > 1))
                problems.Add($"the inventory lists {twice.Key} twice");
            var registered = window.AllRows.Select(r => r.Id).ToList();
            foreach (string id in ExpectedPreferenceRows.Distinct())
            {
                int count = registered.Count(r => r == id);
                if (count == 0) problems.Add($"row {id} is in the inventory but the window has none");
                else if (count > 1) problems.Add($"row {id} is registered {count} times");
            }
            var expected = new HashSet<string>(ExpectedPreferenceRows);
            foreach (string id in registered.Distinct().Where(r => !expected.Contains(r)))
                problems.Add($"row {id} is registered but not in the inventory");

            foreach (var locale in new[] { "en-US", "de-DE" })
            {
                using var file = Godot.FileAccess.Open($"res://i18n/{locale}.json", Godot.FileAccess.ModeFlags.Read);
                if (file == null) { problems.Add($"cannot open {locale}"); continue; }
                var manager = new SLNG.Core.Services.LocalizationManager { FallbackLocale = locale, CurrentLocale = locale };
                manager.LoadLocaleFromJson(locale, file.GetAsText());
                void Need(string what, string key)
                {
                    if (string.IsNullOrWhiteSpace(manager.GetStringIn(locale, key))) problems.Add($"{locale}: {what} key '{key}' has no text");
                }
                foreach (var category in window.Categories)
                {
                    Need($"category {category.Id} title", category.TitleKey);
                    foreach (var section in category.Sections)
                    {
                        if (section.TitleKey != null) Need($"section {category.Id}.{section.Id} title", section.TitleKey);
                    }
                }
                foreach (var row in window.AllRows)
                {
                    foreach (var (kind, key) in PreferenceRowKeys(row)) Need($"row {row.Id} {kind}", key);
                }
            }

            return problems.Count == 0
                ? new Check(Name, true, $"{registered.Count} rows = the inventory of {expected.Count}, {window.Categories.Count} categories, every key resolves in en-US and de-DE")
                : new Check(Name, false, string.Join("; ", problems.Take(12)) + (problems.Count > 12 ? $"; ... {problems.Count - 12} more" : ""));
        }
        catch (Exception ex) { return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>A row whose switch is off is disabled, not hidden: the real-time mirror probe without Mirrors, the six shadow
    /// rows without Shadows, every depth-of-field control without its Enable. Drives the real Graphics page through
    /// its own checkboxes, on settings objects whose file is a temp file, so nothing of the person's is written; the paths
    /// are put back afterwards.</summary>
    private static Check CheckPreferencesDependents(SceneTree tree)
    {
        const string Name = "preferences dependents";
        string temp = Path.Combine(Path.GetTempPath(), "slng-selftest-graphics-" + Guid.NewGuid().ToString("N") + ".cfg");
        string savedGraphics = UI.GraphicsSettings.ConfigPath, savedDof = UI.DofSettings.ConfigPath, savedPrefs = PrefsState.ConfigPath;
        var host = new Control { Size = new Vector2(640, 800) };
        try
        {
            UI.GraphicsSettings.ConfigPath = UI.DofSettings.ConfigPath = PrefsState.ConfigPath = temp;
            tree.Root.AddChild(host);
            var category = new PrefsCategory("graphics", "ui.preferences.cat_graphics") { Size = new Vector2(640, 800) };
            host.AddChild(category);
            var quality = category.AddSection("quality", null);
            var viewDetail = category.AddSection("view_detail", "ui.preferences.sec_graphics_view_detail");
            var shadows = category.AddSection("shadows", "ui.preferences.sec_graphics_shadows");
            var light = category.AddSection("light_effects", "ui.preferences.sec_graphics_light_effects");
            var dof = category.AddSection("depth_of_field", "ui.preferences.sec_graphics_depth_of_field");
            var perf = category.AddSection("performance", "ui.preferences.sec_graphics_performance");
            new UI.GraphicsPreferencesPage().Build(quality, viewDetail, shadows, light, dof, perf,
                new UI.GraphicsSettings(), () => { }, new UI.DofSettings(), () => { });

            var problems = new List<string>();
            PrefsRow Row(string id) => category.Rows.First(r => r.Id == id);
            static bool Off(PrefsRow row) => row.Control switch
            {
                BaseButton button => button.Disabled,
                Slider slider => !slider.Editable,
                _ => row.GetChildren().OfType<Control>().SelectMany(c => new[] { c }.Concat(DescendantNodes(c).OfType<Control>())).OfType<BaseButton>().Any(b => b.Disabled),
            };

            void Drive(string switchId, string[] dependents, string what)
            {
                var box = Row(switchId).ControlAs<CheckBox>()!;
                foreach (bool on in new[] { true, false, true, false })
                {
                    box.ButtonPressed = !on; // a change of state is what raises the signal: go the other way first
                    box.ButtonPressed = on;
                    foreach (string id in dependents)
                    {
                        var row = Row(id);
                        if (Off(row) == on) problems.Add($"{what} {(on ? "on" : "off")}: {id} is {(Off(row) ? "disabled" : "enabled")}");
                        if (!row.Visible) problems.Add($"{id} is hidden, it should only be disabled");
                        if (row.Controller?.Id != switchId) problems.Add($"{id} does not depend on {switchId}");
                    }
                }
            }

            Drive("graphics.light_effects.mirrors", new[] { "graphics.light_effects.hero_probe" }, "Mirrors");
            Drive("graphics.shadows.shadows", new[]
            {
                "graphics.shadows.shadow_resolution", "graphics.shadows.shadow_splits", "graphics.shadows.shadow_softness", "graphics.shadows.shadow_distance",
                "graphics.shadows.shadow_opacity", "graphics.shadows.small_object_shadows",
            }, "Shadows");
            Drive("graphics.depth_of_field.enable", new[]
            {
                "graphics.depth_of_field.auto_focus", "graphics.depth_of_field.focus_distance", "graphics.depth_of_field.focus_range",
                "graphics.depth_of_field.falloff", "graphics.depth_of_field.blur", "graphics.depth_of_field.near_blur",
                "graphics.depth_of_field.show_marker", "graphics.depth_of_field.reset",
            }, "Depth of field");

            return problems.Count == 0
                ? new Check(Name, true, "the mirror probe follows Mirrors, the six shadow rows follow Shadows, the eight depth-of-field rows follow Enable: disabled, never hidden")
                : new Check(Name, false, string.Join("; ", problems.Take(10)));
        }
        catch (Exception ex) { return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            UI.GraphicsSettings.ConfigPath = savedGraphics;
            UI.DofSettings.ConfigPath = savedDof;
            PrefsState.ConfigPath = savedPrefs;
            if (GodotObject.IsInstanceValid(host)) { host.GetParent()?.RemoveChild(host); host.QueueFree(); }
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>The public value-type and string properties of a settings object, as text: what "the settings object is
    /// unchanged" compares.</summary>
    private static Dictionary<string, string> SnapshotSettings(object settings)
    {
        var snapshot = new Dictionary<string, string>();
        foreach (var property in settings.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
            if (!property.PropertyType.IsValueType && property.PropertyType != typeof(string)) continue;
            snapshot[property.Name] = Convert.ToString(property.GetValue(settings), System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
        return snapshot;
    }

    /// <summary>Looking at the Graphics page must not change a setting. Opening Preferences refreshes the page and
    /// the window shows and hides; with the auto-focus on and off, and with values off the sliders' steps (a slider cannot
    /// show them, and writing its rounded value back used to change them), none of that may touch the settings objects or
    /// the file. Also: "Automatic" for the texture memory on and off again leaves the manual size as it was (the slider
    /// showing the budget used to store it), and one trip through the Profiles menu -- save as, load, delete -- works
    /// against the file and leaves it as it found it. Settings write to a temp file meanwhile.</summary>
    private static Check CheckPreferencesRoundTrips(SceneTree tree)
    {
        const string Name = "preferences round trips";
        string temp = Path.Combine(Path.GetTempPath(), "slng-selftest-roundtrip-" + Guid.NewGuid().ToString("N") + ".cfg");
        string savedGraphics = UI.GraphicsSettings.ConfigPath, savedDof = UI.DofSettings.ConfigPath, savedPrefs = PrefsState.ConfigPath;
        int savedAvatars = RenderConfig.MaxFullyRenderedAvatars; // loading a profile sets it
        UI.PreferencesWindow? window = null;
        try
        {
            UI.GraphicsSettings.ConfigPath = UI.DofSettings.ConfigPath = PrefsState.ConfigPath = temp;
            window = new UI.PreferencesWindow();
            tree.Root.AddChild(window);
            var category = window.AddCategory("graphics", "ui.preferences.cat_graphics");
            var quality = category.AddSection("quality", null);
            var viewDetail = category.AddSection("view_detail", "ui.preferences.sec_graphics_view_detail");
            var shadows = category.AddSection("shadows", "ui.preferences.sec_graphics_shadows");
            var light = category.AddSection("light_effects", "ui.preferences.sec_graphics_light_effects");
            var depth = category.AddSection("depth_of_field", "ui.preferences.sec_graphics_depth_of_field");
            var perf = category.AddSection("performance", "ui.preferences.sec_graphics_performance");

            var settings = new UI.GraphicsSettings();
            var dof = new UI.DofSettings();
            var page = new UI.GraphicsPreferencesPage();
            page.Build(quality, viewDetail, shadows, light, depth, perf, settings, () => { }, dof, () => { }, () => 0, () => 1000L << 20);

            var problems = new List<string>();
            void Expect(bool ok, string what) { if (!ok) problems.Add(what); }
            PrefsRow Row(string id) => category.Rows.First(r => r.Id == id);
            string Text() => File.Exists(temp) ? File.ReadAllText(temp) : "";
            bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);

            // Values a slider cannot show: its steps are 16 m, 0.1, 0.05, 0.125, 128 MB and so on.
            settings.SetDrawDistance(100f);
            settings.SetVolumeLodFactor(2.3f);
            settings.SetShadows(true);
            settings.SetShadowDistance(100f);
            settings.SetShadowBlur(1.37f);
            settings.SetShadowOpacity(0.77f);
            settings.SetTextureMemoryAuto(false);
            settings.SetTextureMemoryMb(3000);
            dof.SetEnabled(true);
            dof.SetFocusDistance(7.33f);
            dof.SetFocusRange(2.57f);
            dof.SetFalloff(0.333f);
            dof.SetBlurAmount(0.137f);

            // --- looking changes nothing ----------------------------------------------------------------------------------
            foreach (bool autoFocus in new[] { true, false })
            {
                dof.SetAutoFocus(autoFocus);
                var graphicsBefore = SnapshotSettings(settings);
                var dofBefore = SnapshotSettings(dof);
                File.Delete(temp); // any Save from here on brings it back

                page.Refresh();
                window.Visible = true; // as Boot's OnOpenPreferences: refresh, then show
                page.Refresh();
                page._Process(1.0); // the readouts, including the texture memory slider under "automatic"
                page._Process(1.0);
                window.Visible = false;
                page.Refresh();

                string what = $"auto-focus {(autoFocus ? "on" : "off")}";
                Expect(Same(graphicsBefore, SnapshotSettings(settings)), $"{what}: the graphics settings changed");
                Expect(Same(dofBefore, SnapshotSettings(dof)), $"{what}: the depth-of-field settings changed (auto-focus now {dof.AutoFocus})");
                Expect(!File.Exists(temp), $"{what}: something was saved");
            }

            // --- Automatic on and off again --------------------------------------------------------------------------------
            dof.SetAutoFocus(true);
            settings.SetTextureMemoryAuto(false);
            settings.SetTextureMemoryMb(3000);
            page.Refresh();
            string beforeAuto = Text();
            var autoBox = Row("graphics.performance.texture_memory_auto").ControlAs<CheckBox>()!;
            window.Visible = true;
            autoBox.ButtonPressed = true;
            page._Process(1.0);
            Expect(settings.TextureMemoryAuto && settings.TextureMemoryMb == 3000, $"Automatic on: manual size is {settings.TextureMemoryMb} MB, expected 3000");
            autoBox.ButtonPressed = false;
            Expect(!settings.TextureMemoryAuto && settings.TextureMemoryMb == 3000, $"Automatic off again: manual size is {settings.TextureMemoryMb} MB, expected 3000");
            Expect(Text() == beforeAuto, "Automatic on and off again left the file different");
            window.Visible = false;

            // --- the Profiles menu ------------------------------------------------------------------------------------------
            const string ProfileName = "selftest-roundtrip-profile";
            var popup = DescendantNodes(Row("graphics.quality.profiles")).OfType<MenuButton>().First().GetPopup();
            var deleteMenu = popup.GetNode<PopupMenu>("DeleteProfileMenu");
            int IdOf(PopupMenu menu, string text)
            {
                for (int i = 0; i < menu.ItemCount; i++)
                {
                    if (menu.GetItemText(i) == text) return menu.GetItemId(i);
                }
                return -1;
            }

            string beforeProfile = Text();
            var stateAtSave = SnapshotSettings(settings);
            int saveAs = IdOf(popup, UI.L10n.Tr("ui.preferences.profile_save_as"));
            Expect(saveAs >= 0, "the Profiles menu has no 'Save current as' entry");
            popup.EmitSignal(PopupMenu.SignalName.IdPressed, (long)saveAs);
            var nameField = DescendantNodes(Row("graphics.quality.profile_save")).OfType<LineEdit>().First();
            Expect(Row("graphics.quality.profile_save").Visible, "'Save current as' did not show the name field");
            nameField.Text = ProfileName;
            nameField.EmitSignal(LineEdit.SignalName.TextSubmitted, ProfileName);
            Expect(UI.GraphicsSettings.GetProfileNames().Contains(ProfileName) && settings.CurrentProfileName == ProfileName, "the profile was not saved");
            Expect(IdOf(popup, ProfileName) >= 0, "the saved profile is not in the Profiles menu");

            settings.SetDrawDistance(300f);
            settings.SetShadowDistance(48f);
            int load = IdOf(popup, ProfileName);
            popup.EmitSignal(PopupMenu.SignalName.IdPressed, (long)load);
            var afterLoad = SnapshotSettings(settings);
            afterLoad.Remove(nameof(UI.GraphicsSettings.CurrentProfileName));
            var expectedAfterLoad = new Dictionary<string, string>(stateAtSave);
            expectedAfterLoad.Remove(nameof(UI.GraphicsSettings.CurrentProfileName));
            Expect(Same(expectedAfterLoad, afterLoad) && settings.CurrentProfileName == ProfileName, "loading the profile did not bring the saved values back");

            int delete = IdOf(deleteMenu, ProfileName);
            Expect(delete >= 0, "the Delete submenu does not list the profile");
            deleteMenu.EmitSignal(PopupMenu.SignalName.IdPressed, (long)delete);
            Expect(!UI.GraphicsSettings.GetProfileNames().Contains(ProfileName) && IdOf(popup, ProfileName) < 0, "the profile was not deleted");
            // Loading the profile saved the settings once more, at the values the profile held: the file reads as it did before.
            Expect(Text() == beforeProfile, "saving, loading and deleting a profile left the file different");

            return problems.Count == 0
                ? new Check(Name, true, "Refresh / _Process / open and close leave the settings and the file alone (auto-focus on and off, off-step values), Automatic on and off keeps 3000 MB, Profiles menu: save as, load, delete")
                : new Check(Name, false, string.Join("; ", problems.Take(10)));
        }
        catch (Exception ex) { return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            UI.GraphicsSettings.ConfigPath = savedGraphics;
            UI.DofSettings.ConfigPath = savedDof;
            PrefsState.ConfigPath = savedPrefs;
            RenderConfig.MaxFullyRenderedAvatars = savedAvatars;
            if (window != null && GodotObject.IsInstanceValid(window)) { window.GetParent()?.RemoveChild(window); window.QueueFree(); }
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>FEAT-UI-80: the Preferences search against the real window. The terms of the acceptance criteria, in both
    /// languages (the language is switched through L10n's manager, which also drives the "index rebuilt on language change"
    /// path); the Advanced fold-outs the search opens and what a person's own click does to them; a hit on a disabled
    /// dependent; the Keyboard filter; the no-match line; Esc; and that nothing is written. Every setting it changes
    /// (the language, Shadows) is put back, and PrefsState and the graphics settings write to temp files meanwhile.</summary>
    private static Check CheckPreferencesSearch(SceneTree tree)
    {
        const string Name = "preferences search";
        var window = FindPreferencesWindow(tree);
        if (window == null) return new Check(Name, false, "the client has no PreferencesWindow");
        var manager = UI.L10n.Manager;
        if (manager == null) return new Check(Name, false, "L10n has no manager");

        string savedPrefs = PrefsState.ConfigPath, savedGraphics = UI.GraphicsSettings.ConfigPath, savedLocale = manager.CurrentLocale;
        string stem = Path.Combine(Path.GetTempPath(), "slng-selftest-search-" + Guid.NewGuid().ToString("N"));
        string temp = stem + ".prefs.cfg", tempSelect = stem + ".select.cfg", tempGraphics = stem + ".graphics.cfg";
        string? originalCategory = window.SelectedCategoryId;
        var sections = window.Categories.SelectMany(c => c.Sections).ToList();
        var foldBefore = sections.Where(s => s.AdvancedFold != null).ToDictionary(s => s, s => s.AdvancedFold!.Folded);
        var problems = new List<string>();
        UI.PreferencesWindow? spare = null;
        string timing = "", facts = "";
        void Expect(bool ok, string what) { if (!ok) problems.Add(what); }

        try
        {
            PrefsState.ConfigPath = temp;
            UI.GraphicsSettings.ConfigPath = tempGraphics;
            foreach (var fold in foldBefore.Keys) fold.AdvancedFold!.Folded = true; // a known start, whatever the person left open

            PrefsRow Row(string id) => window.AllRows.First(r => r.Id == id);
            PrefsSection Section(string id) => sections.First(s => $"{s.CategoryId}.{s.Id}" == id);
            string[] Listed() => window.Categories.Where(c => window.IsCategoryListed(c.Id)).Select(c => c.Id).ToArray();
            string[] Shown(string category) => window.Categories.First(c => c.Id == category).Sections.Where(s => s.Visible).Select(s => s.Id).ToArray();
            string Persisted() => File.Exists(temp) ? File.ReadAllText(temp) : "";
            // SelectCategory remembers its choice; the search must not, so the reset between steps writes elsewhere.
            void SelectQuietly(string? id)
            {
                if (id == null) return;
                string path = PrefsState.ConfigPath;
                PrefsState.ConfigPath = tempSelect;
                window.SelectCategory(id);
                PrefsState.ConfigPath = path;
            }
            var keyboardPage = Row("keyboard.all.keyboard_page").SearchTarget as UI.KeyboardPreferencesPage;
            if (keyboardPage == null) return new Check(Name, false, "the Keyboard block does not implement IPrefsSearchTarget");
            void ExpectClean(string when)
            {
                Expect(!window.IsSearching && window.SearchText.Length == 0 && !window.SearchFoundNothing, $"{when}: the search is still on");
                Expect(Listed().Length == window.Categories.Count, $"{when}: a category button is still hidden ({string.Join(",", Listed())})");
                Expect(sections.All(s => s.Visible), $"{when}: a section is still hidden");
                Expect(!window.AllRows.Any(r => r.IsHighlighted), $"{when}: a row is still tinted");
                Expect(foldBefore.Keys.All(s => s.AdvancedFold!.Folded && !s.IsAdvancedOpenedBySearch), $"{when}: an Advanced fold-out was not folded back");
                Expect(keyboardPage.FilterText.Length == 0, $"{when}: the Keyboard filter still holds '{keyboardPage.FilterText}'");
                Expect(Persisted() == "", $"{when}: the search wrote the preferences file");
            }

            ExpectClean("before");
            Expect(window.SearchField == window.LeftColumn.GetChild(0) && window.SearchField.ClearButtonEnabled
                   && window.SearchField.PlaceholderText == UI.L10n.Tr("ui.preferences.search_placeholder"), "the search field is not the first child of the left column, or lacks its clear button / placeholder");

            // --- the text rules hold in the engine's runtime, not only in the unit tests ------------------------------------
            Expect(SettingsSearch.Normalize("Schärfe") == "scharfe" && SettingsSearch.Normalize("Straße") == "strasse", "diacritics / ß are not folded in the Godot runtime");

            // --- (a) the label of Shadows, in both languages ---------------------------------------------------------------
            // Not "only the shadows section": the German low-VRAM hint mentions "Schatten" (Graphics > Performance > Advanced) and
            // a key action is called "Avatar shadows". Both are real hits; the check holds the result to exactly those.
            foreach (string locale in new[] { "de-DE", "en-US" })
            {
                manager.CurrentLocale = locale; // raises LanguageChanged: the index is rebuilt
                SelectQuietly("general");
                string label = UI.L10n.TrIn(locale, "ui.preferences.shadows") ?? "";
                window.Search(label);
                string what = $"{locale} '{label}'";

                var listed = Listed();
                Expect(listed.Contains("graphics") && listed.All(id => id is "graphics" or "keyboard"), $"{what}: categories listed are {string.Join(",", listed)}");
                var shown = Shown("graphics");
                facts += $"{locale} '{label}' lists {string.Join("+", listed)} / graphics sections {string.Join("+", shown)}; ";
                Expect(shown.Contains("shadows") && shown.All(id => id is "shadows" or "performance"), $"{what}: Graphics sections shown are {string.Join(",", shown)}");
                Expect(!shown.Contains("performance") || Row("graphics.performance.low_vram_caps").IsHighlighted, $"{what}: Performance is shown without a hit in it");
                Expect(Row("graphics.shadows.shadows").IsHighlighted, $"{what}: the Shadows row is not tinted");
                Expect(!Row("graphics.view_detail.draw_distance").IsHighlighted, $"{what}: the draw distance row is tinted");
                // "Schatten-Auflösung" contains the German word; "Shadow resolution" does not contain "Shadows".
                Expect(Row("graphics.shadows.shadow_resolution").IsHighlighted == (locale == "de-DE"), $"{what}: the shadow resolution row's tint is {Row("graphics.shadows.shadow_resolution").IsHighlighted}");
                var fold = Section("graphics.shadows").AdvancedFold;
                Expect(fold is { Folded: false } && Section("graphics.shadows").IsAdvancedOpenedBySearch, $"{what}: the Advanced fold-out of Shadows did not open");
                Expect(window.SelectedCategoryId == "graphics", $"{what}: the selected category is {window.SelectedCategoryId}");
                Expect(PrefsState.LastCategory == null && Persisted() == "", $"{what}: something was written");
                Expect(keyboardPage.FilterText == label.Trim() && keyboardPage.IsActionShown(KeyActionIds.DevAvatarShadows), $"{what}: the Keyboard filter is '{keyboardPage.FilterText}'");

                window.Search("");
                ExpectClean($"{what} cleared");
                Expect(window.SelectedCategoryId == "graphics", $"{what}: clearing changed the selected category to {window.SelectedCategoryId}");
            }

            // A fold-out the person touched meanwhile stays as they left it, and is remembered as usual.
            {
                manager.CurrentLocale = "en-US";
                string label = UI.L10n.TrIn("en-US", "ui.preferences.shadows") ?? "";
                var shadows = Section("graphics.shadows");
                window.Search(label);
                shadows.AdvancedFold!.Fold(); // closed by the person
                window.Search("");
                Expect(shadows.AdvancedFold.Folded && !shadows.IsAdvancedOpenedBySearch, "a fold-out the person closed was opened again by clearing");

                window.Search(label);
                shadows.AdvancedFold.Fold();
                shadows.AdvancedFold.Expand(); // closed and opened again by the person
                window.Search("");
                Expect(!shadows.AdvancedFold.Folded && PrefsState.IsAdvancedOpen("graphics", "shadows"), "a fold-out the person opened was folded back, or not remembered");
                shadows.AdvancedFold.Fold();
                try { File.Delete(temp); } catch (IOException) { }
                ExpectClean("after the person's own clicks");
            }

            // --- (b) debug-setting names and English words under a German UI ---------------------------------------------
            manager.CurrentLocale = "de-DE";
            foreach (var (query, rowId, category) in new[]
            {
                ("RenderFarClip", "graphics.view_detail.draw_distance", "graphics"),
                ("renderfarclip", "graphics.view_detail.draw_distance", "graphics"),
                ("fps", "graphics.performance.fps_limit", "graphics"),
                ("bandwidth", "network_cache.network.max_bandwidth", "network_cache"),
                ("maximum bandwidth", "network_cache.network.max_bandwidth", "network_cache"),
                ("auflosung", "graphics.shadows.shadow_resolution", "graphics"),
                ("Auflösung", "graphics.shadows.shadow_resolution", "graphics"),
                ("vram", "graphics.performance.texture_memory", "graphics"),
            })
            {
                SelectQuietly("general");
                window.Search(query);
                Expect(Row(rowId).IsHighlighted && window.IsCategoryListed(category) && window.SelectedCategoryId == category,
                    $"de-DE '{query}': {rowId} tinted {Row(rowId).IsHighlighted}, {category} listed {window.IsCategoryListed(category)}, selected {window.SelectedCategoryId}");
                window.Search("");
            }
            // An English word is found because English is indexed too, not by chance: a German-only index misses it.
            {
                var bandwidth = Row("network_cache.network.max_bandwidth");
                var german = new PrefsSearchIndex(window.Categories, new[] { "de-DE" });
                var both = new PrefsSearchIndex(window.Categories, new[] { "de-DE", "en-US" });
                var maximum = SettingsSearch.Parse("maximum");
                Expect(!german.Matches(bandwidth, maximum) && both.Matches(bandwidth, maximum), "'maximum' should find the bandwidth row through the English index only");
            }
            ExpectClean("after (b)");

            // --- (c) a hit on a row its switch has disabled shows the switch too --------------------------------------------
            {
                manager.CurrentLocale = savedLocale;
                var box = Row("graphics.shadows.shadows").ControlAs<CheckBox>()!;
                bool shadowsWere = box.ButtonPressed;
                try
                {
                    string distance = UI.L10n.Tr("ui.preferences.shadow_distance");
                    box.ButtonPressed = false;
                    Expect(!Row("graphics.shadows.shadow_distance").IsEnabled, "shadow distance is not disabled with Shadows off");
                    SelectQuietly("general");
                    window.Search(distance);
                    Expect(Row("graphics.shadows.shadow_distance").IsHighlighted && Row("graphics.shadows.shadows").IsHighlighted,
                        $"'{distance}' with Shadows off: distance tinted {Row("graphics.shadows.shadow_distance").IsHighlighted}, the Shadows switch tinted {Row("graphics.shadows.shadows").IsHighlighted}");
                    Expect(Section("graphics.shadows").AdvancedFold is { Folded: false }, $"'{distance}': the Advanced fold-out did not open");
                    window.Search("");

                    box.ButtonPressed = true;
                    window.Search(distance);
                    Expect(Row("graphics.shadows.shadow_distance").IsHighlighted && !Row("graphics.shadows.shadows").IsHighlighted,
                        $"'{distance}' with Shadows on: the switch must not be tinted (distance {Row("graphics.shadows.shadow_distance").IsHighlighted}, switch {Row("graphics.shadows.shadows").IsHighlighted})");
                    window.Search("");
                }
                finally { box.ButtonPressed = shadowsWere; }
                Expect(box.ButtonPressed == shadowsWere, "Shadows was not put back");

                // Depth of field, when the person has it off (the default): its controls are dimmed and the Enable switch is tinted.
                if (!Row("graphics.depth_of_field.focus_range").IsEnabled)
                {
                    window.Search(UI.L10n.Tr("ui.snapshot.dof_range"));
                    Expect(Row("graphics.depth_of_field.focus_range").IsHighlighted && Row("graphics.depth_of_field.enable").IsHighlighted, "a hit on a depth-of-field control did not tint its Enable switch");
                    window.Search("");
                }
                ExpectClean("after (c)");
            }

            // --- (d) the Keyboard category ---------------------------------------------------------------------------------
            {
                manager.CurrentLocale = "de-DE";
                string english = UI.L10n.TrIn("en-US", KeyChordText.ActionLabelKey(KeyActionIds.DevAvatarShadows)) ?? "";
                SelectQuietly("general");
                window.Search(english);
                Expect(window.IsCategoryListed("keyboard") && keyboardPage.FilterText == english.Trim(), $"'{english}' under de-DE: Keyboard listed {window.IsCategoryListed("keyboard")}, its filter is '{keyboardPage.FilterText}'");
                int shownActions = keyboardPage.ListedActionIds.Count(keyboardPage.IsActionShown);
                Expect(keyboardPage.IsActionShown(KeyActionIds.DevAvatarShadows) && shownActions > 0 && shownActions < keyboardPage.ListedActionIds.Count, $"the Keyboard filter shows {shownActions} of {keyboardPage.ListedActionIds.Count} actions");

                window.Search(english + " zzqqxx"); // stops matching: the filter the search set is taken off again
                Expect(!window.IsCategoryListed("keyboard") && keyboardPage.FilterText.Length == 0, $"a query that no longer matches left the Keyboard listed ({window.IsCategoryListed("keyboard")}) or its filter '{keyboardPage.FilterText}'");

                window.Search(english);
                window.Search("");
                ExpectClean("after (d)");
                Expect(keyboardPage.ListedActionIds.All(keyboardPage.IsActionShown), "clearing the search left the Keyboard filtered");
            }

            // --- (e) nothing matches ---------------------------------------------------------------------------------------
            {
                window.Search("qzxqzxqzx");
                bool pagesHidden = window.Categories.All(c => !((ScrollContainer)c.GetParent().GetParent()).Visible);
                Expect(window.SearchFoundNothing && Listed().Length == 0 && pagesHidden, $"no match: line {window.SearchFoundNothing}, categories listed {Listed().Length}, pages hidden {pagesHidden}");
                window.Search("");
                ExpectClean("after (e)");
                var page = (ScrollContainer)window.Categories.First(c => c.Id == window.SelectedCategoryId).GetParent().GetParent();
                Expect(page.Visible, "the selected page did not come back after a search without a hit");
            }

            // --- Esc in the field clears it --------------------------------------------------------------------------------
            {
                window.Search("fps");
                var escape = new InputEventKey { Keycode = Key.Escape, Pressed = true };
                window.SearchField.EmitSignal(Control.SignalName.GuiInput, escape);
                ExpectClean("after Esc");
                window.SearchField.EmitSignal(Control.SignalName.GuiInput, escape); // in an empty field: no error, nothing changes
                ExpectClean("after Esc in an empty field");
            }

            // --- title matches show the whole section / category; hiding the window ends the search ----------------------
            {
                spare = new UI.PreferencesWindow();
                tree.Root.AddChild(spare);
                var alpha = spare.AddCategory("alpha", "ui.preferences.cat_general");
                var beta = spare.AddCategory("beta", "ui.preferences.cat_graphics");
                var one = alpha.AddSection("one", "ui.preferences.sec_general_interface");
                var two = alpha.AddSection("two", "ui.preferences.sec_general_names");
                one.AddCheck("alpha.one.a", "ui.preferences.always_run", false, _ => { });
                two.AddCheck("alpha.two.b", "ui.preferences.head_follows_camera", false, _ => { });
                two.AddSlider("alpha.two.c", "ui.preferences.camera_orbit_speed", 0.1, 3.0, 0.05, 1.0, v => $"{v}", _ => { });
                beta.AddSection("only", null).AddCheck("beta.only.d", "ui.preferences.camera_fov", false, _ => { });

                spare.Search("names above"); // the title of section two
                Expect(spare.IsCategoryListed("alpha") && !spare.IsCategoryListed("beta") && !one.Visible && two.Visible
                       && !alpha.Rows.Any(r => r.IsHighlighted), "a section title that matches should show that section whole, untinted, and hide the rest");
                spare.Search("general"); // the title of category alpha
                Expect(spare.IsCategoryListed("alpha") && !spare.IsCategoryListed("beta") && one.Visible && two.Visible && !alpha.Rows.Any(r => r.IsHighlighted),
                    "a category title that matches should show the whole category");
                spare.Search("always");
                Expect(one.Visible && !two.Visible && alpha.Rows.First().IsHighlighted && spare.SelectedCategoryId == "alpha", "a row label that matches should tint the row and hide the other section");
                spare.Search("");

                spare.Visible = true;
                spare.Search("always");
                spare.Visible = false;
                Expect(!spare.IsSearching && spare.SearchText.Length == 0 && spare.IsCategoryListed("beta") && one.Visible && two.Visible,
                    "hiding the window did not end the search");
            }

            // --- what a keystroke costs ------------------------------------------------------------------------------------
            {
                manager.CurrentLocale = "de-DE";
                var clock = new System.Diagnostics.Stopwatch();
                var each = new List<double>();
                foreach (string typed in new[] { "s", "sc", "sch", "scha", "schat", "schatt", "schatte", "schatten", "schatten d", "schatten di" })
                {
                    clock.Restart();
                    window.Search(typed);
                    each.Add(clock.Elapsed.TotalMilliseconds);
                }
                window.Search("");
                ExpectClean("after typing");
                timing = $"typing 'schatten di' costs {each.Average():0.00} ms per keystroke (worst {each.Max():0.00} ms)";
                Expect(each.Max() < 40, $"a keystroke took {each.Max():0.0} ms");
            }

            return problems.Count == 0
                ? new Check(Name, true, $"{facts}RenderFarClip / fps / bandwidth / Auflösung / vram found, disabled dependents tint their switch, Keyboard filter, no-match line, Esc, title matches, folds restored, nothing written; {timing}")
                : new Check(Name, false, string.Join("; ", problems.Take(12)) + (problems.Count > 12 ? $"; ... {problems.Count - 12} more" : ""));
        }
        catch (Exception ex) { return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            try
            {
                window.Search("");
                manager.CurrentLocale = savedLocale;
                foreach (var (section, folded) in foldBefore) section.AdvancedFold!.Folded = folded;
                PrefsState.ConfigPath = tempSelect;
                if (originalCategory != null) window.SelectCategory(originalCategory);
            }
            finally
            {
                PrefsState.ConfigPath = savedPrefs;
                UI.GraphicsSettings.ConfigPath = savedGraphics;
                if (spare != null && GodotObject.IsInstanceValid(spare)) { spare.GetParent()?.RemoveChild(spare); spare.QueueFree(); }
                foreach (string file in new[] { temp, tempSelect, tempGraphics })
                {
                    try { File.Delete(file); } catch (IOException) { }
                }
            }
        }
    }
}
