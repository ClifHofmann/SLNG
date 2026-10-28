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

        private static LocalizationManager TwoLanguages()
        {
            var manager = new LocalizationManager();
            manager.LoadLocaleFromJson("en-US", """{ "ui": { "button": "Save", "only_english": "English only" } }""");
            manager.LoadLocaleFromJson("de-DE", """{ "ui": { "button": "Speichern" } }""");
            return manager;
        }

        [Fact]
        public void GetStringIn_ReturnsTheTextOfThatLocale()
        {
            var manager = TwoLanguages();
            manager.CurrentLocale = "en-US"; // the current language does not decide which text comes back

            Assert.Equal("Speichern", manager.GetStringIn("de-DE", "ui.button"));
            Assert.Equal("Save", manager.GetStringIn("en-US", "ui.button"));
        }

        [Fact]
        public void GetStringIn_MissingKey_IsNull()
        {
            var manager = TwoLanguages();

            Assert.Null(manager.GetStringIn("en-US", "ui.nope"));
            Assert.Null(manager.GetStringIn("de-DE", "ui.nope"));
        }

        [Fact]
        public void GetStringIn_MissingLocale_IsNull()
        {
            var manager = TwoLanguages();

            Assert.Null(manager.GetStringIn("fr-FR", "ui.button"));
        }

        [Fact]
        public void GetStringIn_DoesNotFallBackToTheFallbackLocale()
        {
            var manager = TwoLanguages();
            Assert.Equal("English only", manager.GetStringIn("en-US", "ui.only_english"));

            // Translate() answers this from en-US while German is current; GetStringIn must say the German text does not exist.
            manager.CurrentLocale = "de-DE";
            Assert.Equal("English only", manager.Translate("ui.only_english"));
            Assert.Null(manager.GetStringIn("de-DE", "ui.only_english"));
        }
    }
}
