using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SLNG.App.UI.Prefs;

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
        ("graphics", new[] { "all" }),
        ("camera_movement", new[] { "camera", "movement", "avatar" }),
        ("chat_im", new[] { "conversations", "friends_online", "away", "chat_logs" }),
        ("keyboard", new[] { "all" }),
        ("network_cache", new[] { "network", "cache" }),
        ("help_info", new[] { "debug_log", "licences" }),
    };

    /// <summary>The window the running client built in SetupHud: seven categories in the plan's order with exactly the
    /// plan's sections, row ids that are unique and shaped <c>category.section.name</c>, every text key present in both
    /// languages, and no old page embedded whole -- bar the Graphics page, the Keyboard table and the Licences notice.</summary>
    private static Check CheckPreferencesWindowWiring(SceneTree tree)
    {
        const string Name = "preferences window wiring";
        try
        {
            var window = tree.Root.FindChild("PreferencesWindow", true, false) as UI.PreferencesWindow;
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
                        bool allowed = category.Id is "graphics" or "keyboard" || (category.Id == "help_info" && section.Id == "licences");
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

            // Spot checks on what the plan says about when a change applies, and where the fold-outs are.
            PrefsRow? Row(string id) => window.AllRows.FirstOrDefault(r => r.Id == id);
            if (Row("general.interface.language")?.Apply != PrefsApply.ReopenWindows) problems.Add("the language row has no 'reopen windows' badge");
            if (Row("chat_im.chat_logs.folder")?.Apply != PrefsApply.NextLogin) problems.Add("the chat log folder row has no 'next login' badge");
            foreach (string id in new[] { "chat_im.chat_logs", "network_cache.cache", "camera_movement.camera" })
            {
                var section = window.Categories.SelectMany(c => c.Sections).FirstOrDefault(x => $"{x.CategoryId}.{x.Id}" == id);
                if (section?.AdvancedFold is not { Folded: true }) problems.Add($"{id} has no collapsed Advanced fold-out");
            }
            foreach (var section in window.Categories.Where(c => c.Id is not "graphics").SelectMany(c => c.Sections))
            {
                string id = $"{section.CategoryId}.{section.Id}";
                bool hasAdvancedRows = section.Rows.Any(r => r.IsAdvanced);
                if (hasAdvancedRows != (section.AdvancedFold != null)) problems.Add($"{id}: Advanced fold-out and advanced rows disagree");
            }

            foreach (var locale in new[] { "en-US", "de-DE" })
            {
                using var file = Godot.FileAccess.Open($"res://i18n/{locale}.json", Godot.FileAccess.ModeFlags.Read);
                if (file == null) { problems.Add($"cannot open {locale}"); continue; }
                var manager = new SLNG.Core.Services.LocalizationManager { FallbackLocale = locale, CurrentLocale = locale };
                manager.LoadLocaleFromJson(locale, file.GetAsText());
                var dict = manager.GetDictionaryForLocale(locale);
                var keys = window.Categories.SelectMany(c => c.Sections.Select(s => s.TitleKey).Append(c.TitleKey)).OfType<string>().ToList();
                foreach (var row in window.AllRows)
                {
                    keys.AddRange(new[] { row.LabelKey, row.HintKey, row.TooltipKey }.OfType<string>());
                    keys.AddRange(row.SearchKeys);
                    keys.AddRange(row.OptionKeys);
                }
                var missing = keys.Distinct().Where(k => !dict.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).ToList();
                if (missing.Count > 0) problems.Add($"{locale} missing {string.Join(", ", missing.Take(8))}");
            }

            return problems.Count == 0
                ? new Check(Name, true, $"{ids.Count} categories with the plan's {PreferenceSectionPlan.Sum(p => p.Sections.Length)} sections, {window.AllRows.Count()} registered rows, ids unique, only Graphics, Keyboard and Licences are blocks of old pages")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex) { return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
    }
}
