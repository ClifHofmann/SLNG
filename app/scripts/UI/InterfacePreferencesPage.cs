using System;
using Godot;
using SLNG.App.UI.Prefs;
using SLNG.Core;
using SLNG.Core.Services;

namespace SLNG.App.UI;

/// <summary>
/// The "Interface" section of the General category (FEAT-UI-79): the language, and the interface scale (FEAT-UI-07,
/// FEAT-UI-42) -- an "automatic" checkbox plus a slider. Bound to <see cref="UiSettings"/>. A builder rather than a
/// Control: it fills the section it is given with <see cref="PrefsRow"/>s.
/// </summary>
public sealed class InterfacePreferencesPage
{
    /// <summary>Fills <paramref name="section"/> (General, "interface"), which Boot has already put in its place. Call
    /// once, after the section is in the window's tree.</summary>
    public void Build(PrefsSection section, UiSettings settings, LocalizationManager locManager)
    {
        string prefix = $"{section.CategoryId}.{section.Id}";

        // --- Language ---
        // Applies to windows opened from now on; a window that is already open keeps the language it was built in.
        var locales = locManager.GetAvailableLocales();
        var labels = new string[locales.Count];
        int selected = 0;
        for (int i = 0; i < locales.Count; i++)
        {
            labels[i] = locales[i];
            if (locales[i] == settings.Language) selected = i;
        }
        section.AddOption($"{prefix}.language", "ui.preferences.language", labels, selected, index =>
            {
                string newLang = locales[index];
                settings.SetLanguage(newLang);
                locManager.CurrentLocale = newLang;
            })
            .WithApply(PrefsApply.ReopenWindows);

        // --- Scale ---
        // FEAT-UI-42: automatic (follows the display) or a chosen number. The slider shows the scale
        // in force either way; it is only editable while automatic is off.
        PrefsRow? sliderRow = null;
        var detected = PrefsStyle.MakeInfo("");
        void ShowDetected() => detected.Text = L10n.TrFormat("ui.preferences.ui_scale_detected", FormatPercent(UiScale.OsScale));

        // The slider's value is shown live but applied on release. Applying every step would resize
        // the very control being dragged, and the handle would chase the cursor.
        bool dragging = false;
        bool syncing = false; // true while the code, not the user, moves the slider

        section.AddCheck($"{prefix}.ui_scale_auto", "ui.preferences.ui_scale_auto", settings.ScaleAutomatic, on =>
            {
                settings.SetScaleAutomatic(on);
                var slider = sliderRow!.ControlAs<HSlider>()!;
                slider.Editable = !on;
                syncing = true;
                slider.Value = settings.Scale; // automatic: the display's; off: the scale just in force
                syncing = false;
                sliderRow.ValueLabel!.Text = FormatPercent(settings.Scale);
                ShowDetected();
            })
            .WithTooltip("ui.preferences.ui_scale_auto_tip");

        ShowDetected();
        section.AddBlock($"{prefix}.ui_scale_detected", detected);

        sliderRow = section.AddSlider($"{prefix}.ui_scale", "ui.preferences.ui_scale",
                UiScalePolicy.Min, UiScalePolicy.Max, UiScalePolicy.Step, settings.Scale,
                v => FormatPercent((float)v),
                v =>
                {
                    if (!dragging && !syncing) settings.SetScale((float)v); // keyboard / click on the track
                },
                hintKey: "ui.preferences.ui_scale_hint")
            .WithKeywords("UIScaleFactor");
        var scaleSlider = sliderRow.ControlAs<HSlider>()!;
        scaleSlider.Editable = !settings.ScaleAutomatic;
        scaleSlider.DragStarted += () => dragging = true;
        scaleSlider.DragEnded += changed =>
        {
            dragging = false;
            if (changed) settings.SetScale((float)scaleSlider.Value);
        };
    }

    private static string FormatPercent(float scale) => $"{Mathf.RoundToInt(scale * 100)}%";
}
