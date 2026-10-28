using Godot;
using SLNG.App.UI.Prefs;

namespace SLNG.App.UI;

/// <summary>
/// The "Building" section of the General category (FEAT-UI-79; FEAT-UI-04): the build grid's granularity and the rotation
/// snap, the way a paint program exposes its grid. Bound to <see cref="UiSettings"/>. The explanations stay tooltips.
/// </summary>
public sealed class BuildingPreferencesPage
{
    // The listed steps are the ones SL builders actually use; finer than 1 cm is below what the simulator's position
    // precision can hold anyway.
    private static readonly float[] GridSteps = { 0.01f, 0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f };
    private static readonly float[] RotationAngles = { 1f, 5f, 10f, 15f, 22.5f, 30f, 45f, 90f };

    /// <summary>Fills <paramref name="section"/> (General, "building"), which Boot has already put in its place.</summary>
    public void Build(PrefsSection section, UiSettings settings)
    {
        string prefix = $"{section.CategoryId}.{section.Id}";

        section.AddOption($"{prefix}.build_grid", "ui.preferences.build_grid_heading",
                Labels(GridSteps, v => $"{v:0.##} m"), IndexOf(GridSteps, settings.BuildGridSpacing),
                index => settings.SetBuildGridSpacing(GridSteps[index]))
            .WithTooltip("ui.preferences.build_grid_tip")
            .WithKeywords("GridResolution");

        section.AddOption($"{prefix}.rotation_snap", "ui.preferences.build_rotation_snap_heading",
                Labels(RotationAngles, v => $"{v:0.##}°"), IndexOf(RotationAngles, settings.BuildRotationSnapDegrees),
                index => settings.SetBuildRotationSnapDegrees(RotationAngles[index]))
            .WithTooltip("ui.preferences.build_rotation_snap_tip")
            .WithKeywords("RotationStep");
    }

    private static string[] Labels(float[] values, System.Func<float, string> format)
    {
        var labels = new string[values.Length];
        for (int i = 0; i < values.Length; i++) labels[i] = format(values[i]);
        return labels;
    }

    /// <summary>The entry equal to <paramref name="value"/>; the first one when the stored value is none of the listed
    /// steps, which is what a dropdown shows when nothing was selected.</summary>
    private static int IndexOf(float[] values, float value)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (Mathf.IsEqualApprox(values[i], value)) return i;
        }
        return 0;
    }
}
