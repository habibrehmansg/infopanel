using System;
using System.Linq;
using System.Windows;
using Serilog;

namespace InfoPanel.Services
{
    public static class LocalizationService
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(LocalizationService));

        public static void SetLanguage(string languageCode)
        {
            try
            {
                var uri = new Uri($"pack://application:,,,/InfoPanel;component/Resources/Strings/Strings.{languageCode}.xaml", UriKind.Absolute);
                var dict = new ResourceDictionary { Source = uri };
                
                var existing = Application.Current.Resources.MergedDictionaries
                    .FirstOrDefault(d => d.Source?.OriginalString.Contains("Strings/Strings.") == true);
                
                if (existing != null)
                {
                    Application.Current.Resources.MergedDictionaries.Remove(existing);
                }
                
                Application.Current.Resources.MergedDictionaries.Add(dict);
                Logger.Information("Language set to {LanguageCode}", languageCode);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to load language resources for {LanguageCode}", languageCode);
                
                // Fallback to English if loading fails
                if (languageCode != "en")
                {
                    SetLanguage("en");
                }
            }
        }
    }
}
