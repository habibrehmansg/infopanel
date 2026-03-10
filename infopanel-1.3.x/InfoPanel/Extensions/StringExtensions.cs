using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace InfoPanel.Extensions
{
    public static partial class StringExtensions
    {
        public static bool IsUrl(this string value)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uriResult))
            {
                return uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps;
            }
            return false;
        }

        private static readonly Regex AlphanumericRegex = AlphaNumericRegex();

        [GeneratedRegex(@"^[a-zA-Z0-9]+$", RegexOptions.Compiled)]
        private static partial Regex AlphaNumericRegex();

        public static bool IsNotAlphanumeric(this string text)
        {
            if (string.IsNullOrEmpty(text))
                return true;

            return !AlphanumericRegex.IsMatch(text);
        }

        public static bool IsAlphanumeric(this string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            return AlphanumericRegex.IsMatch(text);
        }
    }
}
