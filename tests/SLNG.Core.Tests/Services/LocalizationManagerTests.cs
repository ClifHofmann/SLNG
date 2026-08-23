using System.IO;
using SLNG.Core.Services;
using Xunit;

namespace SLNG.Core.Tests.Services
{
    public class LocalizationManagerTests
    {
        [Fact]
        public void LoadAndFlattenJson_SuccessfullyTranslates()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "slng_i18n_test_" + System.Guid.NewGuid());
            Directory.CreateDirectory(testDir);
            try
            {
                File.WriteAllText(Path.Combine(testDir, "en-US.json"), """
                {
                    "ui": {
                        "button": "Save",
                        "greet": "Hello {0}!"
                    }
                }
                """);

                File.WriteAllText(Path.Combine(testDir, "de-DE.json"), """
                {
                    "ui": {
                        "button": "Speichern"
                    }
                }
                """);

                var manager = new LocalizationManager(testDir);
                Assert.Contains("en-US", manager.GetAvailableLocales());
                Assert.Contains("de-DE", manager.GetAvailableLocales());

                manager.CurrentLocale = "en-US";
                Assert.Equal("Save", manager.Translate("ui.button"));
                Assert.Equal("Hello Alice!", manager.TranslateFormat("ui.greet", "Alice"));

                // Fallback test
                manager.CurrentLocale = "de-DE";
                Assert.Equal("Speichern", manager.Translate("ui.button"));
                Assert.Equal("Hello Bob!", manager.TranslateFormat("ui.greet", "Bob")); // Should fallback to en-US

                // Missing key fallback test
                Assert.Equal("[missing.key]", manager.Translate("missing.key"));
            }
            finally
            {
                if (Directory.Exists(testDir))
                    Directory.Delete(testDir, true);
            }
        }
    }
}
