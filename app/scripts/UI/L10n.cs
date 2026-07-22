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
        }

        public static string Tr(string key)
        {
            return _manager?.Translate(key) ?? $"[{key}]";
        }

        public static string TrFormat(string key, params object[] args)
        {
            return _manager?.TranslateFormat(key, args) ?? $"[{key}]";
        }
    }
}
