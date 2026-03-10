using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Extensions
{
    public static class DictionaryExtensions
    {
        public static string GetStringValue(this Dictionary<string, string> dict, string key, string fallback)
        {
            return dict.TryGetValue(key, out var value) ? value : fallback;
        }

        public static int GetIntValue(this Dictionary<string, string> dict, string key, int fallback)
        {
            return dict.TryGetValue(key, out var value) 
                && double.TryParse(value, out var doubleResult) ? (int)Math.Round(doubleResult) : fallback;
        }
    }
}
