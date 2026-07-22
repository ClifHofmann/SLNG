using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SLNG.Core.Services
{
    public class LocalizationManager
    {
        private readonly Dictionary<string, Dictionary<string, string>> _locales = new();
        
        public string FallbackLocale { get; set; } = "en-US";
        
        private string _currentLocale = "en-US";
        public string CurrentLocale
        {
            get => _currentLocale;
            set
            {
                if (_currentLocale != value)
                {
                    _currentLocale = value;
                    LanguageChanged?.Invoke();
                }
            }
        }

        public event Action? LanguageChanged;

        public LocalizationManager(string i18nDirectoryPath)
        {
            LoadAllLocales(i18nDirectoryPath);
        }

        public IReadOnlyList<string> GetAvailableLocales()
        {
            return new List<string>(_locales.Keys);
        }

        public Dictionary<string, string> GetDictionaryForLocale(string locale)
        {
            if (_locales.TryGetValue(locale, out var dict))
            {
                return dict;
            }
            return new Dictionary<string, string>();
        }

        public void LoadAllLocales(string directoryPath)
        {
            if (!Directory.Exists(directoryPath)) return;

            foreach (var file in Directory.GetFiles(directoryPath, "*.json"))
            {
                string localeName = Path.GetFileNameWithoutExtension(file);
                try
                {
                    string json = File.ReadAllText(file);
                    using var doc = JsonDocument.Parse(json);
                    var dict = new Dictionary<string, string>();
                    FlattenJsonElement(doc.RootElement, "", dict);
                    _locales[localeName] = dict;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LocalizationManager] Failed to load locale {localeName}: {ex.Message}");
                }
            }
        }

        private void FlattenJsonElement(JsonElement element, string prefix, Dictionary<string, string> dict)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    string newPrefix = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                    FlattenJsonElement(prop.Value, newPrefix, dict);
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                dict[prefix] = element.GetString() ?? "";
            }
        }

        public string Translate(string key)
        {
            if (_locales.TryGetValue(CurrentLocale, out var dict) && dict.TryGetValue(key, out var val))
                return val;
                
            if (_locales.TryGetValue(FallbackLocale, out var fallbackDict) && fallbackDict.TryGetValue(key, out var fallbackVal))
                return fallbackVal;
                
            return $"[{key}]";
        }

        public string TranslateFormat(string key, params object[] args)
        {
            string format = Translate(key);
            try
            {
                return string.Format(format, args);
            }
            catch
            {
                return format;
            }
        }
    }
}
