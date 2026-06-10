using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace SwiftList.Tutorial.Services
{
    public static class TutorialTranslationService
    {
        private static readonly Dictionary<string, string> Translations = new(StringComparer.OrdinalIgnoreCase);

        static TutorialTranslationService()
        {
            LoadTranslations();
        }

        private static void LoadTranslations()
        {
            string culture = System.Globalization.CultureInfo.CurrentUICulture.Name;
            // Fallback strategy: if it's zh-CN, zh-HK, zh-TW, etc. use zh-CN, otherwise default to en-US.
            string targetCulture = culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";

            string resourceCulture = targetCulture.Replace('-', '_');

            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = $"SwiftList.Tutorial.Resources.Translations.{resourceCulture}.Tutorial.json";

            try
            {
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        using (var reader = new StreamReader(stream))
                        {
                            string json = reader.ReadToEnd();
                            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                            if (dict != null)
                            {
                                foreach (var kvp in dict)
                                {
                                    Translations[kvp.Key] = kvp.Value;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public static string Get(string key)
        {
            if (Translations.TryGetValue(key, out var val))
                return val;
            return $"[{key}]";
        }

        public static string Format(string key, params object[] args)
        {
            string fmt = Get(key);
            try
            {
                return string.Format(fmt, args);
            }
            catch
            {
                return fmt;
            }
        }
    }
}
