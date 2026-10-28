using Godot;
using SLNG.Core.Services;

namespace SLNG.App.UI
{
    public static class L10n
    {
        private static LocalizationManager? _manager;

        public static void Initialize(LocalizationManager manager)
        {
            _manager = manager;
            
            // Sync all locales to Godot's TranslationServer
            foreach (var locale in _manager.GetAvailableLocales())
            {
                var translation = new Godot.Translation();
                translation.Locale = locale;
                
                var dict = _manager.GetDictionaryForLocale(locale);
                foreach (var kvp in dict)
                {
                    translation.AddMessage(kvp.Key, kvp.Value);
                }
                
                TranslationServer.AddTranslation(translation);
            }
            
            _manager.LanguageChanged += OnLanguageChanged;
            
            // Set initial locale
            OnLanguageChanged();
        }

        private static void OnLanguageChanged()
        {
            if (_manager != null)
            {
                TranslationServer.SetLocale(_manager.CurrentLocale);
            }
            LanguageChanged?.Invoke();
        }

        /// <summary>Raised on the main thread after the current language changed. A window that keeps text derived from
        /// the language (the Preferences search index) rebuilds it here; unsubscribe when the node leaves the tree.</summary>
        public static event System.Action? LanguageChanged;

        /// <summary>The language <see cref="Tr"/> answers in now (en-US before <see cref="Initialize"/>).</summary>
        public static string CurrentLocale => _manager?.CurrentLocale ?? "en-US";

        /// <summary>The manager <see cref="Initialize"/> was given. For the selftest, which switches the language to check
        /// what follows from it; the client changes it only through the language row of Preferences.</summary>
        internal static LocalizationManager? Manager => _manager;

        public static string Tr(string key)
        {
            return _manager?.Translate(key) ?? $"[{key}]";
        }

        /// <summary>The text of <paramref name="key"/> in <paramref name="locale"/> itself, or null when that language has
        /// none: no fallback to another language and no "[key]" placeholder. For an index over several languages.</summary>
        public static string? TrIn(string locale, string key)
        {
            return _manager?.GetStringIn(locale, key);
        }

        public static string TrFormat(string key, params object[] args)
        {
            return _manager?.TranslateFormat(key, args) ?? $"[{key}]";
        }
    }
}
