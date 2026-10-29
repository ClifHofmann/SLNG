using System;
using System.IO;
using SLNG.Core;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>FEAT-PERF-26: a new install (no saved graphics settings) starts on the preset its machine is
    /// recommended, an existing one keeps what it saved, and only Ultra shadows with four cascades. Runs against a
    /// temp file, not the user's preferences.</summary>
    private static Check CheckGraphicsFirstRun()
    {
        const string Name = "graphics: first start takes the machine's preset, only Ultra has 4 cascades";
        string temp = Path.Combine(Path.GetTempPath(), "slng-selftest-firstrun-" + Guid.NewGuid().ToString("N") + ".cfg");
        string saved = UI.GraphicsSettings.ConfigPath;
        int savedAvatars = RenderConfig.MaxFullyRenderedAvatars; // loading a saved profile sets it
        try
        {
            UI.GraphicsSettings.ConfigPath = temp;

            var fresh = new UI.GraphicsSettings();
            fresh.Load();
            if (!fresh.IsFirstRun) return new Check(Name, false, "no file: IsFirstRun should be true");

            fresh.ApplyRecommended(RecommendedGraphics.High);
            if (fresh.IsFirstRun) return new Check(Name, false, "IsFirstRun still true after applying a preset");
            if (fresh.DetectPreset() != UI.GraphicsPreset.High) return new Check(Name, false, $"High recommended, detected {fresh.DetectPreset()}");
            if (fresh.ShadowSplits != 1) return new Check(Name, false, $"High has ShadowSplits {fresh.ShadowSplits}, expected 1 (2 cascades)");

            var again = new UI.GraphicsSettings();
            again.Load();
            if (again.IsFirstRun) return new Check(Name, false, "saved settings: IsFirstRun should be false");
            if (again.DetectPreset() != UI.GraphicsPreset.High) return new Check(Name, false, "saved High did not load back");

            foreach (var (preset, splits) in new[]
            {
                (UI.GraphicsPreset.Low, 1), (UI.GraphicsPreset.Medium, 1), (UI.GraphicsPreset.High, 1), (UI.GraphicsPreset.Ultra, 2),
            })
            {
                var s = new UI.GraphicsSettings();
                s.ApplyPreset(preset);
                if (s.ShadowSplits != splits) return new Check(Name, false, $"{preset}: ShadowSplits {s.ShadowSplits}, expected {splits}");
                if (s.DetectPreset() != preset) return new Check(Name, false, $"{preset}: detected as {s.DetectPreset()}");
            }

            File.Delete(temp);
            var ultra = new UI.GraphicsSettings();
            ultra.Load();
            ultra.ApplyRecommended(RecommendedGraphics.Ultra);
            if (ultra.ShadowSplits != 2) return new Check(Name, false, "Ultra recommended: expected 4 cascades");

            return new Check(Name, true, "Low/Medium/High 2 cascades, Ultra 4; saved settings are not first-run");
        }
        finally
        {
            UI.GraphicsSettings.ConfigPath = saved;
            RenderConfig.MaxFullyRenderedAvatars = savedAvatars;
            try { File.Delete(temp); } catch (IOException) { }
        }
    }
}
