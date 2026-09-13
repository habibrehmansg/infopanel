using System;
using System.Linq;
using System.Windows;
using Serilog;

namespace InfoPanel.Services
{
    public static class LocalizationService
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(LocalizationService));
        private static string _currentLanguage = "en";

        public static string CurrentLanguage => _currentLanguage;

        /// <summary>Fired whenever the active language changes. Consumers can use this to refresh localized strings.</summary>
        public static event EventHandler? LanguageChanged;

        public static void SetLanguage(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
                languageCode = "en";

            try
            {
                var app = Application.Current;
                if (app == null) return;

                var dictUri = new Uri($"pack://application:,,,/InfoPanel;component/Resources/Strings/Strings.{languageCode}.xaml", UriKind.RelativeOrAbsolute);
                var newDict = new ResourceDictionary { Source = dictUri };

                // Find existing Strings.* dictionary in App resources MergedDictionaries
                var existingDict = app.Resources.MergedDictionaries
                    .FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("/Strings/Strings."));

                if (existingDict != null)
                {
                    var index = app.Resources.MergedDictionaries.IndexOf(existingDict);
                    app.Resources.MergedDictionaries.RemoveAt(index);
                    app.Resources.MergedDictionaries.Insert(index, newDict);
                }
                else
                {
                    app.Resources.MergedDictionaries.Add(newDict);
                }

                _currentLanguage = languageCode;
                Logger.Information("Applied language: {Language}", languageCode);

                LanguageChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to apply language: {Language}", languageCode);
                if (languageCode != "en")
                {
                    SetLanguage("en");
                }
            }
        }

        public static string GetString(string key)
        {
            var app = Application.Current;
            if (app?.Resources.Contains(key) == true && app.Resources[key] is string str)
            {
                return str;
            }
            return key;
        }
    }
}
