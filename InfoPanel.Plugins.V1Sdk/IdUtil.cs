using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace InfoPanel.Plugins
{
    internal partial class IdUtil
    {
        [GeneratedRegex(@"[^a-z0-9-]", RegexOptions.IgnoreCase)]
        private static partial Regex AlphaNumericRegex();

        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();

        public static string Encode(string input)
        {
            // Normalize the input string to decompose combined characters into base characters + diacritics
            string normalized = input.Normalize(NormalizationForm.FormD);

            // Use StringBuilder to filter out non-spacing marks (diacritics) and other non-alphanumeric characters
            var sb = new StringBuilder();
            foreach (var c in normalized)
            {
                UnicodeCategory unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark && (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)))
                {
                    sb.Append(c);
                }
            }

            // Convert to lowercase
            string cleaned = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();

            // Replace spaces with hyphens
            string dashed = WhitespaceRegex().Replace(cleaned, "-").Trim('-');

            // Optionally, you can further refine the output by ensuring it matches URL-safe characters only
            // This regex will ensure only alphanumeric and dash remain
            string slug = AlphaNumericRegex().Replace(dashed, "");

            return slug;
        }
    }
}
